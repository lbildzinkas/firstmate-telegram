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

public sealed record InboxReply(string NoteId, string Body, string Cursor);

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
