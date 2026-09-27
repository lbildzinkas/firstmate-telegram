namespace FirstmateTelegram.State;

/// <summary>The contents of <c>state.json</c>: the Telegram position, the reply cursor, the mute, the stopped flag and the alert watchers' positions.</summary>
public sealed record BridgeState
{
    public const string SchemaName = "firstmate-telegram.state.v1";

    public string Schema { get; init; } = SchemaName;

    /// <summary>The bot the Telegram position belongs to. A different bot starts from the beginning.</summary>
    public long? BotId { get; init; }

    /// <summary>The next <c>getUpdates</c> offset: one above the last update in the unbroken run of handled updates.</summary>
    public int UpdateOffset { get; init; }

    /// <summary>Updates at or above <see cref="UpdateOffset"/> already handled behind a request that could not be saved yet.</summary>
    public IReadOnlyList<int> HandledUpdateIds { get; init; } = [];

    /// <summary>FirstMate's reply cursor, stored only after every part of that reply was sent.</summary>
    public string ReplyCursor { get; init; } = "";

    /// <summary>While set, alerts are delivered silently. A mute survives restarts and ends on its own at its end time.</summary>
    public DateTimeOffset? MuteUntil { get; init; }

    public bool Stopped { get; init; }

    /// <summary>Where the alert watcher stopped reading the fleet ledger: file identity and byte offset.</summary>
    public LedgerPositionView? Ledger { get; init; }

    /// <summary>Live captain holds the decision watcher has seen: task id to first-seen time and absence count. Null until the first snapshot read.</summary>
    public IReadOnlyDictionary<string, DecisionWatch>? Decisions { get; init; }

    /// <summary>Worker statuses waiting out the settle window before they may alert.</summary>
    public IReadOnlyList<SettleWatch> Settles { get; init; } = [];

    /// <summary>The availability alert watcher's debounce state. Null until its first poll.</summary>
    public AvailabilityWatch? Availability { get; init; }
}

/// <summary>The ledger position as stored in <c>state.json</c>, so the alert watcher resumes after a restart.</summary>
public sealed record LedgerPositionView(long Device, long Inode, long Offset);

/// <summary>A live captain hold the decision watcher remembers: first seen, and how many snapshots in a row it was absent.</summary>
public sealed record DecisionWatch(DateTimeOffset FirstSeen, int AbsentStreak);

/// <summary>A worker status waiting out the settle window: it alerts only if still open when the window passes. The dedupe key is fixed when the record arrives, so a repeated ledger record never alerts twice.</summary>
public sealed record SettleWatch(string Task, string State, string? Key, string? Text, DateTimeOffset DueAt, string AlertKey);

/// <summary>The availability alert watcher's state: the outage it is tracking, whether it alerted, and the debounces.</summary>
public sealed record AvailabilityWatch
{
    /// <summary>The bad verdict being tracked, as <see cref="Bridge.AvailabilityVerdict"/> names it.</summary>
    public required string BadVerdict { get; init; }

    /// <summary>When that verdict was first seen; the alert waits for two polls and two minutes from here.</summary>
    public required DateTimeOffset BadSince { get; init; }

    /// <summary>True once the outage alert was sent. It is never sent twice.</summary>
    public bool Alerted { get; init; }

    public int BadStreak { get; init; }

    public int GoodStreak { get; init; }

    public DateTimeOffset? GoodSince { get; init; }
}

/// <summary>What a saved note stands for.</summary>
public static class RequestKinds
{
    public const string Request = "request";

    /// <summary>A <c>/ping live</c> availability check: a real FirstMate turn that must answer within its deadline.</summary>
    public const string LivePing = "live ping";

    /// <summary>An explicit return saved by <c>/back</c> once FirstMate has the hook.</summary>
    public const string Return = "return";

    /// <summary>A request the user wrote as a reply to an alert, tied to that alert.</summary>
    public const string AlertReply = "alert reply";
}

/// <summary>One row of the request map in <c>requests.json</c>: a FirstMate inbox note and the Telegram message it came from.</summary>
public sealed record RequestEntry
{
    public required string NoteId { get; init; }
    public required string RequestId { get; init; }
    public required long ChatId { get; init; }
    public required int MessageId { get; init; }
    public string Kind { get; init; } = RequestKinds.Request;
    public required DateTimeOffset SavedAt { get; init; }

    /// <summary>False while FirstMate has not been woken for the note; the wake is then repaired.</summary>
    public bool Announced { get; init; }

    /// <summary>When the bridge first saw FirstMate's acknowledgement of the note without a reply.</summary>
    public DateTimeOffset? AcknowledgedAt { get; init; }

    /// <summary>How many parts of the reply were sent, recorded after each part.</summary>
    public int ReplyPartsSent { get; init; }

    public DateTimeOffset? RepliedAt { get; init; }

    /// <summary>When a live ping's answer is overdue; set when the note is saved.</summary>
    public DateTimeOffset? LiveDeadlineAt { get; init; }

    /// <summary>When the user was told the live ping timed out. A late reply is still delivered afterwards.</summary>
    public DateTimeOffset? LiveTimeoutToldAt { get; init; }

    /// <summary>A request waits for its reply until the reply is sent or FirstMate acknowledges it without one; a live ping stops waiting at its timeout.</summary>
    public bool IsWaitingForReply => RepliedAt is null && AcknowledgedAt is null && LiveTimeoutToldAt is null;

    /// <summary>True while a live ping can still be answered in time.</summary>
    public bool IsLivePingOpen => Kind == RequestKinds.LivePing && RepliedAt is null && LiveTimeoutToldAt is null;
}

public sealed record RequestsDocument
{
    public const string SchemaName = "firstmate-telegram.requests.v1";

    public string Schema { get; init; } = SchemaName;
    public IReadOnlyList<RequestEntry> Requests { get; init; } = [];
}

/// <summary>One row of the alert history in <c>alerts.json</c>: an alert the bridge sent, kept so a reply to it can quote it.</summary>
public sealed record AlertEntry
{
    /// <summary>The dedupe key: the same key is never alerted twice.</summary>
    public required string Key { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>The FirstMate task the alert is about, when there is one.</summary>
    public string? Task { get; init; }

    /// <summary>The alert text exactly as sent. The only alert or chat message text the bridge stores on disk (spec 9).</summary>
    public required string Text { get; init; }

    /// <summary>The sent alert's Telegram message id, recorded after the send; used to match a reply to the alert.</summary>
    public int? TelegramMessageId { get; init; }
}

public sealed record AlertsDocument
{
    public const string SchemaName = "firstmate-telegram.alerts.v1";

    public string Schema { get; init; } = SchemaName;
    public IReadOnlyList<AlertEntry> Alerts { get; init; } = [];
}
