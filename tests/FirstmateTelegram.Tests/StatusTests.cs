using FirstmateTelegram.Bridge;
using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace FirstmateTelegram.Tests;

/// <summary>The /status command: an instant four-part answer from FirstMate's records, including the away-mode fallback.</summary>
public sealed class StatusTests
{
    static FakeTimeProvider UtcTime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 14, 5, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return time;
    }

    [Fact]
    public async Task Status_answers_from_the_bearings_projection_without_a_FirstMate_turn()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = UtcTime();
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "/status");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal(
            "FirstMate status, 14:05\n"
            + "Needs you (3)\n"
            + "• Decide: Billing API versioning: Options: A path prefix, B header\n"
            + "• Research: Research search providers\n"
            + "• Review: Fix the login redirect https://github.com/acme/webapp/pull/7\n"
            + "Recently landed (1)\n"
            + "• Docs refresh https://github.com/acme/webapp/pull/5\n"
            + "Under way (1)\n"
            + "• Ship the CSV export (harness busy (claude-hook))\n"
            + "Next (2)\n"
            + "• Release notes (waits on: ship-task)\n"
            + "• Rotate the API key",
            sent.Text);
        Assert.Equal((BridgeHarness.UserId, messageId), (sent.ChatId, sent.ReplyToMessageId));
        Assert.Empty(harness.FirstMate.Calls("note"));
        Assert.Equal(["--json"], Assert.Single(harness.FirstMate.SnapshotCalls("bearings")).Arguments);
        Assert.Empty(harness.FirstMate.SnapshotCalls("fleet"));
    }

    [Fact]
    public async Task Status_keeps_answering_from_records_while_FirstMate_is_stopped()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady("ready-not-running.json");
        var time = UtcTime();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/status");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.StartsWith("FirstMate status, 14:05\nNeeds you (3)", sent.Text);
        Assert.DoesNotContain("away mode", sent.Text);
        Assert.Empty(harness.FirstMate.Calls("note"));
        Assert.Single(harness.FirstMate.SnapshotCalls("bearings"));
    }

    [Fact]
    public async Task While_away_mode_refuses_the_bearings_the_answer_comes_from_the_fleet_snapshot()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady("ready-running-away.json");
        harness.FirstMate.Script("bearings", new { exit = 3, stderr = "away mode is on" });
        var time = UtcTime();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/status");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal(
            "FirstMate status, 14:05 (away mode on; away mode: from FirstMate's fleet records)\n"
            + "Needs you (1)\n"
            + "• Decide: Billing API versioning. Options: A path prefix, B header\n"
            + "Recently landed (1)\n"
            + "• Docs refresh\n"
            + "Under way (3)\n"
            + "• acme/webapp: Ship the CSV export (working)\n"
            + "• acme/webapp: Research search providers (done)\n"
            + "• acme/webapp: Fix the login redirect (done)\n"
            + "Next (2)\n"
            + "• Release notes (waits on: Fix the login redirect)\n"
            + "• Rotate the API key",
            sent.Text);
        Assert.Single(harness.FirstMate.SnapshotCalls("bearings"));
        Assert.Equal(["--json"], Assert.Single(harness.FirstMate.SnapshotCalls("fleet")).Arguments);
        Assert.Empty(harness.FirstMate.Calls("note"));
    }

    [Fact]
    public async Task Each_part_shows_at_most_eight_items_and_an_empty_part_says_nothing()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetBearings("bearings-many.json");
        var time = UtcTime();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/status");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages()).Text;
        var parts = sent.Split('\n');
        Assert.Equal("Needs you (1)", parts[1]);
        Assert.Equal("• Research: Research search provider options", parts[2]);
        Assert.Equal("Recently landed (10)", parts[3]);
        Assert.Equal("+2 more", parts[3 + 9]);
        Assert.Equal("Under way (1)", parts[13]);
        Assert.Equal("• Ship the CSV export (harness busy (claude-hook))", parts[14]);
        Assert.Equal("Next (10)", parts[15]);
        Assert.Equal("• Rotate the signing key (until 2026-10-01: decide the rotation wi…)", parts[16]);
        Assert.Equal("• Queued work 1 (waits on: csv-export)", parts[17]);
        Assert.Equal("+2 more", parts[^1]);
    }

    [Fact]
    public async Task A_snapshot_that_times_out_answers_with_the_reason_instead_of_waiting()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Timeouts = harness.Timeouts with { Snapshot = TimeSpan.FromSeconds(1) };
        harness.FirstMate.Script("bearings", new { sleep = 30 });
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/status");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        Assert.Equal(
            "Could not read FirstMate's records: the snapshot timed out",
            Assert.Single(harness.Telegram.SentMessages()).Text);
        Assert.Empty(harness.FirstMate.Calls("note"));
    }

    [Theory]
    [InlineData("jq is missing", "Could not read FirstMate's records: jq is missing")]
    [InlineData("fm-bearings: jq is missing", "Could not read FirstMate's records: fm-bearings: jq is missing")]
    public async Task A_failing_snapshot_says_why_in_one_line(string stderr, string expected)
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.Script("bearings", new { exit = 1, stderr });
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/status");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        Assert.Equal(expected, Assert.Single(harness.Telegram.SentMessages()).Text);
    }

    [Fact]
    public async Task A_newer_snapshot_format_says_to_update_firstmate_telegram()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.Script("bearings", new { stdout = "{\"schema\":\"fm-bearings.v2\"}\n", exit = 0 });
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/status");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        Assert.Equal(
            "Could not read FirstMate's records: FirstMate's records use a newer format; update firstmate-telegram",
            Assert.Single(harness.Telegram.SentMessages()).Text);
    }

    [Fact]
    public async Task Rows_the_bearings_projection_cannot_read_make_the_answer_say_so_instead_of_nothing()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.Script("bearings", new
        {
            stdout = "{\"schema\":\"fm-bearings.v1\",\"decisions_open\":[{}],\"in_flight\":[],\"recorded_prs\":[],\"landed\":[],\"gates\":[]}\n",
            exit = 0,
        });
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/status");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        var text = Assert.Single(harness.Telegram.SentMessages()).Text;
        Assert.StartsWith("Could not read FirstMate's records:", text);
        Assert.Contains("unexpected output from fm-bearings-snapshot.sh", text);
        Assert.DoesNotContain("Needs you", text);
    }

    [Fact]
    public async Task Rows_the_fleet_snapshot_cannot_read_make_the_answer_say_so_instead_of_nothing()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady("ready-running-away.json");
        harness.FirstMate.Script("bearings", new { exit = 3, stderr = "away mode is on" });
        harness.FirstMate.Script("fleet", new
        {
            stdout = "{\"schema\":\"fm-fleet-snapshot.v1\",\"tasks\":[{}],\"backlog\":{\"records\":[]}}\n",
            exit = 0,
        });
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/status");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        var text = Assert.Single(harness.Telegram.SentMessages()).Text;
        Assert.StartsWith("Could not read FirstMate's records:", text);
        Assert.Contains("unexpected output from fm-fleet-snapshot.sh", text);
        Assert.DoesNotContain("Needs you", text);
    }

    [Fact]
    public async Task An_away_fallback_whose_fleet_snapshot_fails_says_why_in_one_line()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady("ready-running-away.json");
        harness.FirstMate.Script("bearings", new { exit = 3, stderr = "away mode is on" });
        harness.FirstMate.Script("fleet", new { exit = 1, stderr = "the fleet snapshot broke" });
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/status");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        Assert.Equal(
            "Could not read FirstMate's records: the fleet snapshot broke",
            Assert.Single(harness.Telegram.SentMessages()).Text);
    }
}
