using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

/// <summary>Parses output captured from FirstMate's real <c>fm-inbox.sh</c> (Fixtures/firstmate-e9a6675).</summary>
public sealed class FirstMateClientTests
{
    const string Home = "/Users/me/firstmate";

    [Fact]
    public async Task Note_exit_0_is_saved_and_woken()
    {
        var (client, runner) = Client(new ProcessResult(0, Fixtures.Read("note-created.json"), ""));

        var result = await client.SaveNoteAsync("tg:123456789:42:7", "body", CancellationToken.None);

        Assert.Equal((NoteSaveStatus.Saved, "created", false), (result.Status, result.Outcome, result.Acknowledged));
        Assert.False(string.IsNullOrEmpty(result.NoteId));
        var request = Assert.Single(runner.Requests);
        Assert.Equal(Home + "/bin/fm-inbox.sh", request.FileName);
        Assert.Equal(["note", "--request-id", "tg:123456789:42:7", "--json", "-"], request.Arguments);
        Assert.Equal("body", request.StandardInput);
        Assert.Equal(Home, request.Environment["FM_HOME"]);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
    }

    [Fact]
    public async Task Note_replay_returns_the_original_note()
    {
        var (created, _) = Client(new ProcessResult(0, Fixtures.Read("note-created.json"), ""));
        var (replayed, _) = Client(new ProcessResult(0, Fixtures.Read("note-replay.json"), ""));

        var first = await created.SaveNoteAsync("tg:123456789:42:7", "body", CancellationToken.None);
        var again = await replayed.SaveNoteAsync("tg:123456789:42:7", "body", CancellationToken.None);

        Assert.Equal("replay", again.Outcome);
        Assert.Equal(first.NoteId, again.NoteId);
    }

    [Fact]
    public async Task Note_exit_3_is_saved_without_a_wake()
    {
        var (client, _) = Client(new ProcessResult(3, Fixtures.Read("note-saved-not-announced.json"), "fm-inbox: note ... was NOT woken\n"));

        var result = await client.SaveNoteAsync("tg:123456789:42:8", "body", CancellationToken.None);

        Assert.Equal(NoteSaveStatus.SavedWithoutWake, result.Status);
        Assert.True(result.IsSaved);
    }

    [Theory]
    [InlineData(1, "", "fm-inbox: refusing to queue an empty note\n", FailureKind.ExitStatus, "refusing to queue an empty note")]
    [InlineData(2, "", "", FailureKind.ExitStatus, "fm-inbox.sh exited with status 2")]
    [InlineData(0, "not json", "", FailureKind.Unparseable, "FirstMate's records could not be read")]
    [InlineData(0, """{"schema":"fm-inbox-note.v2","outcome":"created","id":"x"}""", "", FailureKind.UnknownSchema, "newer format")]
    [InlineData(0, """{"schema":"fm-inbox-note.v1","outcome":"created"}""", "", FailureKind.Unparseable, "could not be read")]
    public async Task Any_other_note_result_is_a_failure_never_success(int exitCode, string output, string error, FailureKind kind, string reason)
    {
        var (client, _) = Client(new ProcessResult(exitCode, output, error));

        var result = await client.SaveNoteAsync("tg:1:2:3", "body", CancellationToken.None);

        Assert.Equal(NoteSaveStatus.Failed, result.Status);
        Assert.Equal(kind, result.Failure!.Kind);
        Assert.Contains(reason, result.Failure.Reason);
    }

    [Fact]
    public async Task A_timeout_is_a_failure()
    {
        var (client, _) = Client(ProcessResult.Timeout());

        var result = await client.SaveNoteAsync("tg:1:2:3", "body", CancellationToken.None);

        Assert.Equal(FailureKind.TimedOut, result.Failure!.Kind);
    }

    [Theory]
    [InlineData("announce-created.json")]
    [InlineData("announce-acknowledged.json")]
    public async Task Announce_created_or_replay_ends_the_repair(string fixture)
    {
        var (client, runner) = Client(new ProcessResult(0, Fixtures.Read(fixture), ""));

        var result = await client.AnnounceAsync("1790449901-hQJrNt", CancellationToken.None);

        Assert.True(result.Repaired);
        Assert.Equal(["announce", "--json", "1790449901-hQJrNt"], Assert.Single(runner.Requests).Arguments);
    }

    [Fact]
    public async Task Announce_that_still_cannot_wake_is_not_a_repair()
    {
        var (client, _) = Client(new ProcessResult(3, Fixtures.Read("note-saved-not-announced.json"), ""));

        Assert.False((await client.AnnounceAsync("x", CancellationToken.None)).Repaired);
    }

