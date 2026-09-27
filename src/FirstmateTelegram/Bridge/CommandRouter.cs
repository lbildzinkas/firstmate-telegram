using System.Globalization;
using FirstmateTelegram.FirstMate;
using FirstmateTelegram.State;
using FirstmateTelegram.Telegram;
using Telegram.Bot.Types;

namespace FirstmateTelegram.Bridge;

public sealed record ChatCommand(string Name, string Arguments);

public static class CommandParser
{
    /// <summary>
    /// Reads a leading <c>/command</c>, case-insensitively, also in Telegram's <c>/command@BotName</c> form for this bot.
    /// Returns null for anything that is not a command addressed to this bot.
    /// </summary>
    public static ChatCommand? Parse(string text, string? botUsername)
    {
        if (!text.StartsWith('/'))
            return null;

        var end = 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;

        var word = text[1..end];
        var at = word.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            var mention = word[(at + 1)..];
            if (botUsername is null || !mention.Equals(botUsername, StringComparison.OrdinalIgnoreCase))
                return null;
            word = word[..at];
        }

        if (word.Length == 0)
            return null;
        return new ChatCommand(word.ToLowerInvariant(), text[end..].Trim());
    }
}

/// <summary>The outcome of a bridge command. A live ping whose note could not be saved carries the failure, so the poller retries it like a request.</summary>
public sealed record CommandAnswer(CallFailure? NoteSaveFailure = null, bool StopsBridge = false)
{
    public static CommandAnswer Handled { get; } = new();
}

/// <summary>
/// The bridge's own commands (spec 4.3): <c>/status</c> and <c>/ping</c> are answered from records and checks,
/// <c>/mute</c>, <c>/unmute</c>, <c>/back</c>, <c>/stop</c> and <c>/help</c> are answered here too. Anything else,
/// including an unknown <c>/word</c>, is a request for FirstMate.
/// </summary>
public sealed class CommandRouter
{
    /// <summary>Commands the bridge answers itself, including the ones a later version adds.</summary>
    public static readonly IReadOnlySet<string> Commands = new HashSet<string>(StringComparer.Ordinal)
    {
        "status", "ping", "mute", "unmute", "back", "stop", "help",
    };

    readonly TelegramGateway _telegram;
    readonly AvailabilityReader _availability;
    readonly StatusRenderer _status;
    readonly RequestSubmitter _submitter;
    readonly StateStore _store;
    readonly TimeProvider _time;
    readonly TimeSpan _livePingTimeout;

    public CommandRouter(
        TelegramGateway telegram,
        AvailabilityReader availability,
        StatusRenderer status,
        RequestSubmitter submitter,
        StateStore store,
        TimeProvider time,
        TimeSpan livePingTimeout)
    {
        _telegram = telegram;
        _availability = availability;
        _status = status;
        _submitter = submitter;
        _store = store;
        _time = time;
        _livePingTimeout = livePingTimeout;
    }

    public static bool IsBridgeCommand(string name) => Commands.Contains(name);

    /// <summary>True for the commands that save a note in FirstMate's inbox, so they keep the requests' order: <c>/ping live</c>, and <c>/back</c> when FirstMate has the return hook.</summary>
    public static bool SendsANote(ChatCommand command) =>
        (command.Name == "ping" && command.Arguments.Equals("live", StringComparison.OrdinalIgnoreCase))
        || command.Name == "back";

    /// <summary>Answers <paramref name="command"/>; the poller calls this only for the bridge's own commands.</summary>
    public async Task<CommandAnswer> AnswerAsync(Message message, ChatCommand command, BotIdentity bot, CancellationToken cancellationToken)
    {
        switch (command.Name)
        {
            case "status":
                await SendAsync(message, await _status.RenderAsync(cancellationToken), cancellationToken);
                return CommandAnswer.Handled;

            case "ping":
                return await AnswerPingAsync(message, command, bot, cancellationToken);

            case "mute":
                return await AnswerMuteAsync(message, command, cancellationToken);

            case "unmute":
                await _store.UpdateStateAsync(state => state with { MuteUntil = null }, cancellationToken);
                await SendAsync(message, BridgeTexts.Unmuted, cancellationToken);
                return CommandAnswer.Handled;

            case "back":
                return await AnswerBackAsync(message, bot, cancellationToken);

            case "stop":
                await SendAsync(message, BridgeTexts.Stopped, cancellationToken);
                return new CommandAnswer(StopsBridge: true);

            case "help":
                await SendAsync(message, BridgeTexts.Help, cancellationToken);
                return CommandAnswer.Handled;

            default:
                await SendAsync(message, BridgeTexts.NotAvailableYet(command.Name), cancellationToken);
                return CommandAnswer.Handled;
        }
    }

