using FirstmateTelegram.Bridge;
using FirstmateTelegram.State;
using FirstmateTelegram.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace FirstmateTelegram.Tests;

/// <summary>The commands this part of v1 adds (spec 4.3): /mute, /unmute, /back, /stop and /help.</summary>
public sealed class BridgeCommandsTests
{
    static FakeTimeProvider Clock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 14, 5, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return time;
    }

    [Theory]
    [InlineData("30m", 30)]
    [InlineData("2h", 120)]
    [InlineData("1d", 1440)]
    [InlineData("1h30m", 90)]
    [InlineData("7d", 10080)]
    [InlineData("", 60)]
    [InlineData("1H", 60)]
    public void Mute_durations_parse_into_minutes(string arguments, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), MuteDurations.Parse(arguments));
    }

    [Theory]
    [InlineData("8d")]
    [InlineData("0m")]
    [InlineData("m")]
    [InlineData("10x")]
    [InlineData("1h 30")]
    [InlineData("-1h")]
    [InlineData("1.5h")]
    [InlineData("10081m")]
    [InlineData("20000000d")]
    [InlineData("300000000h")]
    [InlineData("5000000d5000000d5000000d")]
    public void Anything_beyond_the_allowed_units_or_the_maximum_does_not_parse(string arguments)
    {
        Assert.Null(MuteDurations.Parse(arguments));
    }

    [Fact]
    public async Task Mute_answers_with_the_end_time_and_survives_a_restart()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = Clock();
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/mute 30m");
        await bridge.PollAsync();

        Assert.Equal("Alerts are silent until 14:35.", Assert.Single(harness.Telegram.SentMessages()).Text);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(30), bridge.Store.State.MuteUntil);
        Assert.Empty(harness.FirstMate.Calls("note"));

        var restarted = await harness.StartInstance(time: time).InitializedAsync();
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(30), restarted.Store.State.MuteUntil);
    }

    [Fact]
    public async Task A_long_mute_names_the_day_and_an_unknown_duration_explains_the_form()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = Clock();
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/mute 1d");
        await bridge.PollAsync();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/mute someday");
        await bridge.PollAsync();

        Assert.Equal("Alerts are silent until Sun 14:05.", harness.Telegram.SentMessages()[0].Text);
        Assert.Equal(BridgeTexts.MuteUsage, harness.Telegram.SentMessages()[1].Text);
    }

    [Fact]
    public async Task Unmute_answers_and_clears_the_mute()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = Clock();
        var bridge = await harness.StartInstance(time: time).InitializedAsync();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/mute 2h");
        await bridge.PollAsync();

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/unmute");
        await bridge.PollAsync();

        Assert.Equal("Alerts are back on.", harness.Telegram.SentMessages()[^1].Text);
        Assert.Null(bridge.Store.State.MuteUntil);
        Assert.Empty(harness.FirstMate.Calls("note"));
    }

    [Fact]
    public async Task Back_answers_away_mode_is_not_on_while_the_user_is_present()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var bridge = await harness.StartInstance().InitializedAsync();

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/back");
        await bridge.PollAsync();

        Assert.Equal("Away mode is not on.", Assert.Single(harness.Telegram.SentMessages()).Text);
        Assert.Empty(harness.FirstMate.Calls("note"));
        Assert.Empty(harness.FirstMate.Calls("return"));
    }

    [Fact]
    public async Task Back_in_quiet_mode_explains_it_is_not_away_mode()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady("ready-running-quiet.json");
        var bridge = await harness.StartInstance().InitializedAsync();

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/back");
        await bridge.PollAsync();

        Assert.Equal("FirstMate is in quiet mode, not away mode. Quiet mode ends only at the terminal.", Assert.Single(harness.Telegram.SentMessages()).Text);
        Assert.Empty(harness.FirstMate.Calls("return"));
    }

    [Fact]
    public async Task Back_in_away_mode_on_todays_FirstMate_says_it_needs_a_newer_FirstMate_and_saves_nothing()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady("ready-running-away.json");
        var bridge = await harness.StartInstance().InitializedAsync();

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/back");
        await bridge.PollAsync();

        Assert.Equal("Away mode is still on. Ending it from Telegram needs a newer FirstMate; end it at the terminal for now.", Assert.Single(harness.Telegram.SentMessages()).Text);
        Assert.Equal(["return", "--request-id", BridgeHarness.RequestIdFor(1), "--json"], Assert.Single(harness.FirstMate.Calls("return")).Arguments);
        Assert.Empty(harness.FirstMate.NoteIds());
    }

    [Fact]
    public async Task Back_in_away_mode_on_a_FirstMate_with_the_hook_saves_the_return_and_forwards_its_reply()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady("ready-running-away.json");
        harness.FirstMate.AddReturnHook();
        var bridge = await harness.StartInstance().InitializedAsync();
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "/back");
        await bridge.PollAsync();

        var noteId = Assert.Single(harness.FirstMate.NoteIds());
        Assert.Equal(RequestKinds.Return, Assert.Single(bridge.Store.Requests.Values).Kind);
        Assert.Equal("Return saved. FirstMate will run its return and answer here.", harness.Telegram.SentMessages().Single(message => message.ReplyToMessageId == messageId).Text);
        Assert.Equal(["👀"], harness.Telegram.Reactions().Single(reaction => reaction.MessageId == messageId).Emoji);

        harness.FirstMate.Reply(noteId, "Back. Two notes waited for you; both are handled.");
        await bridge.ForwardAsync();

        var reply = harness.Telegram.SentMessages().Single(message => message.Text.StartsWith("Back. Two notes waited"));
        Assert.Equal(messageId, reply.ReplyToMessageId);
    }

    [Fact]
    public async Task Back_while_FirstMate_is_still_down_says_the_return_waits_in_the_inbox()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady("ready-not-running-away.json");
        harness.FirstMate.AddReturnHook();
        var bridge = await harness.StartInstance().InitializedAsync();

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/back");
        await bridge.PollAsync();

        Assert.Equal(2, harness.Telegram.SentMessages().Count);
        Assert.Contains(harness.Telegram.SentMessages(), message => message.Text == BridgeTexts.BackSaved);
        Assert.Contains(harness.Telegram.SentMessages(), message => message.Text == BridgeTexts.QueuedNotRunning);
        Assert.Single(harness.FirstMate.NoteIds());
    }

    [Fact]
    public async Task Stop_answers_sets_the_flag_confirms_the_position_and_stops_the_bridge()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var bridge = await harness.StartInstance().InitializedAsync();
        var before = bridge.Store.State.UpdateOffset;
        var updateId = harness.Telegram.PendingUpdateCount();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/stop");
        await bridge.PollAsync();

        Assert.Equal("Bridge stopped. Start it again on the Mac with `firstmate-telegram start`.", Assert.Single(harness.Telegram.SentMessages()).Text);
        Assert.True(bridge.Store.State.Stopped);
        Assert.True(bridge.Stopper.WasAskedToStop);
        Assert.Equal(0, harness.Telegram.PendingUpdateCount()); // the /stop is confirmed, so Telegram will not deliver it again
        Assert.True(harness.Telegram.ConfirmedOffset > before);

        // While the flag is set, a restarted bridge stays down: it polls nothing and sends nothing.
        var restarted = await harness.StartInstance().InitializedAsync();
        Assert.True(restarted.Store.State.Stopped);
    }

    [Fact]
    public async Task Help_lists_the_commands_and_says_anything_else_is_a_request()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var bridge = await harness.StartInstance().InitializedAsync();

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/help");
        await bridge.PollAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages());
        foreach (var command in new[] { "/status", "/ping", "/mute", "/unmute", "/back", "/stop", "/help" })
            Assert.Contains(command + " ", sent.Text);
        Assert.EndsWith("Anything else is sent to FirstMate as a request.", sent.Text);
        Assert.Empty(harness.FirstMate.Calls("note"));
    }

    [Fact]
    public async Task The_command_list_is_registered_for_the_users_chat_only()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var bridge = await harness.StartInstance().InitializedAsync();

        var registered = Assert.Single(harness.Telegram.RegisteredCommandLists());
        Assert.Equal(BridgeHarness.UserId, registered.ChatId);
        Assert.Equal(["status", "ping", "mute", "unmute", "back", "stop", "help"], registered.Commands.Select(command => command.Command));
    }

    [Fact]
    public async Task Plain_words_never_end_away_mode_even_while_away()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady("ready-running-away.json");
        var bridge = await harness.StartInstance().InitializedAsync();

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "I'm back");
        await bridge.PollAsync();

        Assert.Empty(harness.Telegram.SentMessages());
        var body = Assert.Single(harness.FirstMate.Calls("note")).StandardInput!;
        Assert.EndsWith("The user is still away: this request does not end away mode.\n", body);
    }
}
