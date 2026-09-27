using FirstmateTelegram.Bridge;
using FirstmateTelegram.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace FirstmateTelegram.Tests;

/// <summary>
/// The availability alerts (spec 4.4.1, 7.5.4): only in true away mode, debounced over two polls and two
/// minutes, silent while muted like every alert, with an "available again" only for an outage the bridge
/// alerted and a hold after a wake.
/// </summary>
public sealed class AvailabilityAlertTests
{
    static FakeTimeProvider AwayClock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 14, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return time;
    }

    [Fact]
    public async Task Availability_alerts_fire_only_while_truly_in_away_mode()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayClock();
        harness.FirstMate.SetReady("ready-not-running.json"); // not running, posture present
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        for (var poll = 0; poll < 4; poll++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await bridge.CheckAvailabilityAsync();
        }

        Assert.Empty(harness.Telegram.SentMessages()); // the user is at the terminal: availability is /ping's job

        harness.FirstMate.SetReady("ready-not-running-away.json");
        time.Advance(TimeSpan.FromMinutes(1));
        await bridge.CheckAvailabilityAsync(); // away mode began with the outage already present: alerts at once

        var alert = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal("FirstMate stopped while you are away. Requests will queue until it starts.", alert.Text);
    }

    [Fact]
    public async Task Quiet_mode_does_not_count_as_away_mode()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayClock();
        harness.FirstMate.SetReady("ready-running-quiet.json");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        for (var poll = 0; poll < 4; poll++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await bridge.CheckAvailabilityAsync();
        }

        Assert.Empty(harness.Telegram.SentMessages());
    }

    [Fact]
    public async Task A_bad_verdict_needs_two_polls_spanning_two_minutes_before_it_alerts()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayClock();
        harness.FirstMate.SetReady("ready-not-running-away.json");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        time.Advance(TimeSpan.FromMinutes(1));
        await bridge.CheckAvailabilityAsync();
        Assert.Empty(harness.Telegram.SentMessages()); // first bad poll

        time.Advance(TimeSpan.FromSeconds(30));
        await bridge.CheckAvailabilityAsync();
        Assert.Empty(harness.Telegram.SentMessages()); // two polls in a row, but not two minutes yet

        time.Advance(TimeSpan.FromSeconds(90));
        await bridge.CheckAvailabilityAsync(); // the bad verdict has now held for two minutes: alert
        Assert.Single(harness.Telegram.SentMessages());

        for (var poll = 0; poll < 5; poll++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await bridge.CheckAvailabilityAsync();
        }

        Assert.Single(harness.Telegram.SentMessages()); // the same outage never alerts twice
    }

    [Fact]
    public async Task Available_again_after_an_alerted_outage_and_only_after_two_ready_polls()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayClock();
        harness.FirstMate.SetReady("ready-not-running-away.json");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await PollAsync(bridge, time, minutes: 5);
        Assert.Single(harness.Telegram.SentMessages());

        harness.FirstMate.SetReady("ready-running-away.json");
        time.Advance(TimeSpan.FromMinutes(1));
        await bridge.CheckAvailabilityAsync(); // first Ready poll: not confirmed yet
        Assert.Single(harness.Telegram.SentMessages());

        time.Advance(TimeSpan.FromMinutes(1));
        await bridge.CheckAvailabilityAsync(); // two Ready polls, but not two minutes of them yet
        Assert.Single(harness.Telegram.SentMessages());

        time.Advance(TimeSpan.FromMinutes(1));
        await bridge.CheckAvailabilityAsync(); // Ready held for two minutes: available again

        var back = harness.Telegram.SentMessages()[^1];
        Assert.StartsWith("FirstMate is available again (was stopped for ", back.Text);
        Assert.EndsWith(").", back.Text);
        Assert.Equal(2, harness.Telegram.SentMessages().Count);
    }

    [Fact]
    public async Task Nothing_alerts_for_five_minutes_after_a_wake()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayClock();
        harness.FirstMate.SetReady("ready-not-running-away.json");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        time.Advance(TimeSpan.FromMinutes(1));
        await bridge.CheckAvailabilityAsync();

        time.Advance(TimeSpan.FromMinutes(30)); // the wall clock jumps: the Mac slept and woke
        await bridge.CheckAvailabilityAsync();
        Assert.Empty(harness.Telegram.SentMessages()); // the debounce also starts over

        for (var poll = 0; poll < 4; poll++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await bridge.CheckAvailabilityAsync();
        }

        Assert.Empty(harness.Telegram.SentMessages()); // still inside the wake grace

        time.Advance(TimeSpan.FromMinutes(2));
        await bridge.CheckAvailabilityAsync();
        Assert.Single(harness.Telegram.SentMessages()); // the grace passed and the outage held
    }

    [Fact]
    public async Task When_away_mode_ends_alerts_stop_and_the_recovery_never_announces_itself()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayClock();
        harness.FirstMate.SetReady("ready-not-running-away.json");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await PollAsync(bridge, time, minutes: 5);
        Assert.Single(harness.Telegram.SentMessages());

        harness.FirstMate.SetReady("ready-not-running.json"); // the user came back, FirstMate still down
        for (var poll = 0; poll < 4; poll++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await bridge.CheckAvailabilityAsync();
        }

        Assert.Single(harness.Telegram.SentMessages()); // no "available again" while the user is present
    }

    [Fact]
    public async Task An_out_of_quota_FirstMate_alerts_with_the_reset_time()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayClock();
        harness.FirstMate.SetReady("ready-running-away.json");
        harness.FirstMate.SetQuota("quota-exhausted.json");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await PollAsync(bridge, time, minutes: 5);

        var alert = Assert.Single(harness.Telegram.SentMessages());
        Assert.StartsWith("Claude quota is out until ", alert.Text);
        Assert.EndsWith(". FirstMate can't work until then.", alert.Text);
    }

    [Fact]
    public async Task Availability_alerts_are_silent_while_muted_but_never_dropped()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayClock();
        harness.FirstMate.SetReady("ready-not-running-away.json");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/mute 1h");
        await bridge.PollAsync();

        await PollAsync(bridge, time, minutes: 5);

        var alert = Assert.Single(harness.Telegram.SentMessages(), message => message.Text.StartsWith("FirstMate stopped"));
        Assert.True(alert.DisableNotification);
    }

    static async Task PollAsync(BridgeInstance bridge, FakeTimeProvider time, int minutes)
    {
        for (var poll = 0; poll < minutes; poll++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await bridge.CheckAvailabilityAsync();
        }
    }
}
