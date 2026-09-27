using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.State;
using FirstmateTelegram.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;

namespace FirstmateTelegram.Bridge;

/// <summary>
/// The <c>getUpdates</c> loop. Updates are handled in order, and Telegram's position advances only over an unbroken
/// run of handled updates, after the request's note is saved and the bridge state is written. A request that cannot
/// be saved holds the position; later commands are still answered and remembered, and later requests wait behind it.
/// </summary>
public sealed class UpdatePoller : BackgroundService
{
    public const int LongPollSeconds = 50;
    const int NotifyAfterAttempts = 3;
    static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    static readonly TimeSpan LastRetry = TimeSpan.FromMinutes(5);
    static readonly TimeSpan BlockedPollInterval = TimeSpan.FromSeconds(5);

    readonly TelegramGateway _telegram;
    readonly StateStore _store;
    readonly AccessGate _gate;
    readonly CommandRouter _commands;
    readonly RequestSubmitter _submitter;
    readonly TimeProvider _time;
    readonly ILogger<UpdatePoller> _logger;
    BotIdentity? _bot;
    BlockedRequest? _blocked;

    public UpdatePoller(
        TelegramGateway telegram,
        StateStore store,
        AccessGate gate,
        CommandRouter commands,
        RequestSubmitter submitter,
        TimeProvider time,
        ILogger<UpdatePoller> logger)
    {
        _telegram = telegram;
        _store = store;
        _gate = gate;
        _commands = commands;
        _submitter = submitter;
        _time = time;
        _logger = logger;
    }

    enum Outcome
    {
        Handled,
        Waiting,
    }

    /// <summary>A request that could not be saved yet, with its retry schedule.</summary>
    sealed record BlockedRequest(int UpdateId, int Failures, DateTimeOffset NextAttemptAt, bool UserTold);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InitializeAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken);
            await WaitWhileBlockedAsync(stoppingToken);
        }
    }

    /// <summary>Learns the bot's identity. A different bot than the one in the saved state starts from Telegram's beginning.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var me = await _telegram.GetMeAsync(cancellationToken);
        _bot = new BotIdentity(me.Id, me.Username);
        if (_store.State.BotId != me.Id)
            await _store.UpdateStateAsync(state => state with { BotId = me.Id, UpdateOffset = 0, HandledUpdateIds = [] }, cancellationToken);
        _logger.LogInformation("polling for bot {BotId} from update offset {Offset}", me.Id, _store.State.UpdateOffset);
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        await _submitter.RepairWakesAsync(cancellationToken);
        var timeout = _blocked is null ? LongPollSeconds : 0;
        var updates = await _telegram.GetUpdatesAsync(_store.State.UpdateOffset, timeout, cancellationToken);
        await HandleBatchAsync(updates, cancellationToken);
    }

    async Task HandleBatchAsync(IEnumerable<Update> updates, CancellationToken cancellationToken)
    {
        var state = _store.State;
        var offset = state.UpdateOffset;
        var handledAhead = state.HandledUpdateIds.ToHashSet();
        var isUnbroken = true;
        var isRequestWaiting = false;
        var batch = updates.Where(update => update.Id >= state.UpdateOffset).OrderBy(update => update.Id).ToList();

        // Telegram drops an update after 24 hours; a blocked request that is no longer delivered stops blocking.
        if (_blocked is { } blocked && batch.All(update => update.Id != blocked.UpdateId))
            _blocked = null;

        foreach (var update in batch)
        {
            var outcome = handledAhead.Contains(update.Id)
                ? Outcome.Handled
                : await HandleAsync(update, isRequestWaiting, cancellationToken);
            if (outcome == Outcome.Waiting)
                isRequestWaiting = true;

            if (isUnbroken && outcome == Outcome.Handled)
            {
                offset = update.Id + 1;
                continue;
            }

            isUnbroken = false;
            if (outcome == Outcome.Handled)
                handledAhead.Add(update.Id);
        }

        handledAhead.RemoveWhere(id => id < offset);
        if (offset == state.UpdateOffset && handledAhead.SetEquals(state.HandledUpdateIds))
            return;

        var handled = handledAhead.Order().ToArray();
        await _store.UpdateStateAsync(current => current with { UpdateOffset = offset, HandledUpdateIds = handled }, cancellationToken);
    }

    async Task<Outcome> HandleAsync(Update update, bool isRequestWaiting, CancellationToken cancellationToken)
    {
        if (!_gate.Admit(update))
            return Outcome.Handled;

        var message = update.Message!;
        if (message.Text is not { } text)
        {
            await _telegram.TrySendTextAsync(message.Chat.Id, BridgeTexts.OnlyText, message.Id, cancellationToken);
            return Outcome.Handled;
        }

        var command = CommandParser.Parse(text, _bot!.Username);
        if (command is not null && CommandRouter.IsBridgeCommand(command.Name))
        {
            // A live ping becomes an inbox note, so it keeps the requests' order behind one that could not be saved yet.
            if (CommandRouter.SendsANote(command) && WaitsForNoteSlot(update.Id, isRequestWaiting))
                return Outcome.Waiting;

            var answer = await _commands.AnswerAsync(message, command, _bot, cancellationToken);
            if (answer.NoteSaveFailure is { } failure)
            {
                await RecordFailureAsync(update, message, failure, cancellationToken);
                return Outcome.Waiting;
            }

            return Outcome.Handled;
        }

        // Requests reach FirstMate in the order they were sent, so a later one waits behind a blocked one.
        if (WaitsForNoteSlot(update.Id, isRequestWaiting))
            return Outcome.Waiting;

        var result = await _submitter.SubmitAsync(message, text, _bot, cancellationToken);
        if (result.IsSaved)
        {
            _blocked = null;
            return Outcome.Handled;
        }

        await RecordFailureAsync(update, message, result.Failure!, cancellationToken);
        return Outcome.Waiting;
    }

    bool WaitsForNoteSlot(int updateId, bool isRequestWaiting) =>
        isRequestWaiting || (_blocked is { } blocked && blocked.UpdateId == updateId && _time.GetUtcNow() < blocked.NextAttemptAt);

    async Task RecordFailureAsync(Update update, Message message, CallFailure failure, CancellationToken cancellationToken)
    {
        var previous = _blocked?.UpdateId == update.Id ? _blocked : null;
        var failures = (previous?.Failures ?? 0) + 1;
        var delay = Backoff.Exponential(failures, FirstRetry, LastRetry);
        var isUserTold = previous?.UserTold ?? false;
        _logger.LogWarning(
            "message {MessageId} not saved (attempt {Failures}: {Failure}, exit {ExitCode}); retrying in {Seconds} s",
            message.Id,
            failures,
            failure.Kind,
            failure.ExitCode,
            delay.TotalSeconds);

        if (failures >= NotifyAfterAttempts && !isUserTold)
        {
            await _telegram.TrySendTextAsync(message.Chat.Id, BridgeTexts.StillTrying(failure.Reason), message.Id, cancellationToken);
            isUserTold = true;
        }

        _blocked = new BlockedRequest(update.Id, failures, _time.GetUtcNow() + delay, isUserTold);
    }

    async Task WaitWhileBlockedAsync(CancellationToken cancellationToken)
    {
        if (_blocked is not { } blocked)
            return;

        var untilRetry = blocked.NextAttemptAt - _time.GetUtcNow();
        var wait = untilRetry < BlockedPollInterval ? untilRetry : BlockedPollInterval;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, _time, cancellationToken);
    }
}
