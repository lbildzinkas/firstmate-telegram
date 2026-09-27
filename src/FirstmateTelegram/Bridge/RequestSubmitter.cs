using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.State;
using FirstmateTelegram.Telegram;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;

namespace FirstmateTelegram.Bridge;

public sealed record BotIdentity(long Id, string? Username);

public sealed record SubmitResult(string? NoteId, CallFailure? Failure)
{
    public bool IsSaved => NoteId is not null;
}

/// <summary>The outcome of a <c>/back</c>: FirstMate lacks the explicit-return hook, or the return saved (or failed) like a request.</summary>
public sealed record ReturnSubmitResult(bool NoHook, SubmitResult? Result);

/// <summary>
/// Saves a request as a FirstMate inbox note, records it in the request map, reacts 👀, says once when FirstMate
/// is not running or not picking up requests, and repairs the wake for notes saved without one. A live ping goes
/// through the same path with its own body and a deadline instead of the queued notice.
/// </summary>
public sealed class RequestSubmitter
{
    readonly FirstMateClient _firstMate;
    readonly TelegramGateway _telegram;
    readonly StateStore _store;
    readonly WakeSignal _replyWaiting;
    readonly TimeProvider _time;
    readonly ILogger<RequestSubmitter> _logger;

    public RequestSubmitter(FirstMateClient firstMate, TelegramGateway telegram, StateStore store, WakeSignal replyWaiting, TimeProvider time, ILogger<RequestSubmitter> logger)
    {
        _firstMate = firstMate;
        _telegram = telegram;
        _store = store;
        _replyWaiting = replyWaiting;
        _time = time;
        _logger = logger;
    }

    public async Task<SubmitResult> SubmitAsync(Message message, string text, BotIdentity bot, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var ready = await _firstMate.ReadReadyAsync(cancellationToken);
        var requestId = RequestIds.For(bot.Id, chatId, message.Id);

        // A reply to one of the bridge's alerts is a request tied to that alert, so it carries the alert's text.
        var alert = AlertQuotedBy(message, bot);
        var saved = await _firstMate.SaveNoteAsync(requestId, NoteBody.Build(text, ready.IsAway, alert), cancellationToken);
        if (!saved.IsSaved)
            return new SubmitResult(null, saved.Failure);

        var noteId = saved.NoteId!;
        await RecordAsync(message, bot, requestId, noteId, alert is null ? RequestKinds.Request : RequestKinds.AlertReply, saved.Status == NoteSaveStatus.Saved, _time.GetUtcNow(), deadline: null, cancellationToken);
        _logger.LogInformation(
            "saved message {MessageId} as note {NoteId} ({Outcome}{Wake}{AlertReply})",
            message.Id,
            noteId,
            saved.Outcome,
            saved.Status == NoteSaveStatus.Saved ? "" : ", not announced",
            alert is null ? "" : ", an alert reply");
        _replyWaiting.Set();

        await _telegram.TrySetReactionAsync(chatId, message.Id, BotReactions.Received, cancellationToken);
        var notice = ReadinessRules.Classify(ready) switch
        {
            Readiness.NotRunning => BridgeTexts.QueuedNotRunning,
            Readiness.NotPickingUp => BridgeTexts.QueuedNotPickingUp,
            _ => null,
        };
        if (notice is not null)
            await _telegram.TrySendTextAsync(chatId, notice, message.Id, cancellationToken);
        return new SubmitResult(noteId, null);
    }