    [Fact]
    public async Task Receipts_give_replies_in_cursor_order_and_the_pending_and_handled_notes()
    {
        var (client, runner) = Client(new ProcessResult(0, Fixtures.Read("receipts.json"), ""));

        var result = await client.ReadReceiptsAsync("", CancellationToken.None);

        var receipts = result.Receipts!;
        var reply = Assert.Single(receipts.Replies);
        Assert.Equal(("1790449900-5GMkE4", "The release is on track.\nTests pass.", "000000000001"), (reply.NoteId, reply.Body, reply.Cursor));
        Assert.Equal("000000000001", receipts.ReplyCursor);
        Assert.False(receipts.HasMoreReplies);
        Assert.Contains("1790449901-hQJrNt", receipts.PendingNoteIds);
        Assert.Contains("1790449900-5GMkE4", receipts.HandledNoteIds);
        Assert.True(receipts.IsPendingListComplete);
        Assert.Equal(["receipts"], Assert.Single(runner.Requests).Arguments);
    }

    [Fact]
    public async Task Receipts_after_a_cursor_pass_it_and_keep_it_when_nothing_is_new()
    {
        var (client, runner) = Client(new ProcessResult(0, Fixtures.Read("receipts-after-cursor.json"), ""));

        var result = await client.ReadReceiptsAsync("000000000001", CancellationToken.None);

        Assert.Empty(result.Receipts!.Replies);
        Assert.Equal("000000000001", result.Receipts.ReplyCursor);
        Assert.Equal(["receipts", "--after", "000000000001"], Assert.Single(runner.Requests).Arguments);
    }

    [Fact]
    public async Task Receipts_report_replies_and_pending_notes_left_out_by_the_bound()
    {
        const string output = """
            {"schema":"fm-inbox-receipts.v1","pending":[],"handled":[],"replies":[{"id":"a","at":"t","body":"b","cursor":"000000000020"}],
             "reply_cursor":"000000000020","omitted":[{"surface":"pending notes omitted by bound: 4","reveal":"pass --all-pending"},{"surface":"replies omitted by bound: 3","reveal":"pass --all-replies"}]}
            """;
        var (client, _) = Client(new ProcessResult(0, output, ""));

        var receipts = (await client.ReadReceiptsAsync("", CancellationToken.None)).Receipts!;

        Assert.True(receipts.HasMoreReplies);
        Assert.False(receipts.IsPendingListComplete);
    }

    [Theory]
    [InlineData("ready-not-running.json", Readiness.NotRunning, "present")]
    [InlineData("ready-not-running-away.json", Readiness.NotRunning, "away")]
    [InlineData("ready-not-picking-up.json", Readiness.NotPickingUp, "present")]
    [InlineData("ready-running-listening.json", Readiness.RunningListening, "present")]
    [InlineData("ready-running-away.json", Readiness.RunningListening, "away")]
    [InlineData("ready-listening-unconfirmed.json", Readiness.RunningListeningUnconfirmed, "present")]
    public async Task Ready_gives_running_listening_and_posture(string fixture, Readiness readiness, string posture)
    {
        var (client, runner) = Client(new ProcessResult(0, Fixtures.Read(fixture), ""));

        var result = await client.ReadReadyAsync(CancellationToken.None);

        Assert.Equal(readiness, ReadinessRules.Classify(result));
        Assert.Equal(posture, result.Reading!.PostureState);
        Assert.Equal(posture == "away", result.IsAway);
        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(runner.Requests).Timeout);
    }

    [Theory]
    [InlineData("""{"schema":"fm-primary-ready.v1","lock":{"state":"unreadable"},"wake_consumer":{"state":"unknown"},"posture":{"state":"unknown"},"can_receive":"unknown"}""")]
    [InlineData("""{"schema":"fm-primary-ready.v2","lock":{"state":"held"},"wake_consumer":{"state":"healthy"},"posture":{"state":"present"},"can_receive":true}""")]
    [InlineData("garbage")]
    public async Task Unreadable_or_unknown_readiness_is_unknown(string output)
    {
        var (client, _) = Client(new ProcessResult(0, output, ""));

        Assert.Equal(Readiness.Unknown, ReadinessRules.Classify(await client.ReadReadyAsync(CancellationToken.None)));
    }

    [Fact]
    public void Todays_FirstMate_has_no_explicit_return_subcommand()
    {
        Assert.Contains("unknown subcommand: return", Fixtures.Read("return-unknown-subcommand.stderr"));
    }

    static (FirstMateClient Client, ScriptedProcessRunner Runner) Client(ProcessResult result)
    {
        var runner = new ScriptedProcessRunner(_ => result);
        return (new FirstMateClient(runner, Home), runner);
    }
}
