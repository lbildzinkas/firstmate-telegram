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
/// A live ping's reply becomes the bridge's own live-answer sentence, and an unanswered one is told so at its deadline.
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
    readonly AvailabilityReader _availability;
    readonly ILogger<ReplyForwarder> _logger;
    readonly TimeProvider _time;

    public ReplyForwarder(
        FirstMateClient firstMate,
        TelegramGateway telegram,
        StateStore store,
        WakeSignal replyWaiting,
        string repliedReaction,
        AvailabilityReader availability,
        TimeProvider time,
        ILogger<ReplyForwarder> logger)
    {
        _firstMate = firstMate;
        _telegram = telegram;
        _store = store;
        _replyWaiting = replyWaiting;
        _repliedReaction = repliedReaction;
        _availability = availability;
        _time = time;
        _logger = logger;
    }

    /// <summary>True while a bridge request or live ping is still waiting for its reply, which switches polling to the fast cadence.</summary>
    public bool HasWaitingRequests => _store.Requests.Values.Any(entry => entry.IsWaitingForReply || entry.IsLivePingOpen);

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

        await HandleLivePingTimeoutsAsync(cancellationToken);
        await PruneAsync(cancellationToken);
    }

    async Task DeliverAsync(RequestEntry entry, InboxReply reply, CancellationToken cancellationToken)
    {
        if (entry.Kind == RequestKinds.LivePing)
        {
            await DeliverLivePingAsync(entry, reply, cancellationToken);
            return;
        }

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
    /// The live ping's answer to the user is the bridge's own sentence: answered in time, or marked late when the
    /// timeout already passed. The reply is never skipped, and the cursor advances after it is sent.
    /// </summary>
    async Task DeliverLivePingAsync(RequestEntry entry, InboxReply reply, CancellationToken cancellationToken)
    {
        var answeredAt = reply.At ?? _time.GetUtcNow();
        var elapsed = answeredAt - entry.SavedAt;
        var inTime = entry.LiveDeadlineAt is not { } deadline || answeredAt <= deadline;
        var text = inTime ? BridgeTexts.LivePingAnswered(elapsed) : BridgeTexts.LivePingLateAnswer(elapsed);
        await _telegram.SendTextAsync(entry.ChatId, text, entry.MessageId, cancellationToken);
        await _store.UpdateRequestsAsync(
            requests => requests.SetItem(entry.NoteId, requests[entry.NoteId] with { RepliedAt = _time.GetUtcNow() }),
            cancellationToken);
        await _store.UpdateStateAsync(state => state with { ReplyCursor = reply.Cursor }, cancellationToken);
        _logger.LogInformation("the live ping note {NoteId} was answered{Late}, cursor {Cursor}", entry.NoteId, inTime ? "" : " late", reply.Cursor);
        await _telegram.TrySetReactionAsync(entry.ChatId, entry.MessageId, _repliedReaction, cancellationToken);
    }

    /// <summary>
    /// Tells the user when a live ping is overdue. The telling is recorded before the message is sent, so a crash
    /// never repeats it; a reply that arrives afterwards is still delivered, marked as late.
    /// </summary>
    async Task HandleLivePingTimeoutsAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var expired = _store.Requests.Values
            .Where(entry => entry.IsLivePingOpen && entry.LiveDeadlineAt is { } deadline && deadline <= now)
            .ToList();
        foreach (var entry in expired)
        {
            await _store.UpdateRequestsAsync(
                requests => requests.TryGetValue(entry.NoteId, out var current)
                    ? requests.SetItem(entry.NoteId, current with { LiveTimeoutToldAt = now })
                    : requests,
                cancellationToken);

            var timeout = (int)Math.Max(1, (entry.LiveDeadlineAt!.Value - entry.SavedAt).TotalSeconds);
            var report = await _availability.ReadAsync(cancellationToken);
            var lines = new List<string> { BridgeTexts.LivePingNotAvailable(timeout) };
            lines.AddRange(report.CheckLines);
            if (report.IsAwayOrQuiet)
                lines.Add(BridgeTexts.AwayModePickupNote);
            await _telegram.SendTextAsync(entry.ChatId, string.Join("\n", lines), entry.MessageId, cancellationToken);
            _logger.LogInformation("the live ping note {NoteId} did not answer in {Seconds} s", entry.NoteId, timeout);
        }
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

    /// <summary>Drops a request 30 days after it settled: its reply, FirstMate's acknowledgement without one, or a live ping's timeout.</summary>
    async Task PruneAsync(CancellationToken cancellationToken)
    {
        var cutoff = _time.GetUtcNow() - KeepAnswered;
        var expired = _store.Requests.Values
            .Where(entry => (entry.RepliedAt ?? entry.AcknowledgedAt ?? entry.LiveTimeoutToldAt) is { } settledAt && settledAt < cutoff)
            .Select(entry => entry.NoteId)
            .ToList();
        if (expired.Count > 0)
            await _store.UpdateRequestsAsync(requests => requests.RemoveRange(expired), cancellationToken);
    }
}
