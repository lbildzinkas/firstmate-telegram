using FirstmateTelegram.Telegram;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

/// <summary>
/// Each test stops the bridge at one step of the request or reply path, starts a new bridge over the same state,
/// and checks that no request is lost or duplicated and that a reply is never skipped.
/// </summary>
public sealed class CrashWindowTests
{
    [Fact]
    public async Task Crash_after_the_note_is_saved_but_before_the_bridge_records_it_leaves_exactly_one_note()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "exactly once please");
        var writer = new CrashingStateWriter();
        var crashed = await harness.StartInstance(writer).InitializedAsync();
        writer.CrashOn("requests.json");

        await Assert.ThrowsAsync<SimulatedCrashException>(crashed.PollAsync);
        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        Assert.Equal(1, harness.Telegram.PendingUpdateCount());

        var restarted = await harness.StartInstance().InitializedAsync();
        await restarted.PollAsync();
        await restarted.PollAsync();

        Assert.Equal([noteId], harness.FirstMate.NoteIds());
        Assert.Equal(noteId, Assert.Single(restarted.Store.Requests.Values).NoteId);
        Assert.Equal(2, harness.FirstMate.Calls("note").Count);
        Assert.Equal(0, harness.Telegram.PendingUpdateCount());
        Assert.All(harness.Telegram.Reactions(), reaction => Assert.Equal(messageId, reaction.MessageId));
    }

    [Fact]
    public async Task Crash_after_the_note_and_the_request_map_but_before_the_position_is_saved_leaves_exactly_one_note()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "exactly once please");
        var writer = new CrashingStateWriter();
        var crashed = await harness.StartInstance(writer).InitializedAsync();
        writer.CrashOn("state.json");

        await Assert.ThrowsAsync<SimulatedCrashException>(crashed.PollAsync);
        var noteId = Assert.Single(harness.FirstMate.NoteIds());

        var restarted = await harness.StartInstance().InitializedAsync();
        Assert.Equal(noteId, Assert.Single(restarted.Store.Requests.Values).NoteId);
        await restarted.PollAsync();
        await restarted.PollAsync();

        Assert.Equal([noteId], harness.FirstMate.NoteIds());
        Assert.Single(restarted.Store.Requests);
        Assert.Equal(0, harness.Telegram.PendingUpdateCount());
    }

    [Fact]
    public async Task Crash_after_the_position_is_saved_but_before_Telegram_is_told_confirms_on_restart_without_redelivery()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "exactly once please");
        var crashed = await harness.StartInstance().InitializedAsync();
        await crashed.PollAsync();
        Assert.Equal(1, harness.Telegram.PendingUpdateCount());

        var restarted = await harness.StartInstance().InitializedAsync();
        await restarted.PollAsync();

        Assert.Single(harness.FirstMate.NoteIds());
        Assert.Single(harness.FirstMate.Calls("note"));
        Assert.Equal(0, harness.Telegram.PendingUpdateCount());
    }

    [Fact]
    public async Task Crash_between_a_note_saved_without_a_wake_and_the_repair_repairs_from_the_saved_state()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.Script("note", new { no_wake = true });
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "wake FirstMate once");
        var crashed = await harness.StartInstance().InitializedAsync();
        await crashed.PollAsync();
        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        Assert.False(harness.FirstMate.IsAnnounced(noteId));

        var restarted = await harness.StartInstance().InitializedAsync();
        await restarted.PollAsync();

        Assert.True(harness.FirstMate.IsAnnounced(noteId));
        Assert.Single(harness.FirstMate.Calls("announce"));
        Assert.Single(harness.FirstMate.Calls("note"));
    }

    [Fact]
    public async Task Crash_after_a_reply_part_is_sent_but_before_it_is_recorded_repeats_at_most_that_one_part()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "long answer please");
        var writer = new CrashingStateWriter();
        var crashed = await harness.StartInstance(writer).InitializedAsync();
        await crashed.PollAsync();
        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        var cursor = harness.FirstMate.Reply(noteId, string.Join("\n\n", Enumerable.Range(1, 3).Select(index => $"part {index} " + new string('y', 4000))));
        writer.CrashOn("requests.json", skip: 1);

        await Assert.ThrowsAsync<SimulatedCrashException>(crashed.ForwardAsync);
        Assert.Equal(2, harness.Telegram.SentMessages().Count);
        Assert.Equal("", crashed.Store.State.ReplyCursor);

        var restarted = await harness.StartInstance().InitializedAsync();
        await restarted.ForwardAsync();

        var parts = harness.Telegram.SentMessages().Select(message => message.Text[..5]).ToList();
        Assert.Equal(["(1/3)", "(2/3)", "(2/3)", "(3/3)"], parts);
        Assert.All(harness.Telegram.SentMessages(), message => Assert.Equal(messageId, message.ReplyToMessageId));
        Assert.Equal(cursor, restarted.Store.State.ReplyCursor);
        Assert.Equal(BotReactions.DefaultReplied, Assert.Single(harness.Telegram.Reactions()[^1].Emoji));
    }

    [Fact]
    public async Task Crash_after_the_last_part_but_before_the_cursor_is_stored_resends_nothing()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "short answer please");
        var writer = new CrashingStateWriter();
        var crashed = await harness.StartInstance(writer).InitializedAsync();
        await crashed.PollAsync();
        var cursor = harness.FirstMate.Reply(Assert.Single(harness.FirstMate.NoteIds()), "Short answer.");
        writer.CrashOn("state.json");

        await Assert.ThrowsAsync<SimulatedCrashException>(crashed.ForwardAsync);
        Assert.Single(harness.Telegram.SentMessages());

        var restarted = await harness.StartInstance().InitializedAsync();
        await restarted.ForwardAsync();

        Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal(cursor, restarted.Store.State.ReplyCursor);
    }

    [Fact]
    public async Task A_reply_that_Telegram_refuses_for_a_while_is_retried_and_never_skipped()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "answer me");
        var bridge = await harness.StartInstance().InitializedAsync();
        await bridge.PollAsync();
        var cursor = harness.FirstMate.Reply(Assert.Single(harness.FirstMate.NoteIds()), "Here it is.");
        harness.Telegram.FailNext("sendMessage", 502, "Bad Gateway", times: 4);

        await bridge.ForwardAsync();

        Assert.Equal(5, harness.Telegram.CallsTo("sendMessage").Count);
        Assert.Equal("Here it is.", Assert.Single(harness.Telegram.SentMessages().DistinctBy(message => message.Text)).Text);
        Assert.Equal(cursor, bridge.Store.State.ReplyCursor);
    }

    [Fact]
    public async Task Crash_after_an_alert_is_sent_but_before_its_message_id_is_recorded_never_sends_it_twice()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var writer = new CrashingStateWriter();
        var crashed = await harness.StartInstance(writer).InitializedAsync();
        await crashed.WatchAsync();
        writer.CrashOn("alerts.json", skip: 1); // the dedupe key was written; the message id was not

        harness.FirstMate.AppendLedger("""{"v":1,"ts":"2026-09-26T14:10:00Z","event":"task.pr_ready","task":"ship-task","pr":"https://github.com/acme/webapp/pull/7"}""");
        await Assert.ThrowsAsync<SimulatedCrashException>(crashed.WatchAsync);
        Assert.Single(harness.Telegram.SentMessages());

        var restarted = await harness.StartInstance().InitializedAsync();
        await restarted.WatchAsync();
        await restarted.WatchAsync();

        Assert.Single(harness.Telegram.SentMessages()); // exactly one alert per event, even across the crash
    }
}
