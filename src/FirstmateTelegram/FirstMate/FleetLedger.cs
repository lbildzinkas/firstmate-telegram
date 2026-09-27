using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace FirstmateTelegram.FirstMate;

/// <summary>Where the bridge stopped reading the ledger: the file's identity (device and inode) and byte offset.</summary>
public sealed record LedgerPosition(long Device, long Inode, long Offset);

/// <summary>One event from the fleet activity ledger; events and members the bridge does not know are ignored.</summary>
public abstract record LedgerEvent
{
    public required string Task { get; init; }

    /// <summary>A worker started: <c>task.dispatched</c>, with the task's kind (a scout researches) and project.</summary>
    public sealed record Dispatched : LedgerEvent
    {
        public required string? Kind { get; init; }

        public required string? Project { get; init; }
    }

    /// <summary>A pull request is ready for review: <c>task.pr_ready</c>, with the PR's full URL.</summary>
    public sealed record PrReady : LedgerEvent
    {
        public required string PrUrl { get; init; }
    }

    /// <summary>A worker status line: <c>task.status</c>, with its state, dedupe key and text.</summary>
    public sealed record Status : LedgerEvent
    {
        public required string State { get; init; }

        public required string? Key { get; init; }

        public required string? Text { get; init; }

        /// <summary>The record's own timestamp; it keys the alert when the status carries no key.</summary>
        public required DateTimeOffset? At { get; init; }
    }
}

/// <summary>
/// Reads FirstMate's fleet activity ledger, <c>state/fleet-ledger.jsonl</c> in the FirstMate home (spec 7.2.6).
/// The file is JSON Lines and append-only; the bridge reads new complete lines from its stored byte offset and
/// file identity, a partial last line waits for the next read, and a shrunk or replaced file is read from the
/// beginning again. A first read with no stored position takes the file's current end as its starting point and
/// returns no events, so nothing from before the bridge's first start alerts (spec 4.4).
/// </summary>
public sealed class FleetLedger
{
    public const int KnownVersion = 1;

    readonly string _path;

    public FleetLedger(string firstmateHome) => _path = System.IO.Path.Combine(firstmateHome, "state", "fleet-ledger.jsonl");

    public string FilePath => _path;

    public sealed record LedgerRead(IReadOnlyList<LedgerEvent> Events, LedgerPosition Position, int SkippedOtherVersions);

    /// <summary>Reads the complete lines after <paramref name="from"/>. Null when the ledger file does not exist yet.</summary>
    public LedgerRead? Read(LedgerPosition? from)
    {
        if (!File.Exists(_path))
            return null;

        var identity = FileIdentity.Of(_path);
        if (identity is null)
            return null;
        // A replaced or shrunk file is read from the beginning again; the alert history stops repeats.
        var start = from is not null && from.Device == identity.Device && from.Inode == identity.Inode ? from.Offset : 0;
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < start)
            start = 0;

        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, leaveOpen: true);
        var content = reader.ReadToEnd();

        // A partial last line is left for the next read.
        var complete = content.EndsWith('\n') ? content : content[..(content.LastIndexOf('\n') + 1)];
        var position = new LedgerPosition(identity.Device, identity.Inode, start + Encoding.UTF8.GetByteCount(complete));
        if (from is null)
            return new LedgerRead([], position, 0);

        var events = new List<LedgerEvent>();
        var skipped = 0;
        foreach (var line in complete.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (Parse(line))
            {
                case { Event: { } anEvent }:
                    events.Add(anEvent);
                    break;
                case { OtherVersion: true }:
                    skipped++;
                    break;
            }
        }

        return new LedgerRead(events, position, skipped);
    }

    readonly record struct ParsedLine(LedgerEvent? Event, bool OtherVersion);

    static ParsedLine Parse(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return default;
        }

        using (document)
        {
            var root = document.RootElement;
            var version = 0;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("v", out var versionMember))
                versionMember.TryGetInt32(out version);
            if (root.ValueKind != JsonValueKind.Object
                || version != KnownVersion
                || !root.TryGetProperty("event", out var eventName) || eventName.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("task", out var task) || task.ValueKind != JsonValueKind.String)
                return version != KnownVersion ? new ParsedLine(null, true) : default;

            var name = eventName.GetString()!;
            var taskId = task.GetString()!;
            LedgerEvent? parsed = name switch
            {
                "task.dispatched" => new LedgerEvent.Dispatched
                {
                    Task = taskId,
                    Kind = Member(root, "kind"),
                    Project = Member(root, "project"),
                },
                "task.pr_ready" => new LedgerEvent.PrReady { Task = taskId, PrUrl = Member(root, "pr") ?? "" },
                "task.status" when Member(root, "state") is { } state => new LedgerEvent.Status
                {
                    Task = taskId,
                    State = state,
                    Key = Member(root, "key"),
                    Text = Member(root, "text"),
                    At = Time(root, "ts"),
                },
                _ => null,
            };
            return new ParsedLine(parsed, false);
        }
    }

    static string? Member(JsonElement root, string name) =>
        root.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String ? member.GetString() : null;

    static DateTimeOffset? Time(JsonElement root, string name) =>
        root.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(member.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;
}

/// <summary>A file's identity: the device and inode the operating system gives it, so a replaced file is told apart from an appended one.</summary>
public static class FileIdentity
{
    // macOS's struct stat is larger than the members read here; the native call writes the whole struct,
    // so it gets a buffer big for it, never a smaller managed struct.
    const int StatBufferSize = 256;

    public static LedgerPosition? Of(string path)
    {
        var buffer = Marshal.AllocHGlobal(StatBufferSize);
        try
        {
            if (Stat(path, buffer) != 0)
                return null;
            return new LedgerPosition((long)Marshal.ReadInt32(buffer, 0), Marshal.ReadInt64(buffer, 8), 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libSystem", EntryPoint = "stat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int Stat(string path, IntPtr status);
}
