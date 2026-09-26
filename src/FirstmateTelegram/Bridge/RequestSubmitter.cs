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

/// <summary>
/// Saves a request as a FirstMate inbox note, records it in the request map, reacts 👀, says once when FirstMate
/// is not running or not picking up requests, and repairs the wake for notes saved without one.
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
        var saved = await _firstMate.SaveNoteAsync(requestId, NoteBody.Build(text, ready.IsAway), cancellationToken);
        if (!saved.IsSaved)
            return new SubmitResult(null, saved.Failure);

        var noteId = saved.NoteId!;
        var announced = saved.Status == NoteSaveStatus.Saved;
        await _store.UpdateRequestsAsync(
            requests => requests.TryGetValue(noteId, out var existing)
                ? requests.SetItem(noteId, existing with { Announced = existing.Announced || announced })
                : requests.SetItem(noteId, new RequestEntry
                {
                    NoteId = noteId,
                    RequestId = requestId,
                    ChatId = chatId,
                    MessageId = message.Id,
                    SavedAt = _time.GetUtcNow(),
                    Announced = announced,
                }),
            cancellationToken);
        _logger.LogInformation(
            "saved message {MessageId} as note {NoteId} ({Outcome}{Wake})",
            message.Id,
            noteId,
            saved.Outcome,
            announced ? "" : ", not announced");
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

    /// <summary>Runs <c>announce</c> for every note that was saved without waking FirstMate. It never saves a second note.</summary>
    public async Task RepairWakesAsync(CancellationToken cancellationToken)
    {
        var unannounced = _store.Requests.Values.Where(entry => !entry.Announced && entry.IsWaitingForReply).ToList();
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