    async Task<CommandAnswer> AnswerPingAsync(Message message, ChatCommand command, BotIdentity bot, CancellationToken cancellationToken)
    {
        var report = await _availability.ReadAsync(cancellationToken);

        // A plain /ping is answered by the bridge alone; /ping live is a real round trip through FirstMate.
        if (!SendsANote(command))
        {
            await SendAsync(message, report.Answer(), cancellationToken);
            return CommandAnswer.Handled;
        }

        if (report.Readiness is Readiness.NotRunning or Readiness.NotPickingUp)
        {
            await SendAsync(message, report.Answer() + "\n" + BridgeTexts.NoLiveCheckSent, cancellationToken);
            return CommandAnswer.Handled;
        }

        var saved = await _submitter.SubmitLivePingAsync(message, bot, report.PostureState == "away", _livePingTimeout, cancellationToken);
        return saved.IsSaved ? CommandAnswer.Handled : new CommandAnswer(saved.Failure);
    }

    async Task<CommandAnswer> AnswerMuteAsync(Message message, ChatCommand command, CancellationToken cancellationToken)
    {
        var duration = MuteDurations.Parse(command.Arguments);
        if (duration is null)
        {
            await SendAsync(message, BridgeTexts.MuteUsage, cancellationToken);
            return CommandAnswer.Handled;
        }

        var until = _time.GetUtcNow() + duration.Value;
        await _store.UpdateStateAsync(state => state with { MuteUntil = until }, cancellationToken);
        var local = TimeZoneInfo.ConvertTime(until, _time.LocalTimeZone);
        var when = duration.Value >= TimeSpan.FromDays(1)
            ? local.ToString("ddd HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("HH:mm", CultureInfo.InvariantCulture);
        await SendAsync(message, BridgeTexts.MutedUntil(when), cancellationToken);
        return CommandAnswer.Handled;
    }

    /// <summary>
    /// The explicit return (spec 4.3.6): quiet mode and away-off answer at once, while away mode on today's
    /// FirstMate, which has no return hook, says so without saving anything (spec 7.2.8). The hook, once it
    /// exists, is checked on every call.
    /// </summary>
    async Task<CommandAnswer> AnswerBackAsync(Message message, BotIdentity bot, CancellationToken cancellationToken)
    {
        var posture = await _availability.ReadPostureAsync(cancellationToken);
        if (posture.Failure is { } failure)
        {
            await SendAsync(message, BridgeTexts.RecordsUnreadable(failure.Reason), cancellationToken);
            return CommandAnswer.Handled;
        }

        switch (posture.PostureState)
        {
            case "quiet":
                await SendAsync(message, BridgeTexts.QuietModeNotAway, cancellationToken);
                return CommandAnswer.Handled;

            case "away":
                var saved = await _submitter.SubmitReturnAsync(message, bot, cancellationToken);
                if (saved.NoHook)
                {
                    await SendAsync(message, BridgeTexts.BackNeedsNewerFirstMate, cancellationToken);
                    return CommandAnswer.Handled;
                }

                return saved.Result!.IsSaved ? CommandAnswer.Handled : new CommandAnswer(saved.Result.Failure);

            default:
                await SendAsync(message, BridgeTexts.AwayNotOn, cancellationToken);
                return CommandAnswer.Handled;
        }
    }

    async Task SendAsync(Message message, string text, CancellationToken cancellationToken) =>
        await _telegram.TrySendTextAsync(message.Chat.Id, text, message.Id, cancellationToken);
}
