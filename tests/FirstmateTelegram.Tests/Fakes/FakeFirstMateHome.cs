using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests.Fakes;

/// <summary>
/// A throwaway FirstMate home whose <c>bin/fm-inbox.sh</c> is the stub script. It keeps a real notes folder, so
/// request-id replay behaves as in FirstMate, and it records every call. Nothing here touches a real FirstMate home.
/// </summary>
public sealed class FakeFirstMateHome : IDisposable
{
    readonly TempDirectory _root = new();
    int _replySequence;

    public FakeFirstMateHome()
    {
        Home = _root.Combine("firstmate");
        Directory.CreateDirectory(Path.Combine(Home, "bin"));
        Directory.CreateDirectory(Path.Combine(Home, "fake", "faults"));
        Directory.CreateDirectory(Inbox);
        var script = Path.Combine(Home, "bin", "fm-inbox.sh");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fakes", "fm-inbox-stub.py"), script);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var name in new[] { "fm-bearings-snapshot.sh", "fm-fleet-snapshot.sh" })
        {
            var snapshot = Path.Combine(Home, "bin", name);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fakes", "fm-snapshot-stub.py"), snapshot);
            File.SetUnixFileMode(snapshot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        SetReady("ready-running-listening.json");
        SetBearings("bearings.json");
        SetFleetSnapshot("fleet-snapshot.json");
    }

    public string Home { get; }

    string Inbox => Path.Combine(Home, "state", "inbox");

    string Replies => Path.Combine(Inbox, ".replies");

    /// <summary>Makes <c>ready</c> print one of the captured fixtures.</summary>
    public void SetReady(string fixture) => File.WriteAllText(Path.Combine(Home, "fake", "ready.json"), Fixtures.Read(fixture));

    /// <summary>Makes <c>fm-bearings-snapshot.sh --json</c> print one of the fixtures.</summary>
    public void SetBearings(string fixture) => File.WriteAllText(Path.Combine(Home, "fake", "bearings.json"), Fixtures.Read(fixture));

    /// <summary>Makes <c>fm-fleet-snapshot.sh --json</c> print one of the fixtures.</summary>
    public void SetFleetSnapshot(string fixture) => File.WriteAllText(Path.Combine(Home, "fake", "fleet.json"), Fixtures.Read(fixture));

    /// <summary>Scripts the next calls of <paramref name="subcommand"/>; see the stub's header for the fault shapes.</summary>
    public void Script(string subcommand, params object[] faults) =>
        File.WriteAllText(Path.Combine(Home, "fake", "faults", subcommand + ".json"), JsonSerializer.Serialize(faults));

    /// <summary>Note files, pending and handled, as FirstMate would hold them.</summary>
    public IReadOnlyList<string> NoteIds()
    {
        var pending = Directory.GetFiles(Inbox, "*.note");
        var handledFolder = Path.Combine(Inbox, "handled");
        var handled = Directory.Exists(handledFolder) ? Directory.GetFiles(handledFolder, "*.note") : [];
        return [.. pending.Concat(handled).Select(path => Path.GetFileNameWithoutExtension(path)).Order(StringComparer.Ordinal)];
    }

    public string NoteBody(string noteId)
    {
        var path = Path.Combine(Inbox, noteId + ".note");
        if (!File.Exists(path))
            path = Path.Combine(Inbox, "handled", noteId + ".note");
        var text = File.ReadAllText(path);
        return text[(text.IndexOf("\n--\n", StringComparison.Ordinal) + 4)..];
    }

    public string NoteIdFor(string requestId) => File.ReadAllText(Path.Combine(Inbox, ".requests", requestId)).Trim();

    public bool IsAnnounced(string noteId) => File.Exists(Path.Combine(Inbox, ".announced", noteId));

    /// <summary>What FirstMate's <c>reply</c> records: one reply per note, stamped with the next sequence.</summary>
    public string Reply(string noteId, string body, DateTimeOffset? at = null)
    {
        Directory.CreateDirectory(Replies);
        var sequence = Interlocked.Increment(ref _replySequence);
        var stamp = (at ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(Replies, noteId), string.Create(CultureInfo.InvariantCulture, $"id={noteId}\nat={stamp}\nseq={sequence}\n--\n{body}\n"));
        return sequence.ToString("D12", CultureInfo.InvariantCulture);
    }

    /// <summary>What FirstMate's <c>drain --ack</c> does.</summary>
    public void Acknowledge(string noteId)
    {
        Directory.CreateDirectory(Path.Combine(Inbox, "handled"));
        File.Move(Path.Combine(Inbox, noteId + ".note"), Path.Combine(Inbox, "handled", noteId + ".note"));
    }

    /// <summary>A note from another inbox client, such as voice or a terminal tool. Announced notes count towards the not-responding check.</summary>
    public string AddOtherNote(string body, bool announced = true)
    {
        var noteId = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-other{Guid.NewGuid().ToString("N")[..4]}");
        File.WriteAllText(Path.Combine(Inbox, noteId + ".note"), $"id={noteId}\nat=2026-09-26T00:00:00Z\nsource=text\nannounce_marker=1\n--\n{body}\n");
        if (announced)
        {
            Directory.CreateDirectory(Path.Combine(Inbox, ".announced"));
            File.WriteAllText(Path.Combine(Inbox, ".announced", noteId), "2026-09-26T00:00:00Z\n");
        }
        return noteId;
    }

    public sealed record InboxCall(IReadOnlyList<string> Arguments, string? StandardInput, string? FirstmateHome, string? Script = "fm-inbox.sh");

    public IReadOnlyList<InboxCall> Calls()
    {
        var path = Path.Combine(Home, "fake", "calls.jsonl");
        if (!File.Exists(path))
            return [];
        return File.ReadAllLines(path)
            .Select(line => JsonNode.Parse(line)!)
            .Select(node => new InboxCall(
                node["argv"]!.AsArray().Select(argument => argument!.GetValue<string>()).ToList(),
                node["stdin"]?.GetValue<string>(),
                node["fm_home"]?.GetValue<string>(),
                node["script"]?.GetValue<string>() ?? "fm-inbox.sh"))
            .ToList();
    }

    public IReadOnlyList<InboxCall> Calls(string subcommand) => Calls().Where(call => call.Script == "fm-inbox.sh" && call.Arguments.Count > 0 && call.Arguments[0] == subcommand).ToList();

    /// <summary>Calls of one of the snapshot scripts, by the fixture key the stub records (<c>bearings</c> or <c>fleet</c>).</summary>
    public IReadOnlyList<InboxCall> SnapshotCalls(string script) => Calls().Where(call => call.Script == script).ToList();

    public void Dispose() => _root.Dispose();
}
