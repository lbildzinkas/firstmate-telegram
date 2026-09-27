using FirstmateTelegram.Bridge;
using FirstmateTelegram.State;
using FirstmateTelegram.Telegram;
using FirstmateTelegram.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace FirstmateTelegram.Tests;

/// <summary>The /ping live round trip: a real FirstMate turn with a timeout, told honestly in every outcome (spec 4.3.3).</summary>
public sealed class LivePingTests
{
    static FakeTimeProvider UtcTime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 14, 5, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return time;
    }

    [Fact]
    public async Task A_live_ping_round_trips_through_FirstMate_and_reports_the_time()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = UtcTime();
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "/ping live");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal("Asking FirstMate directly (up to 60 s).", sent.Text);
        Assert.Equal(messageId, sent.ReplyToMessageId);
        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        Assert.StartsWith(BridgeTexts.LivePingBody + "\n\n" + NoteBody.FooterHeading + "\n", harness.FirstMate.NoteBody(noteId));
        Assert.DoesNotContain("The user is still away", harness.FirstMate.NoteBody(noteId));
        var entry = Assert.Single(bridge.Store.Requests.Values);
        Assert.Equal((RequestKinds.LivePing, noteId, messageId), (entry.Kind, entry.NoteId, entry.MessageId));
        Assert.Equal(entry.SavedAt + TimeSpan.FromSeconds(60), entry.LiveDeadlineAt);
        Assert.Equal((messageId, BotReactions.Received), (Assert.Single(harness.Telegram.Reactions()).MessageId, Assert.Single(Assert.Single(harness.Telegram.Reactions()).Emoji)));
        Assert.True(bridge.Forwarder.HasWaitingRequests);

        time.Advance(TimeSpan.FromSeconds(14));
        var cursor = harness.FirstMate.Reply(noteId, "Here and listening.", at: time.GetUtcNow());
        await bridge.ForwardAsync();

        var answer = harness.Telegram.SentMessages()[^1];
        Assert.Equal("Live: FirstMate answered in 14 s.", answer.Text);
        Assert.Equal(messageId, answer.ReplyToMessageId);
        Assert.Equal(cursor, bridge.Store.State.ReplyCursor);
        Assert.NotNull(bridge.Store.Requests[noteId].RepliedAt);
        Assert.False(bridge.Forwarder.HasWaitingRequests);
        Assert.Equal(BotReactions.DefaultReplied, Assert.Single(harness.Telegram.Reactions()[^1].Emoji));

        await bridge.ForwardAsync();
        Assert.Equal(2, harness.Telegram.SentMessages().Count);
    }

    [Fact]
    public async Task A_live_ping_that_times_out_says_not_available_and_the_reply_that_comes_late_is_marked_late()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = UtcTime();
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "/ping live");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();
        await bridge.PollAsync();
        var noteId = Assert.Single(harness.FirstMate.NoteIds());

        time.Advance(TimeSpan.FromSeconds(60));
        await bridge.ForwardAsync();

        var timeout = harness.Telegram.SentMessages()[^1];
        Assert.Equal(
            "Not available: FirstMate did not answer within 60 s.\n"
            + "FirstMate: running, listening\n"
            + "Away mode: off\n"
            + "Claude quota: unknown\n"
            + "Bridge: up 1 min",
            timeout.Text);
        Assert.Equal(messageId, timeout.ReplyToMessageId);
        Assert.False(bridge.Forwarder.HasWaitingRequests);

        var cursor = harness.FirstMate.Reply(noteId, "Better late.", at: time.GetUtcNow() + TimeSpan.FromMinutes(4));
        await bridge.ForwardAsync();

        var late = harness.Telegram.SentMessages()[^1];
        Assert.Equal("FirstMate answered the live ping after 5 min.", late.Text);
        Assert.Equal(messageId, late.ReplyToMessageId);
        Assert.Equal(cursor, bridge.Store.State.ReplyCursor);
        Assert.NotNull(bridge.Store.Requests[noteId].RepliedAt);

        // The timeout is told once, even after another receipts round.
        await bridge.ForwardAsync();
        Assert.Equal(3, harness.Telegram.SentMessages().Count);
    }

    [Fact]
    public async Task A_live_ping_in_away_mode_carries_the_away_footer_and_explains_the_timeout()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady("ready-running-away.json");
        var time = UtcTime();
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "/ping live");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();

        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        Assert.EndsWith("The user is still away: this request does not end away mode.\n", harness.FirstMate.NoteBody(noteId));

        time.Advance(TimeSpan.FromSeconds(60));
        await bridge.ForwardAsync();

        Assert.Equal(
            "Not available: FirstMate did not answer within 60 s.\n"
            + "FirstMate: running, listening\n"
            + "Away mode: on\n"
            + "Claude quota: unknown\n"
            + "Bridge: up 1 min\n"
            + "In away mode, FirstMate can take a couple of minutes to pick up a request.",
            harness.Telegram.SentMessages()[^1].Text);
    }

    [Theory]
    [InlineData("ready-not-running.json", "Not running; requests will queue", "FirstMate: not running")]
    [InlineData("ready-not-running-away.json", "Not running; requests will queue", "FirstMate: not running")]
    [InlineData("ready-not-picking-up.json", "Running, but not picking up requests; requests will queue", "FirstMate: running, not picking up requests")]
    public async Task No_live_check_is_sent_while_FirstMate_is_not_running_or_not_picking_up(string readyFixture, string verdict, string firstMateLine)
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady(readyFixture);
        var time = UtcTime();
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "/ping live");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.StartsWith(verdict + "\n", sent.Text);
        Assert.EndsWith("\n" + firstMateLine + "\nAway mode: " + (readyFixture.Contains("away") ? "on" : "off") + "\nClaude quota: unknown\nBridge: up 1 s\nNo live check was sent, so FirstMate won't answer a stale ping later.", sent.Text);
        Assert.Equal(messageId, sent.ReplyToMessageId);
        Assert.Empty(harness.FirstMate.Calls("note"));
        Assert.Empty(harness.Telegram.Reactions());
    }

    [Fact]
    public async Task A_live_ping_note_that_cannot_be_saved_is_retried_like_a_request()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = UtcTime();
        harness.FirstMate.Script("note", new { exit = 1, stderr = "inbox hiccup" });
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "/ping live");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();

        Assert.Empty(harness.FirstMate.NoteIds());
        Assert.Empty(harness.Telegram.SentMessages());
        Assert.Equal(0, harness.Telegram.ConfirmedOffset);
        Assert.Equal(1, harness.Telegram.PendingUpdateCount());

        time.Advance(TimeSpan.FromSeconds(2));
        await bridge.PollAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal("Asking FirstMate directly (up to 60 s).", sent.Text);
        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        Assert.StartsWith(BridgeTexts.LivePingBody, harness.FirstMate.NoteBody(noteId));
        Assert.Equal(RequestKinds.LivePing, Assert.Single(bridge.Store.Requests.Values).Kind);
        Assert.Equal(2, harness.FirstMate.Calls("note").Count);
    }

    [Fact]
    public async Task A_live_ping_waits_in_order_behind_a_request_that_cannot_be_saved()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = UtcTime();
        harness.FirstMate.Script("note", new { exit = 1, stderr = "inbox hiccup" });
        var request = harness.Telegram.EnqueueText(BridgeHarness.UserId, "do this first");
        var ping = harness.Telegram.EnqueueText(BridgeHarness.UserId, "/ping live");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();

        Assert.Empty(harness.FirstMate.NoteIds());
        Assert.Empty(harness.Telegram.SentMessages());

        time.Advance(TimeSpan.FromSeconds(2));
        await bridge.PollAsync();

        Assert.Equal(2, harness.FirstMate.NoteIds().Count);
        var requestNote = harness.FirstMate.NoteIdFor(BridgeHarness.RequestIdFor(request));
        var pingNote = harness.FirstMate.NoteIdFor(BridgeHarness.RequestIdFor(ping));
        var callOrder = harness.FirstMate.Calls("note").Select(call => call.Arguments[2]).Distinct().ToList();
        Assert.Equal([BridgeHarness.RequestIdFor(request), BridgeHarness.RequestIdFor(ping)], callOrder);
        Assert.Equal("Asking FirstMate directly (up to 60 s).", harness.Telegram.SentMessages()[^1].Text);
        Assert.Equal(RequestKinds.Request, bridge.Store.Requests[requestNote].Kind);
        Assert.Equal(RequestKinds.LivePing, bridge.Store.Requests[pingNote].Kind);
    }

    [Fact]
    public async Task A_live_ping_FirstMate_acknowledges_without_a_reply_still_times_out()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = UtcTime();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/ping live");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();
        await bridge.PollAsync();
        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        harness.FirstMate.Acknowledge(noteId);

        await bridge.ForwardAsync();
        Assert.Single(harness.Telegram.SentMessages());

        time.Advance(TimeSpan.FromSeconds(60));
        await bridge.ForwardAsync();

        Assert.StartsWith("Not available: FirstMate did not answer within 60 s.\nFirstMate: ", harness.Telegram.SentMessages()[^1].Text);
    }

    [Fact]
    public async Task A_live_ping_timeout_is_told_once_even_across_a_restart()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = UtcTime();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/ping live");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();
        await bridge.PollAsync();
        time.Advance(TimeSpan.FromSeconds(60));
        await bridge.ForwardAsync();
        Assert.Equal(2, harness.Telegram.SentMessages().Count);

        var restarted = await harness.StartInstance(time: time).InitializedAsync();
        time.Advance(TimeSpan.FromSeconds(30));
        await restarted.ForwardAsync();

        Assert.Equal(2, harness.Telegram.SentMessages().Count);
        var entry = Assert.Single(restarted.Store.Requests.Values);
        Assert.NotNull(entry.LiveTimeoutToldAt);
        Assert.Null(entry.RepliedAt);
    }

    [Fact]
    public async Task A_live_ping_saved_without_a_wake_is_repaired_and_still_answered()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = UtcTime();
        harness.FirstMate.Script("note", new { no_wake = true });
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/ping live");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();
        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        Assert.False(harness.FirstMate.IsAnnounced(noteId));

        await bridge.PollAsync();

        Assert.True(harness.FirstMate.IsAnnounced(noteId));
        Assert.Equal(["announce", "--json", noteId], Assert.Single(harness.FirstMate.Calls("announce")).Arguments);

        time.Advance(TimeSpan.FromSeconds(10));
        var cursor = harness.FirstMate.Reply(noteId, "Awake now.", at: time.GetUtcNow());
        await bridge.ForwardAsync();
        Assert.Equal("Live: FirstMate answered in 10 s.", harness.Telegram.SentMessages()[^1].Text);
        Assert.Equal(cursor, bridge.Store.State.ReplyCursor);
    }
}
