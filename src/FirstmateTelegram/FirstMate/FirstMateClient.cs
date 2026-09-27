using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FirstmateTelegram.Infrastructure;

namespace FirstmateTelegram.FirstMate;

public sealed record FirstMateTimeouts
{
    public TimeSpan Note { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Announce { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Receipts { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Ready { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Runs FirstMate's inbox script, <c>$FM_HOME/bin/fm-inbox.sh</c>, without a shell and with <c>FM_HOME</c> set.
/// Message bodies travel on standard input. Every result is version-checked, and a timeout, an undefined exit
/// status or unparseable output is reported as a failure, never as success or as an empty result.
/// </summary>
public sealed class FirstMateClient
{
    public const string NoteSchema = "fm-inbox-note.v1";
    public const string ReceiptsSchema = "fm-inbox-receipts.v1";
    public const string ReadySchema = "fm-primary-ready.v1";

    const string NewerFormat = "FirstMate's records use a newer format; update firstmate-telegram";

    readonly IProcessRunner _runner;
    readonly string _home;
    readonly FirstMateTimeouts _timeouts;

    public FirstMateClient(IProcessRunner runner, string firstmateHome, FirstMateTimeouts? timeouts = null)
    {
        _runner = runner;
        _home = firstmateHome;
        _timeouts = timeouts ?? new FirstMateTimeouts();
    }

    public string InboxScript => Path.Combine(_home, "bin", "fm-inbox.sh");

    /// <summary><c>fm-inbox.sh note --request-id &lt;id&gt; --json -</c>, with the body on standard input.</summary>
    public async Task<NoteSaveResult> SaveNoteAsync(string requestId, string body, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["note", "--request-id", requestId, "--json", "-"], _timeouts.Note, body, cancellationToken);
        if (!result.Ran)
            return NoteSaveResult.Failed(RunFailure(result));
        if (result.ExitCode is not (0 or 3))
            return NoteSaveResult.Failed(ExitFailure(result));

        var parsed = ParseNote(result.StandardOutput);
        if (parsed.Failure is { } failure)
            return NoteSaveResult.Failed(failure);

        var note = parsed.Note!;
        var status = result.ExitCode == 0 ? NoteSaveStatus.Saved : NoteSaveStatus.SavedWithoutWake;
        return new NoteSaveResult(status, note.Id, note.Outcome, note.Acknowledged == true, null);
    }

    /// <summary><c>fm-inbox.sh announce --json &lt;note-id&gt;</c>, only for a note saved without a wake.</summary>
    public async Task<WakeRepairResult> AnnounceAsync(string noteId, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["announce", "--json", noteId], _timeouts.Announce, null, cancellationToken);
        if (!result.Ran)
            return new WakeRepairResult(false, RunFailure(result));
        if (result.ExitCode != 0)
            return new WakeRepairResult(false, ExitFailure(result));

        var parsed = ParseNote(result.StandardOutput);
        if (parsed.Failure is { } failure)
            return new WakeRepairResult(false, failure);
        if (parsed.Note!.Outcome is not ("created" or "replay"))
            return new WakeRepairResult(false, new CallFailure(FailureKind.Unparseable, $"FirstMate did not confirm the wake (outcome {parsed.Note.Outcome})"));
        return new WakeRepairResult(true, null);
    }

    /// <summary><c>fm-inbox.sh receipts --after &lt;cursor&gt;</c>, or without a cursor on the first run.</summary>
    public async Task<ReceiptsResult> ReadReceiptsAsync(string cursor, CancellationToken cancellationToken)
    {
        string[] arguments = string.IsNullOrEmpty(cursor) ? ["receipts"] : ["receipts", "--after", cursor];
        var result = await RunAsync(arguments, _timeouts.Receipts, null, cancellationToken);
        if (!result.Ran)
            return new ReceiptsResult(null, RunFailure(result));
        if (result.ExitCode != 0)
            return new ReceiptsResult(null, ExitFailure(result));

        var parsed = Parse(result.StandardOutput, FirstMateJsonContext.Default.ReceiptsJson, document => document.Schema, ReceiptsSchema);
        if (parsed.Failure is { } failure)
            return new ReceiptsResult(null, failure);

        var document = parsed.Document!;
        if (document.Replies is null || document.ReplyCursor is null || document.Replies.Any(reply => reply.Id is null || reply.Body is null || reply.Cursor is null))
            return new ReceiptsResult(null, Unparseable());

        var replies = document.Replies
            .Select(reply => new InboxReply(reply.Id!, reply.Body!, reply.Cursor!))
            .OrderBy(reply => reply.Cursor, StringComparer.Ordinal)
            .ToList();
        var omitted = document.Omitted ?? [];
        var receipts = new InboxReceipts(
            replies,
            document.ReplyCursor,
            HasMoreReplies: omitted.Any(entry => entry.Surface?.StartsWith("replies omitted", StringComparison.Ordinal) == true),
            PendingNoteIds: NoteIds(document.Pending),
            HandledNoteIds: NoteIds(document.Handled),
            IsPendingListComplete: document.Pending is not null && !omitted.Any(entry => entry.Surface?.StartsWith("pending notes omitted", StringComparison.Ordinal) == true));
        return new ReceiptsResult(receipts, null);
    }

    /// <summary><c>fm-inbox.sh ready</c>: FirstMate's read-only readiness projection.</summary>
    public async Task<ReadyResult> ReadReadyAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["ready"], _timeouts.Ready, null, cancellationToken);
        if (!result.Ran)
            return new ReadyResult(null, RunFailure(result));
        if (result.ExitCode != 0)
            return new ReadyResult(null, ExitFailure(result));

        var parsed = Parse(result.StandardOutput, FirstMateJsonContext.Default.ReadyJson, document => document.Schema, ReadySchema);
        if (parsed.Failure is { } failure)
            return new ReadyResult(null, failure);

        var document = parsed.Document!;
        if (document.Lock?.State is not { } lockState || document.WakeConsumer?.State is not { } consumerState || document.Posture?.State is not { } posture)
            return new ReadyResult(null, Unparseable());

        bool? canReceive = document.CanReceive.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
        return new ReadyResult(new ReadyReading(lockState, consumerState, posture, canReceive), null);
    }

    Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, string? input, CancellationToken cancellationToken)
    {
        var request = new ProcessRequest(InboxScript, arguments, timeout)
        {
            StandardInput = input,
            Environment = new Dictionary<string, string?> { ["FM_HOME"] = _home },
        };
        return _runner.RunAsync(request, cancellationToken);
    }

