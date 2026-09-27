using System.Collections.Immutable;
using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.State;
using FirstmateTelegram.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FirstmateTelegram.Bridge;

/// <summary>
/// Polls FirstMate's receipts and sends each reply to a bridge note back to the user, threaded to their message and
/// split when too long. Parts are recorded as they are sent, the reply cursor is stored only after the last part,
/// and the replied reaction is set best-effort after that. Replies to other notes only advance the cursor.
/// </summary>
public sealed class ReplyForwarder : BackgroundService
{
    public static readonly TimeSpan WaitingCadence = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan IdleCadence = TimeSpan.FromSeconds(60);
    static readonly TimeSpan KeepAnswered = TimeSpan.FromDays(30);

    readonly FirstMateClient _firstMate;
    readonly TelegramGateway _telegram;
    readonly StateStore _store;
    readonly WakeSignal _replyWaiting;
    readonly string _repliedReaction;
    readonly TimeProvider _time;
    readonly ILogger<ReplyForwarder> _logger;

    public ReplyForwarder(
        FirstMateClient firstMate,
        TelegramGateway telegram,
        StateStore store,
        WakeSignal replyWaiting,
        string repliedReaction,
        TimeProvider time,
        ILogger<ReplyForwarder> logger)
    {
        _firstMate = firstMate;
        _telegram = telegram;
        _store = store;
        _replyWaiting = replyWaiting;
        _repliedReaction = repliedReaction;
        _time = time;
        _logger = logger;
    }

    /// <summary>True while a bridge request is still waiting for its reply, which switches polling to the fast cadence.</summary>
    public bool HasWaitingRequests => _store.Requests.Values.Any(entry => entry.IsWaitingForReply);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ForwardOnceAsync(stoppingToken);
            await _replyWaiting.WaitAsync(HasWaitingRequests ? WaitingCadence : IdleCadence, _time, stoppingToken);
        }
    }

    public async Task ForwardOnceAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            // Only entries recorded before this read can be judged by it: their notes were already saved.
            var known = _store.Requests;
            var cursor = _store.State.ReplyCursor;
            var read = await _firstMate.ReadReceiptsAsync(cursor, cancellationToken);
            if (read.Receipts is not { } receipts)
            {
                _logger.LogWarning("could not read FirstMate's receipts ({Failure}, exit {ExitCode})", read.Failure!.Kind, read.Failure.ExitCode);
                return;
            }

            await NoteAcknowledgementsAsync(known, receipts, cancellationToken);
            foreach (var reply in receipts.Replies)
            {
                if (_store.Requests.TryGetValue(reply.NoteId, out var entry) && entry.RepliedAt is null)
                    await DeliverAsync(entry, reply, cancellationToken);
            }

            if (receipts.ReplyCursor != _store.State.ReplyCursor)
                await _store.UpdateStateAsync(state => state with { ReplyCursor = receipts.ReplyCursor }, cancellationToken);
            // FirstMate bounds each read; when it left replies out, read again from the new cursor straight away.
            if (!receipts.HasMoreReplies || receipts.ReplyCursor == cursor)
                break;
        }

        await PruneAsync(cancellationToken);
    }

    async Task DeliverAsync(RequestEntry entry, InboxReply reply, CancellationToken cancellationToken)
    {
        var body = string.IsNullOrWhiteSpace(reply.Body) ? BridgeTexts.EmptyReply : reply.Body;
        var parts = MessageSplitter.Split(body);
        for (var index = entry.ReplyPartsSent; index < parts.Count; index++)
        {
            await _telegram.SendTextAsync(entry.ChatId, parts[index], entry.MessageId, cancellationToken);
            var sent = index + 1;
            var repliedAt = sent == parts.Count ? _time.GetUtcNow() : (DateTimeOffset?)null;
            await _store.UpdateRequestsAsync(
                requests => requests.SetItem(entry.NoteId, requests[entry.NoteId] with { ReplyPartsSent = sent, RepliedAt = repliedAt }),
                cancellationToken);
        }

        if (entry.ReplyPartsSent >= parts.Count)
        {
            await _store.UpdateRequestsAsync(
                requests => requests.SetItem(entry.NoteId, requests[entry.NoteId] with { RepliedAt = _time.GetUtcNow() }),
                cancellationToken);
        }

        await _store.UpdateStateAsync(state => state with { ReplyCursor = reply.Cursor }, cancellationToken);
        _logger.LogInformation("sent the reply to note {NoteId} in {Parts} part(s), cursor {Cursor}", entry.NoteId, parts.Count, reply.Cursor);
        await _telegram.TrySetReactionAsync(entry.ChatId, entry.MessageId, _repliedReaction, cancellationToken);
    }

    /// <summary>
    /// Marks requests FirstMate has acknowledged without a reply, so they stop holding the fast cadence. A late reply
    /// is still delivered.
    /// </summary>
    async Task NoteAcknowledgementsAsync(ImmutableDictionary<string, RequestEntry> known, InboxReceipts receipts, CancellationToken cancellationToken)
    {
        var acknowledged = known.Values
            .Where(entry => entry.IsWaitingForReply)
            .Where(entry => receipts.HandledNoteIds.Contains(entry.NoteId)
                || (receipts.IsPendingListComplete && !receipts.PendingNoteIds.Contains(entry.NoteId)))
            .Where(entry => receipts.Replies.All(reply => reply.NoteId != entry.NoteId))
            .Select(entry => entry.NoteId)
            .ToList();
        if (acknowledged.Count == 0)
            return;

        var now = _time.GetUtcNow();
        await _store.UpdateRequestsAsync(
            requests => acknowledged.Aggregate(
                requests,
                (map, noteId) => map.TryGetValue(noteId, out var entry) ? map.SetItem(noteId, entry with { AcknowledgedAt = now }) : map),
            cancellationToken);
    }

    /// <summary>Drops a request 30 days after its reply, or 30 days after FirstMate acknowledged it without one.</summary>
    async Task PruneAsync(CancellationToken cancellationToken)
    {
        var cutoff = _time.GetUtcNow() - KeepAnswered;
        var expired = _store.Requests.Values
            .Where(entry => (entry.RepliedAt ?? entry.AcknowledgedAt) is { } settledAt && settledAt < cutoff)
            .Select(entry => entry.NoteId)
            .ToList();
        if (expired.Count > 0)
            await _store.UpdateRequestsAsync(requests => requests.RemoveRange(expired), cancellationToken);
    }
}
