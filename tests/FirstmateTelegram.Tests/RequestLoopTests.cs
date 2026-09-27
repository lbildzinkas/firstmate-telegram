using FirstmateTelegram.Bridge;
using FirstmateTelegram.Telegram;
using FirstmateTelegram.Tests.Fakes;
using FirstmateTelegram.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace FirstmateTelegram.Tests;

public sealed class RequestLoopTests
{
    [Fact]
    public async Task A_request_from_the_paired_user_becomes_one_inbox_note_with_the_eyes_reaction()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "What is the status of the release?");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        var requestId = BridgeHarness.RequestIdFor(messageId);
        Assert.Equal(noteId, harness.FirstMate.NoteIdFor(requestId));
        var call = Assert.Single(harness.FirstMate.Calls("note"));
        Assert.Equal(["note", "--request-id", requestId, "--json", "-"], call.Arguments);
        Assert.Equal(harness.FirstMate.Home, call.FirstmateHome);
        Assert.StartsWith("What is the status of the release?\n\n" + NoteBody.FooterHeading + "\n", call.StandardInput);
        Assert.DoesNotContain("The user is still away", call.StandardInput);

        var reaction = Assert.Single(harness.Telegram.Reactions());
        Assert.Equal((BridgeHarness.UserId, messageId, BotReactions.Received), (reaction.ChatId, reaction.MessageId, Assert.Single(reaction.Emoji)));
        Assert.Empty(harness.Telegram.SentMessages());

        var entry = Assert.Single(bridge.Store.Requests.Values);
        Assert.Equal((noteId, requestId, BridgeHarness.UserId, messageId, true), (entry.NoteId, entry.RequestId, entry.ChatId, entry.MessageId, entry.Announced));

        await bridge.PollAsync();
        Assert.Equal(0, harness.Telegram.PendingUpdateCount());
        Assert.Single(harness.FirstMate.Calls("note"));
    }

    [Fact]
    public async Task The_footer_says_the_user_is_still_away_while_away_mode_is_on()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady("ready-running-away.json");
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "Any decisions for me?");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        var body = Assert.Single(harness.FirstMate.Calls("note")).StandardInput!;
        Assert.EndsWith("The user is still away: this request does not end away mode.\n", body);
        Assert.Empty(harness.Telegram.SentMessages());
    }

    [Fact]
    public async Task FirstMates_reply_comes_back_threaded_as_plain_text_and_then_gets_the_replied_reaction()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "status?");
        var bridge = await harness.StartInstance().InitializedAsync();
        await bridge.PollAsync();
        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        Assert.True(bridge.Forwarder.HasWaitingRequests);

        var cursor = harness.FirstMate.Reply(noteId, "All green. See https://github.com/acme/webapp/pull/7 *not markdown*");
        await bridge.ForwardAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal("All green. See https://github.com/acme/webapp/pull/7 *not markdown*", sent.Text);
        Assert.Equal(BridgeHarness.UserId, sent.ChatId);
        Assert.Equal(messageId, sent.ReplyToMessageId);
        Assert.True(sent.AllowSendingWithoutReply);
        Assert.True(sent.LinkPreviewDisabled);
        Assert.Null(sent.ParseMode);

        var reactions = harness.Telegram.Reactions();
        Assert.Equal([BotReactions.Received, BotReactions.DefaultReplied], reactions.Select(reaction => Assert.Single(reaction.Emoji)));
        Assert.Equal(cursor, bridge.Store.State.ReplyCursor);
        Assert.NotNull(bridge.Store.Requests[noteId].RepliedAt);
        Assert.False(bridge.Forwarder.HasWaitingRequests);

        await bridge.ForwardAsync();
        Assert.Single(harness.Telegram.SentMessages());
    }

    [Fact]
    public async Task The_replied_reaction_follows_the_configuration()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Config = harness.Config with { RepliedReaction = "\U0001F44D" };
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "thanks");
        var bridge = await harness.StartInstance().InitializedAsync();
        await bridge.PollAsync();
        harness.FirstMate.Reply(Assert.Single(harness.FirstMate.NoteIds()), "You're welcome.");

        await bridge.ForwardAsync();

        Assert.Equal("\U0001F44D", Assert.Single(harness.Telegram.Reactions()[^1].Emoji));
    }

    [Fact]
    public async Task A_reply_over_Telegrams_limit_is_split_into_numbered_parts_each_threaded_to_the_request()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "full report please");
        var bridge = await harness.StartInstance().InitializedAsync();
        await bridge.PollAsync();
        var paragraphs = Enumerable.Range(1, 30).Select(index => $"Paragraph {index}: " + new string('x', 300)).ToList();
        harness.FirstMate.Reply(Assert.Single(harness.FirstMate.NoteIds()), string.Join("\n\n", paragraphs));

        await bridge.ForwardAsync();

        var sent = harness.Telegram.SentMessages();
        Assert.Equal(3, sent.Count);
        for (var index = 0; index < sent.Count; index++)
        {
            Assert.StartsWith($"({index + 1}/3)\n", sent[index].Text);
            Assert.True(sent[index].Text.Length <= MessageSplitter.TelegramLimit);
            Assert.Equal(messageId, sent[index].ReplyToMessageId);
        }

        var rebuilt = sent.SelectMany(message => message.Text[(message.Text.IndexOf('\n') + 1)..].Split("\n\n")).ToList();
        Assert.Equal(paragraphs, rebuilt);
        Assert.Equal(BotReactions.DefaultReplied, Assert.Single(harness.Telegram.Reactions()[^1].Emoji));
        Assert.Equal(3, bridge.Store.Requests.Values.Single().ReplyPartsSent);
    }

    [Fact]
    public async Task Replies_to_other_inbox_notes_only_advance_the_cursor()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var bridge = await harness.StartInstance().InitializedAsync();
        var other = harness.FirstMate.AddOtherNote("a note from the voice relay");
        var cursor = harness.FirstMate.Reply(other, "answer for the voice relay");

        await bridge.ForwardAsync();

        Assert.Empty(harness.Telegram.SentMessages());
        Assert.Equal(cursor, bridge.Store.State.ReplyCursor);
    }

    [Fact]
    public async Task Replies_left_out_by_FirstMates_bound_are_read_straight_away_from_the_new_cursor()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var bridge = await harness.StartInstance().InitializedAsync();
        for (var index = 0; index < 23; index++)
            harness.FirstMate.Reply(harness.FirstMate.AddOtherNote($"other {index}"), $"reply {index}");
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "mine");
        await bridge.PollAsync();
        var mine = harness.FirstMate.NoteIdFor(BridgeHarness.RequestIdFor(messageId));
        var cursor = harness.FirstMate.Reply(mine, "the answer to mine");

        await bridge.ForwardAsync();

        Assert.Equal("the answer to mine", Assert.Single(harness.Telegram.SentMessages()).Text);
        Assert.Equal(cursor, bridge.Store.State.ReplyCursor);
        Assert.Equal(2, harness.FirstMate.Calls("receipts").Count);
    }

    [Fact]
    public async Task Strangers_groups_and_spoofed_chats_are_ignored_silently_and_logged_by_sender_id_only()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Telegram.EnqueueText(777, "stranger secret text");
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "group secret text", chatId: -100200, chatType: "group");
        harness.Telegram.EnqueueText(888, "spoofed secret text", chatId: BridgeHarness.UserId);
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "channel-like secret text", chatId: BridgeHarness.UserId, chatType: "supergroup");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();
        await bridge.PollAsync();

        Assert.Empty(harness.FirstMate.NoteIds());
        Assert.Empty(harness.Telegram.SentMessages());
        Assert.Empty(harness.Telegram.Reactions());
        Assert.Equal(0, harness.Telegram.PendingUpdateCount());
        var log = harness.Logging.Text();
        Assert.Contains("ignored a message from user 777", log);
        Assert.Contains("ignored a message from user 888", log);
        Assert.Contains($"ignored a message from user {BridgeHarness.UserId}", log);
        Assert.DoesNotContain("secret", log);
        Assert.DoesNotContain("-100200", log);
    }

    [Theory]
    [InlineData("ready-not-running.json", BridgeTexts.QueuedNotRunning)]
    [InlineData("ready-not-running-away.json", BridgeTexts.QueuedNotRunning)]
    [InlineData("ready-not-picking-up.json", BridgeTexts.QueuedNotPickingUp)]
    public async Task While_FirstMate_is_down_the_request_is_queued_and_the_user_is_told_once(string readyFixture, string notice)
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady(readyFixture);
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "are you there?");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();
        await bridge.PollAsync();

        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        Assert.Equal(BotReactions.Received, Assert.Single(Assert.Single(harness.Telegram.Reactions()).Emoji));
        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal((notice, messageId), (sent.Text, sent.ReplyToMessageId));

        harness.FirstMate.SetReady("ready-running-listening.json");
        harness.FirstMate.Reply(noteId, "Back now. Yes.");
        await bridge.ForwardAsync();
        Assert.Equal("Back now. Yes.", harness.Telegram.SentMessages()[^1].Text);
    }

    [Theory]
    [InlineData("ready-running-listening.json")]
    [InlineData("ready-listening-unconfirmed.json")]
    public async Task No_queued_notice_when_FirstMate_is_running_or_cannot_be_confirmed_down(string readyFixture)
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady(readyFixture);
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "hello");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        Assert.Single(harness.FirstMate.NoteIds());
        Assert.Empty(harness.Telegram.SentMessages());
    }

    [Fact]
    public async Task When_readiness_cannot_be_read_the_request_is_still_saved_without_a_notice()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.Script("ready", new { exit = 1, stderr = "ready broke" });
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "hello");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        Assert.Single(harness.FirstMate.NoteIds());
        Assert.Single(harness.Telegram.Reactions());
        Assert.Empty(harness.Telegram.SentMessages());
    }

    [Fact]
    public async Task A_non_text_message_gets_the_text_only_answer_and_nothing_goes_to_FirstMate()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var messageId = harness.Telegram.EnqueuePhoto(BridgeHarness.UserId);
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        Assert.Empty(harness.FirstMate.Calls("note"));
        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal((BridgeTexts.OnlyText, messageId), (sent.Text, sent.ReplyToMessageId));
        await bridge.PollAsync();
        Assert.Equal(0, harness.Telegram.PendingUpdateCount());
        Assert.Single(harness.Telegram.SentMessages());
    }

    [Theory]
    [InlineData("/mute 2h", "mute")]
    [InlineData("/unmute", "unmute")]
    [InlineData("/back", "back")]
    [InlineData("/stop", "stop")]
    [InlineData("/Help@FirstMate_Test_Bot", "help")]
    public async Task The_commands_a_later_version_adds_answer_not_available_yet_and_reach_nobody(string text, string command)
    {
        await using var harness = await BridgeHarness.StartAsync();
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, text);
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        Assert.Empty(harness.FirstMate.Calls("note"));
        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal((BridgeTexts.NotAvailableYet(command), messageId), (sent.Text, sent.ReplyToMessageId));
        Assert.False(bridge.Store.State.Stopped);
    }

    [Theory]
    [InlineData("/deploy the webapp")]
    [InlineData("/start")]
    [InlineData("/status@some_other_bot")]
    [InlineData("I'm back")]
    public async Task Anything_else_including_an_unknown_slash_word_is_a_request(string text)
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, text);
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        Assert.StartsWith(text + "\n\n", Assert.Single(harness.FirstMate.Calls("note")).StandardInput);
        Assert.Empty(harness.Telegram.SentMessages());
    }

    [Fact]
    public async Task A_note_saved_without_a_wake_is_repaired_with_announce_and_never_saved_twice()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.Script("note", new { no_wake = true });
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "wake up");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        Assert.False(harness.FirstMate.IsAnnounced(noteId));
        Assert.False(bridge.Store.Requests[noteId].Announced);
        Assert.Single(harness.Telegram.Reactions());

        await bridge.PollAsync();

        Assert.True(harness.FirstMate.IsAnnounced(noteId));
        Assert.True(bridge.Store.Requests[noteId].Announced);
        Assert.Equal(["announce", "--json", noteId], Assert.Single(harness.FirstMate.Calls("announce")).Arguments);
        Assert.Single(harness.FirstMate.Calls("note"));
    }

    [Fact]
    public async Task A_request_that_cannot_be_saved_holds_the_position_while_commands_are_answered_once_and_later_requests_wait_in_order()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        harness.FirstMate.Script(
            "note",
            new { exit = 1, stderr = "request id is reserved but unreadable" },
            new { exit = 1, stderr = "request id is reserved but unreadable" },
            new { exit = 1, stderr = "request id is reserved but unreadable" });
        var first = harness.Telegram.EnqueueText(BridgeHarness.UserId, "first request");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();
        var startOffset = bridge.Store.State.UpdateOffset;

        await bridge.PollAsync();
        Assert.Empty(harness.FirstMate.NoteIds());
        Assert.Empty(harness.Telegram.Reactions());
        Assert.Equal(startOffset, bridge.Store.State.UpdateOffset);

        var command = harness.Telegram.EnqueueText(BridgeHarness.UserId, "/help");
        var second = harness.Telegram.EnqueueText(BridgeHarness.UserId, "second request");
        await bridge.PollAsync();
        Assert.Equal(BridgeTexts.NotAvailableYet("help"), Assert.Single(harness.Telegram.SentMessages()).Text);
        Assert.Single(harness.FirstMate.Calls("note"));

        time.Advance(TimeSpan.FromSeconds(1));
        await bridge.PollAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        await bridge.PollAsync();

        var told = harness.Telegram.SentMessages().Where(message => message.ReplyToMessageId == first).ToList();
        Assert.Equal(BridgeTexts.StillTrying("request id is reserved but unreadable"), Assert.Single(told).Text);
        Assert.Single(harness.Telegram.SentMessages(), message => message.ReplyToMessageId == command);
        Assert.Empty(harness.FirstMate.NoteIds());
        Assert.Equal(startOffset, bridge.Store.State.UpdateOffset);

        time.Advance(TimeSpan.FromSeconds(4));
        await bridge.PollAsync();

        var notes = harness.FirstMate.Calls("note").Select(call => call.Arguments[2]).Distinct().ToList();
        Assert.Equal([BridgeHarness.RequestIdFor(first), BridgeHarness.RequestIdFor(second)], notes);
        Assert.Equal(2, harness.FirstMate.NoteIds().Count);
        Assert.Single(harness.Telegram.SentMessages(), message => message.ReplyToMessageId == command);
        Assert.Empty(bridge.Store.State.HandledUpdateIds);

        await bridge.PollAsync();
        Assert.Equal(0, harness.Telegram.PendingUpdateCount());
        Assert.Single(harness.Telegram.SentMessages(), message => message.ReplyToMessageId == command);
    }

    [Fact]
    public async Task A_note_call_that_times_out_after_saving_is_retried_with_the_same_request_id_and_leaves_one_note()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        harness.Timeouts = harness.Timeouts with { Note = TimeSpan.FromSeconds(1) };
        harness.FirstMate.Script("note", new { sleep_after_save = 10 });
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "slow inbox");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();
        Assert.Empty(bridge.Store.Requests);
        Assert.Empty(harness.Telegram.Reactions());

        time.Advance(TimeSpan.FromSeconds(1));
        await bridge.PollAsync();

        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        var calls = harness.FirstMate.Calls("note");
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Equal(BridgeHarness.RequestIdFor(messageId), call.Arguments[2]));
        Assert.Equal(noteId, Assert.Single(bridge.Store.Requests.Values).NoteId);
    }

    [Fact]
    public async Task A_request_acknowledged_without_a_reply_stops_holding_the_fast_cadence_but_a_late_reply_still_arrives()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "just do it, no answer needed");
        var bridge = await harness.StartInstance().InitializedAsync();
        await bridge.PollAsync();
        var noteId = Assert.Single(harness.FirstMate.NoteIds());

        harness.FirstMate.Acknowledge(noteId);
        await bridge.ForwardAsync();
        Assert.False(bridge.Forwarder.HasWaitingRequests);
        Assert.NotNull(bridge.Store.Requests[noteId].AcknowledgedAt);

        harness.FirstMate.Reply(noteId, "Done after all.");
        await bridge.ForwardAsync();
        Assert.Equal("Done after all.", Assert.Single(harness.Telegram.SentMessages()).Text);
    }

    [Fact]
    public async Task The_log_keeps_ids_only_never_message_text_or_the_token()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "confidential request text");
        harness.Telegram.FailNext("sendMessage", 500, "Internal Server Error");
        var bridge = await harness.StartInstance().InitializedAsync();
        await bridge.PollAsync();
        harness.FirstMate.Reply(Assert.Single(harness.FirstMate.NoteIds()), "confidential reply text");

        await bridge.ForwardAsync();

        var log = harness.Logging.Text();
        Assert.Contains("saved message", log);
        Assert.Contains("sendMessage failed", log);
        Assert.DoesNotContain("confidential", log);
        Assert.DoesNotContain(FakeTelegramServer.Token, log);
        Assert.DoesNotContain(FakeTelegramServer.Token.Split(':')[1], log);
    }
}
