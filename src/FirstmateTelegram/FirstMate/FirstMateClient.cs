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

    /// <summary>Either snapshot call: the bearings projection and the fleet fallback.</summary>
    public TimeSpan Snapshot { get; init; } = TimeSpan.FromSeconds(60);
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
    public const string BearingsSchema = "fm-bearings.v1";
    public const string FleetSnapshotSchema = "fm-fleet-snapshot.v1";

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

    public string BearingsScript => Path.Combine(_home, "bin", "fm-bearings-snapshot.sh");

    public string FleetSnapshotScript => Path.Combine(_home, "bin", "fm-fleet-snapshot.sh");

    /// <summary><c>fm-inbox.sh note --request-id &lt;id&gt; --json -</c>, with the body on standard input.</summary>
    public async Task<NoteSaveResult> SaveNoteAsync(string requestId, string body, CancellationToken cancellationToken)
    {
        var result = await RunAsync(InboxScript, ["note", "--request-id", requestId, "--json", "-"], _timeouts.Note, body, cancellationToken);
        if (!result.Ran)
            return NoteSaveResult.Failed(RunFailure(result, InboxScript));
        if (result.ExitCode is not (0 or 3))
            return NoteSaveResult.Failed(ExitFailure(result, InboxScript));

        var parsed = ParseNote(result.StandardOutput, InboxScript);
        if (parsed.Failure is { } failure)
            return NoteSaveResult.Failed(failure);

        var note = parsed.Note!;
        var status = result.ExitCode == 0 ? NoteSaveStatus.Saved : NoteSaveStatus.SavedWithoutWake;
        return new NoteSaveResult(status, note.Id, note.Outcome, note.Acknowledged == true, null);
    }

    /// <summary><c>fm-inbox.sh announce --json &lt;note-id&gt;</c>, only for a note saved without a wake.</summary>
    public async Task<WakeRepairResult> AnnounceAsync(string noteId, CancellationToken cancellationToken)
    {
        var result = await RunAsync(InboxScript, ["announce", "--json", noteId], _timeouts.Announce, null, cancellationToken);
        if (!result.Ran)
            return new WakeRepairResult(false, RunFailure(result, InboxScript));
        if (result.ExitCode != 0)
            return new WakeRepairResult(false, ExitFailure(result, InboxScript));

        var parsed = ParseNote(result.StandardOutput, InboxScript);
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
        var result = await RunAsync(InboxScript, arguments, _timeouts.Receipts, null, cancellationToken);
        if (!result.Ran)
            return new ReceiptsResult(null, RunFailure(result, InboxScript));
        if (result.ExitCode != 0)
            return new ReceiptsResult(null, ExitFailure(result, InboxScript));

        var parsed = Parse(result.StandardOutput, FirstMateJsonContext.Default.ReceiptsJson, document => document.Schema, ReceiptsSchema, InboxScript);
        if (parsed.Failure is { } failure)
            return new ReceiptsResult(null, failure);

        var document = parsed.Document!;
        if (document.Replies is null || document.ReplyCursor is null || document.Replies.Any(reply => reply.Id is null || reply.Body is null || reply.Cursor is null))
            return new ReceiptsResult(null, Unparseable(InboxScript));

        var replies = document.Replies
            .Select(reply => new InboxReply(reply.Id!, reply.Body!, reply.Cursor!, ParseTime(reply.At)))
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
        var result = await RunAsync(InboxScript, ["ready"], _timeouts.Ready, null, cancellationToken);
        if (!result.Ran)
            return new ReadyResult(null, RunFailure(result, InboxScript));
        if (result.ExitCode != 0)
            return new ReadyResult(null, ExitFailure(result, InboxScript));

        var parsed = Parse(result.StandardOutput, FirstMateJsonContext.Default.ReadyJson, document => document.Schema, ReadySchema, InboxScript);
        if (parsed.Failure is { } failure)
            return new ReadyResult(null, failure);

        var document = parsed.Document!;
        if (document.Lock?.State is not { } lockState || document.WakeConsumer?.State is not { } consumerState || document.Posture?.State is not { } posture)
            return new ReadyResult(null, Unparseable(InboxScript));

        bool? canReceive = document.CanReceive.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
        return new ReadyResult(new ReadyReading(lockState, consumerState, posture, canReceive), null);
    }

    /// <summary><c>fm-inbox.sh receipts --all-pending</c>: finds the oldest note FirstMate has not handled, for the not-responding check.</summary>
    public async Task<PendingResult> ReadAllPendingAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(InboxScript, ["receipts", "--all-pending"], _timeouts.Receipts, null, cancellationToken);
        if (!result.Ran)
            return new PendingResult(null, RunFailure(result, InboxScript));
        if (result.ExitCode != 0)
            return new PendingResult(null, ExitFailure(result, InboxScript));

        var parsed = Parse(result.StandardOutput, FirstMateJsonContext.Default.ReceiptsJson, document => document.Schema, ReceiptsSchema, InboxScript);
        if (parsed.Failure is { } failure)
            return new PendingResult(null, failure);

        // Notes whose announcement is known to have failed never reached FirstMate, so they are not its delay.
        var oldest = parsed.Document!.Pending?
            .Where(row => row is { Id: not null, At: not null } && row.Acknowledged != true && row.Announced != false)
            .Select(row => ParseTime(row.At))
            .OfType<DateTimeOffset>()
            .Cast<DateTimeOffset?>()
            .Min();
        return new PendingResult(new PendingNotes(oldest), null);
    }

    /// <summary><c>fm-bearings-snapshot.sh --json</c>: the status answer's primary source. Exit 3 means away mode refused it.</summary>
    public async Task<BearingsResult> ReadBearingsAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(BearingsScript, ["--json"], _timeouts.Snapshot, null, cancellationToken);
        if (!result.Ran)
            return new BearingsResult(null, AwayRefused: false, RunFailure(result, BearingsScript, "the snapshot timed out"));
        if (result.ExitCode == 3)
            return new BearingsResult(null, AwayRefused: true, null);
        if (result.ExitCode != 0)
            return new BearingsResult(null, AwayRefused: false, ExitFailure(result, BearingsScript));

        var parsed = Parse(result.StandardOutput, FirstMateJsonContext.Default.BearingsJson, document => document.Schema, BearingsSchema, BearingsScript);
        if (parsed.Failure is { } failure)
            return new BearingsResult(null, AwayRefused: false, failure);

        var document = parsed.Document!;
        return new BearingsResult(
            new BearingsSnapshot(
                (document.DecisionsOpen ?? []).Where(row => row?.Summary is not null).Select(row => row.Summary!).ToList(),
                (document.InFlight ?? []).Where(row => row?.Name is not null).Select(row => new BearingsWork(row.Name!, row.State, row.Doing, row.Repo, row.Kind)).ToList(),
                (document.RecordedPrs ?? []).Where(row => row?.Task is not null).Select(row => new RecordedPr(row.Task!, row.Url)).ToList(),
                (document.Landed ?? []).Where(row => row?.What is not null).Select(row => new LandedWork(row.What!, row.Artifact)).ToList(),
                (document.Gates ?? []).Where(row => row?.Title is not null).Select(row => new WorkGate(row.Title!, row.BlockedBy, row.Reason)).ToList()),
            AwayRefused: false,
            null);
    }

    /// <summary><c>fm-fleet-snapshot.sh --json</c>: the canonical fleet snapshot the status answer falls back to in away mode.</summary>
    public async Task<FleetSnapshotResult> ReadFleetSnapshotAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(FleetSnapshotScript, ["--json"], _timeouts.Snapshot, null, cancellationToken);
        if (!result.Ran)
            return new FleetSnapshotResult(null, RunFailure(result, FleetSnapshotScript, "the snapshot timed out"));
        if (result.ExitCode != 0)
            return new FleetSnapshotResult(null, ExitFailure(result, FleetSnapshotScript));

        var parsed = Parse(result.StandardOutput, FirstMateJsonContext.Default.FleetSnapshotJson, document => document.Schema, FleetSnapshotSchema, FleetSnapshotScript);
        if (parsed.Failure is { } failure)
            return new FleetSnapshotResult(null, failure);

        var document = parsed.Document!;
        var snapshot = new FleetSnapshot(
            (document.Tasks ?? [])
                .Where(task => task?.Backlog?.Title is not null)
                .Select(task => new FleetTask(task.Project, task.Backlog!.Title, task.CurrentState?.State, task.SecondMate == true))
                .ToList(),
            (document.Backlog?.Records ?? [])
                .Where(record => record?.Title is not null)
                .Select(record => new FleetRecord(
                    record.Id,
                    record.Title!,
                    record.State,
                    record.CaptainActionable == true,
                    record.HoldReason,
                    record.UnresolvedBlockerIds ?? [],
                    record.UpdatedAt))
                .ToList());
        return new FleetSnapshotResult(snapshot, null);
    }

    Task<ProcessResult> RunAsync(string script, IReadOnlyList<string> arguments, TimeSpan timeout, string? input, CancellationToken cancellationToken)
    {
        var request = new ProcessRequest(script, arguments, timeout)
        {
            StandardInput = input,
            Environment = new Dictionary<string, string?> { ["FM_HOME"] = _home },
        };
        return _runner.RunAsync(request, cancellationToken);
    }

    static DateTimeOffset? ParseTime(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    static (NoteJson? Note, CallFailure? Failure) ParseNote(string output, string script)
    {
        var parsed = Parse(output, FirstMateJsonContext.Default.NoteJson, document => document.Schema, NoteSchema, script);
        if (parsed.Failure is not null)
            return (null, parsed.Failure);
        if (string.IsNullOrEmpty(parsed.Document!.Id))
            return (null, Unparseable(script));
        return (parsed.Document, null);
    }

    static (T? Document, CallFailure? Failure) Parse<T>(string output, JsonTypeInfo<T> typeInfo, Func<T, string?> schemaOf, string schema, string script)
        where T : class
    {
        T? document;
        try
        {
            document = JsonSerializer.Deserialize(output, typeInfo);
        }
        catch (JsonException)
        {
            return (null, Unparseable(script));
        }

        if (document is null)
            return (null, Unparseable(script));
        if (schemaOf(document) != schema)
            return (null, new CallFailure(FailureKind.UnknownSchema, NewerFormat));
        return (document, null);
    }

    static HashSet<string> NoteIds(List<NoteRowJson>? rows) =>
        rows is null ? [] : rows.Where(row => row.Id is not null).Select(row => row.Id!).ToHashSet(StringComparer.Ordinal);

    static CallFailure Unparseable(string script) => new(FailureKind.Unparseable, $"FirstMate's records could not be read: unexpected output from {Path.GetFileName(script)}");

    static CallFailure RunFailure(ProcessResult result, string script, string? timedOutReason = null) => result.TimedOut
        ? new CallFailure(FailureKind.TimedOut, timedOutReason ?? "FirstMate's inbox did not answer in time")
        : new CallFailure(FailureKind.NotStarted, $"could not run {Path.GetFileName(script)}: {result.StartFailure}");

    static CallFailure ExitFailure(ProcessResult result, string script)
    {
        var name = Path.GetFileName(script);
        var line = result.StandardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        var reason = line is null
            ? $"{name} exited with status {result.ExitCode.ToString(CultureInfo.InvariantCulture)}"
            : line.StartsWith("fm-inbox: ", StringComparison.Ordinal) ? line["fm-inbox: ".Length..] : line;
        if (reason.Length > 200)
            reason = reason[..200];
        return new CallFailure(FailureKind.ExitStatus, reason, result.ExitCode);
    }
}
