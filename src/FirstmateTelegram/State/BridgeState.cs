namespace FirstmateTelegram.State;

/// <summary>The contents of <c>state.json</c>: the Telegram position, the reply cursor and the stopped flag.</summary>
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

    public bool Stopped { get; init; }
}

/// <summary>What a saved note stands for.</summary>
public static class RequestKinds
{
    public const string Request = "request";
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

    /// <summary>A request waits for its reply until the reply is sent or FirstMate acknowledges it without one.</summary>
    public bool IsWaitingForReply => RepliedAt is null && AcknowledgedAt is null;
}

public sealed record RequestsDocument
{
    public const string SchemaName = "firstmate-telegram.requests.v1";

    public string Schema { get; init; } = SchemaName;
    public IReadOnlyList<RequestEntry> Requests { get; init; } = [];
}
