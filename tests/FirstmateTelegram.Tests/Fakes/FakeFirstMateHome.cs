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
        SetReady("ready-running-listening.json");
    }

    public string Home { get; }

    string Inbox => Path.Combine(Home, "state", "inbox");

    string Replies => Path.Combine(Inbox, ".replies");

    /// <summary>Makes <c>ready</c> print one of the captured fixtures.</summary>
    public void SetReady(string fixture) => File.WriteAllText(Path.Combine(Home, "fake", "ready.json"), Fixtures.Read(fixture));

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
    public string Reply(string noteId, string body)
    {
        Directory.CreateDirectory(Replies);
        var sequence = Interlocked.Increment(ref _replySequence);
        var at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(Replies, noteId), string.Create(CultureInfo.InvariantCulture, $"id={noteId}\nat={at}\nseq={sequence}\n--\n{body}\n"));
        return sequence.ToString("D12", CultureInfo.InvariantCulture);
    }

    /// <summary>What FirstMate's <c>drain --ack</c> does.</summary>
    public void Acknowledge(string noteId)
    {
        Directory.CreateDirectory(Path.Combine(Inbox, "handled"));
        File.Move(Path.Combine(Inbox, noteId + ".note"), Path.Combine(Inbox, "handled", noteId + ".note"));
    }

    /// <summary>A note from another inbox client, such as voice or a terminal tool.</summary>
    public string AddOtherNote(string body)
    {
        var noteId = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-other{Guid.NewGuid().ToString("N")[..4]}");
        File.WriteAllText(Path.Combine(Inbox, noteId + ".note"), $"id={noteId}\nat=2026-09-26T00:00:00Z\nsource=text\nannounce_marker=1\n--\n{body}\n");
        return noteId;
    }

    public sealed record InboxCall(IReadOnlyList<string> Arguments, string? StandardInput, string? FirstmateHome);

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
                node["fm_home"]?.GetValue<string>()))
            .ToList();
    }

    public IReadOnlyList<InboxCall> Calls(string subcommand) => Calls().Where(call => call.Arguments.Count > 0 && call.Arguments[0] == subcommand).ToList();

    public void Dispose() => _root.Dispose();
}