    /// <summary>The alert a message replies to, when it replies to one of the bridge's own alert messages (spec 4.2).</summary>
    AlertQuote? AlertQuotedBy(Message message, BotIdentity bot)
    {
        if (message.ReplyToMessage is not { From.Id: var sender, Id: var repliedId } || sender != bot.Id)
            return null;

        var alert = _store.Alerts.Values.FirstOrDefault(entry => entry.TelegramMessageId == repliedId);
        if (alert is null)
            return null;
        var local = TimeZoneInfo.ConvertTime(alert.At, _time.LocalTimeZone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return new AlertQuote(local, alert.Text);
    }

    /// <summary>Saves the live ping's short availability-check note and tells the user FirstMate is being asked directly.</summary>
    public async Task<SubmitResult> SubmitLivePingAsync(Message message, BotIdentity bot, bool isAway, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var requestId = RequestIds.For(bot.Id, chatId, message.Id);
        var saved = await _firstMate.SaveNoteAsync(requestId, NoteBody.Build(BridgeTexts.LivePingBody, isAway), cancellationToken);
        if (!saved.IsSaved)
            return new SubmitResult(null, saved.Failure);

        var noteId = saved.NoteId!;
        // One timestamp for the save time and the deadline, so the timeout the forwarder later tells the user
        // is exactly the timeout that was asked for, never a second less to clock jitter between two reads.
        var savedAt = _time.GetUtcNow();
        await RecordAsync(message, bot, requestId, noteId, RequestKinds.LivePing, saved.Status == NoteSaveStatus.Saved, savedAt, savedAt + timeout, cancellationToken);
        _logger.LogInformation(
            "saved the live ping for message {MessageId} as note {NoteId} ({Outcome}{Wake})",
            message.Id,
            noteId,
            saved.Outcome,
            saved.Status == NoteSaveStatus.Saved ? "" : ", not announced");
        _replyWaiting.Set();

        await _telegram.TrySetReactionAsync(chatId, message.Id, BotReactions.Received, cancellationToken);
        await _telegram.TrySendTextAsync(chatId, BridgeTexts.AskingFirstMate((int)Math.Round(timeout.TotalSeconds)), message.Id, cancellationToken);
        return new SubmitResult(noteId, null);
    }

    /// <summary>
    /// Saves the explicit return <c>/back</c> needs, when FirstMate has the hook (spec 7.2.8): the hook's note,
    /// reaction and repair behave like a request's, and FirstMate's return summary arrives as that note's reply.
    /// </summary>
    public async Task<ReturnSubmitResult> SubmitReturnAsync(Message message, BotIdentity bot, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var ready = await _firstMate.ReadReadyAsync(cancellationToken);
        var requestId = RequestIds.For(bot.Id, chatId, message.Id);
        var saved = await _firstMate.SaveReturnAsync(requestId, cancellationToken);
        if (saved.Outcome == ReturnOutcome.NoHook)
            return new ReturnSubmitResult(NoHook: true, null);
        if (!saved.IsSaved)
            return new ReturnSubmitResult(false, new SubmitResult(null, saved.Failure));

        var noteId = saved.NoteId!;
        await RecordAsync(message, bot, requestId, noteId, RequestKinds.Return, saved.Outcome == ReturnOutcome.Saved, _time.GetUtcNow(), deadline: null, cancellationToken);
        _logger.LogInformation(
            "saved the return for message {MessageId} as note {NoteId} ({Outcome})",
            message.Id,
            noteId,
            saved.SavedOutcome);
        _replyWaiting.Set();

        await _telegram.TrySetReactionAsync(chatId, message.Id, BotReactions.Received, cancellationToken);
        await _telegram.TrySendTextAsync(chatId, BridgeTexts.BackSaved, message.Id, cancellationToken);
        var notice = ReadinessRules.Classify(ready) switch
        {
            Readiness.NotRunning => BridgeTexts.QueuedNotRunning,
            Readiness.NotPickingUp => BridgeTexts.QueuedNotPickingUp,
            _ => null,
        };
        if (notice is not null)
            await _telegram.TrySendTextAsync(chatId, notice, message.Id, cancellationToken);
        return new ReturnSubmitResult(false, new SubmitResult(noteId, null));
    }

    async Task RecordAsync(Message message, BotIdentity bot, string requestId, string noteId, string kind, bool announced, DateTimeOffset savedAt, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        await _store.UpdateRequestsAsync(
            requests => requests.TryGetValue(noteId, out var existing)
                ? requests.SetItem(noteId, existing with { Announced = existing.Announced || announced })
                : requests.SetItem(noteId, new RequestEntry
                {
                    NoteId = noteId,
                    RequestId = requestId,
                    ChatId = chatId,
                    MessageId = message.Id,
                    Kind = kind,
                    SavedAt = savedAt,
                    Announced = announced,
                    LiveDeadlineAt = deadline,
                }),
            cancellationToken);
    }

    /// <summary>Runs <c>announce</c> for every note that was saved without waking FirstMate. It never saves a second note.</summary>
    public async Task RepairWakesAsync(CancellationToken cancellationToken)
    {
        var unannounced = _store.Requests.Values
            .Where(entry => !entry.Announced && (entry.IsWaitingForReply || entry.IsLivePingOpen))
            .ToList();
        foreach (var entry in unannounced)
        {
            var repair = await _firstMate.AnnounceAsync(entry.NoteId, cancellationToken);
            if (!repair.Repaired)
            {
                _logger.LogWarning("wake repair for note {NoteId} failed ({Failure}, exit {ExitCode})", entry.NoteId, repair.Failure?.Kind, repair.Failure?.ExitCode);
                continue;
            }

            await _store.UpdateRequestsAsync(
                requests => requests.TryGetValue(entry.NoteId, out var current)
                    ? requests.SetItem(entry.NoteId, current with { Announced = true })
                    : requests,
                cancellationToken);
            _logger.LogInformation("repaired the wake for note {NoteId}", entry.NoteId);
        }
    }
}