    static (NoteJson? Note, CallFailure? Failure) ParseNote(string output)
    {
        var parsed = Parse(output, FirstMateJsonContext.Default.NoteJson, document => document.Schema, NoteSchema);
        if (parsed.Failure is not null)
            return (null, parsed.Failure);
        if (string.IsNullOrEmpty(parsed.Document!.Id))
            return (null, Unparseable());
        return (parsed.Document, null);
    }

    static (T? Document, CallFailure? Failure) Parse<T>(string output, JsonTypeInfo<T> typeInfo, Func<T, string?> schemaOf, string schema)
        where T : class
    {
        T? document;
        try
        {
            document = JsonSerializer.Deserialize(output, typeInfo);
        }
        catch (JsonException)
        {
            return (null, Unparseable());
        }

        if (document is null)
            return (null, Unparseable());
        if (schemaOf(document) != schema)
            return (null, new CallFailure(FailureKind.UnknownSchema, NewerFormat));
        return (document, null);
    }

    static HashSet<string> NoteIds(List<NoteRowJson>? rows) =>
        rows is null ? [] : rows.Where(row => row.Id is not null).Select(row => row.Id!).ToHashSet(StringComparer.Ordinal);

    static CallFailure Unparseable() => new(FailureKind.Unparseable, "FirstMate's records could not be read: unexpected output from fm-inbox.sh");

    static CallFailure RunFailure(ProcessResult result) => result.TimedOut
        ? new CallFailure(FailureKind.TimedOut, "FirstMate's inbox did not answer in time")
        : new CallFailure(FailureKind.NotStarted, $"could not run fm-inbox.sh: {result.StartFailure}");

    static CallFailure ExitFailure(ProcessResult result)
    {
        var line = result.StandardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        var reason = line is null
            ? $"fm-inbox.sh exited with status {result.ExitCode.ToString(CultureInfo.InvariantCulture)}"
            : line.StartsWith("fm-inbox: ", StringComparison.Ordinal) ? line["fm-inbox: ".Length..] : line;
        if (reason.Length > 200)
            reason = reason[..200];
        return new CallFailure(FailureKind.ExitStatus, reason, result.ExitCode);
    }
}
