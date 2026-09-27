namespace FirstmateTelegram.FirstMate;

/// <summary>Why a FirstMate call gave no usable answer. Every kind means "FirstMate's records could not be read", never success.</summary>
public enum FailureKind
{
    TimedOut,
    NotStarted,
    ExitStatus,
    Unparseable,
    UnknownSchema,
}

/// <summary>A failed call: <see cref="Reason"/> is for the user, <see cref="Kind"/> and <see cref="ExitCode"/> for the log.</summary>
public sealed record CallFailure(FailureKind Kind, string Reason, int? ExitCode = null);

public enum NoteSaveStatus
{
    /// <summary>Saved, and FirstMate was woken or has already acknowledged it (exit 0).</summary>
    Saved,

    /// <summary>Saved, but FirstMate was not woken (exit 3); the wake is repaired later.</summary>
    SavedWithoutWake,

    /// <summary>Nothing is known to be saved; retry with the same request id.</summary>
    Failed,
}

public sealed record NoteSaveResult(NoteSaveStatus Status, string? NoteId, string? Outcome, bool Acknowledged, CallFailure? Failure)
{
    public bool IsSaved => Status != NoteSaveStatus.Failed;

    public static NoteSaveResult Failed(CallFailure failure) => new(NoteSaveStatus.Failed, null, null, false, failure);
}

public sealed record WakeRepairResult(bool Repaired, CallFailure? Failure);

public sealed record InboxReply(string NoteId, string Body, string Cursor, DateTimeOffset? At);

public sealed record InboxReceipts(
    IReadOnlyList<InboxReply> Replies,
    string ReplyCursor,
    bool HasMoreReplies,
    IReadOnlySet<string> PendingNoteIds,
    IReadOnlySet<string> HandledNoteIds,
    bool IsPendingListComplete);

public sealed record ReceiptsResult(InboxReceipts? Receipts, CallFailure? Failure);

/// <summary>The parts of <c>fm-primary-ready.v1</c> the bridge reads.</summary>
public sealed record ReadyReading(string LockState, string WakeConsumerState, string PostureState, bool? CanReceive);

public sealed record ReadyResult(ReadyReading? Reading, CallFailure? Failure)
{
    public bool IsAway => Reading?.PostureState == "away";
}

/// <summary>What <c>receipts --all-pending</c> says about the notes FirstMate has not handled yet.</summary>
public sealed record PendingNotes(DateTimeOffset? OldestUnacknowledgedAt);

public sealed record PendingResult(PendingNotes? Notes, CallFailure? Failure);

public sealed record BearingsWork(string Name, string? State, string? Doing, string? Repo, string? Kind);

public sealed record RecordedPr(string Task, string? Url);

public sealed record LandedWork(string What, string? Artifact);

public sealed record WorkGate(string Title, string? BlockedBy, string? Reason);

/// <summary>The four-part bearings projection, <c>fm-bearings.v1</c>.</summary>
public sealed record BearingsSnapshot(
    IReadOnlyList<string> OpenDecisions,
    IReadOnlyList<BearingsWork> InFlight,
    IReadOnlyList<RecordedPr> RecordedPrs,
    IReadOnlyList<LandedWork> Landed,
    IReadOnlyList<WorkGate> Gates);

/// <summary>A bearings read. <see cref="AwayRefused"/> is FirstMate's exit 3: away mode refuses the projection, and the fleet snapshot is used instead.</summary>
public sealed record BearingsResult(BearingsSnapshot? Snapshot, bool AwayRefused, CallFailure? Failure);

public sealed record FleetTask(string? Project, string? Title, string? State, bool SecondMate);

public sealed record FleetRecord(
    string? Id,
    string Title,
    string? State,
    bool CaptainActionable,
    string? HoldReason,
    IReadOnlyList<string> UnresolvedBlockerIds,
    DateTimeOffset? UpdatedAt);

/// <summary>The canonical fleet snapshot, <c>fm-fleet-snapshot.v1</c>, used for the status answer while away mode is on.</summary>
public sealed record FleetSnapshot(IReadOnlyList<FleetTask> Tasks, IReadOnlyList<FleetRecord> Records);

public sealed record FleetSnapshotResult(FleetSnapshot? Snapshot, CallFailure? Failure);
