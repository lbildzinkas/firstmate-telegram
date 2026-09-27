using System.Text.Json.Nodes;
using FirstmateTelegram.Bridge;
using FirstmateTelegram.State;
using FirstmateTelegram.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace FirstmateTelegram.Tests;

/// <summary>
/// The alerts FirstMate's own records produce (spec 4.4, 7.2.6): exactly one alert per event, the settle window
/// for worker statuses, decisions from the fleet snapshot, and mute making alerts silent without dropping any.
/// </summary>
public sealed class AlertTests
{
    static FakeTimeProvider AwayTime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 14, 5, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return time;
    }

    static JsonObject HoldRecord(string id, string title, string? holdReason = null, string repo = "acme/webapp") => new()
    {
        ["structured"] = true,
        ["id"] = id,
        ["title"] = title,
        ["state"] = "queued",
        ["captain_actionable"] = true,
        ["hold_reason"] = holdReason,
        ["repo"] = repo,
        ["unresolved_blocker_ids"] = new JsonArray(),
    };

    static JsonObject TaskRow(string id, string title, string state, string repo = "acme/webapp", string kind = "ship", string? openDecisionKey = null) => new()
    {
        ["id"] = id,
        ["kind"] = kind,
        ["project"] = repo,
        ["backlog"] = new JsonObject { ["title"] = title, ["repo"] = repo },
        ["current_state"] = new JsonObject { ["state"] = state },
        ["hints"] = new JsonObject
        {
            ["open_decisions"] = openDecisionKey is null ? new JsonArray() : new JsonArray(new JsonObject { ["key"] = openDecisionKey }),
        },
    };

    static string HoldsOnly(params (string Id, string Title, string? HoldReason)[] holds) => new JsonObject
    {
        ["schema"] = "fm-fleet-snapshot.v1",
        ["tasks"] = new JsonArray(),
        ["backlog"] = new JsonObject { ["records"] = new JsonArray(holds.Select(hold => HoldRecord(hold.Id, hold.Title, hold.HoldReason)).ToArray()) },
    }.ToJsonString();

    static string WithTasksAndRecords(JsonObject[] tasks, JsonObject[] records) => new JsonObject
    {
        ["schema"] = "fm-fleet-snapshot.v1",
        ["tasks"] = new JsonArray(tasks),
        ["backlog"] = new JsonObject { ["records"] = new JsonArray(records) },
    }.ToJsonString();

    static string TaskSnapshot(string id, string title, string state, string kind = "ship", string? openDecisionKey = null) =>
        WithTasksAndRecords([TaskRow(id, title, state, kind: kind, openDecisionKey: openDecisionKey)], []);

    [Fact]
    public async Task A_pr_ready_record_alerts_once_with_the_task_title_and_url()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayTime();
        harness.FirstMate.SetFleetSnapshotText(TaskSnapshot("ship-task", "Fix the login redirect", "done"));
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.WatchAsync(); // the first read takes the current end as its starting point
        harness.FirstMate.AppendLedger(
            """{"v":1,"ts":"2026-09-26T14:10:00Z","event":"task.dispatched","task":"ship-task","kind":"ship","project":"acme/webapp"}""",
            """{"v":1,"ts":"2026-09-26T14:11:00Z","event":"task.pr_ready","task":"ship-task","pr":"https://github.com/acme/webapp/pull/7"}""");
        await bridge.WatchAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal("Ready for your review: Fix the login redirect https://github.com/acme/webapp/pull/7", sent.Text);
        Assert.Null(sent.ReplyToMessageId);
        Assert.NotEqual(true, sent.DisableNotification);

        // The ledger may repeat the record; the same dedupe key never alerts twice.
        harness.FirstMate.AppendLedger("""{"v":1,"ts":"2026-09-26T14:12:00Z","event":"task.pr_ready","task":"ship-task","pr":"https://github.com/acme/webapp/pull/7"}""");
        await bridge.WatchAsync();
        await bridge.WatchAsync();
        Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal("pr:ship-task:https://github.com/acme/webapp/pull/7", Assert.Single(bridge.Store.Alerts.Values).Key);
    }

    [Fact]
    public async Task A_finished_scout_alerts_as_research_and_a_finished_ship_task_alerts_nothing()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetFleetSnapshotText(TaskSnapshot("scout-x", "Research search providers", "done", kind: "scout"));
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.WatchAsync();
        harness.FirstMate.AppendLedger(
            """{"v":1,"ts":"2026-09-26T14:10:00Z","event":"task.status","task":"scout-x","state":"done","text":"research complete"}""",
            """{"v":1,"ts":"2026-09-26T14:10:30Z","event":"task.status","task":"ship-task","state":"done","text":"shipped"}""");
        await bridge.WatchAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal("Research finished: Research search providers. Reply to this message to get the findings.", sent.Text);
        Assert.Equal("research:scout-x", Assert.Single(bridge.Store.Alerts.Values).Key);
    }

    [Fact]
    public async Task A_new_captain_hold_alerts_as_a_decision_and_never_twice_for_the_same_hold()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayTime();
        var bridge = await harness.StartInstance(time: time).InitializedAsync();
        harness.FirstMate.SetFleetSnapshotText(HoldsOnly(("billing-choice", "Billing API versioning", "Options: A path prefix, B header")));

        await bridge.WatchAsync(); // the first decisions read seeds the holds it finds without alerting
        Assert.Empty(harness.Telegram.SentMessages());

        harness.FirstMate.SetFleetSnapshotText(HoldsOnly(
            ("billing-choice", "Billing API versioning", "Options: A path prefix, B header"),
            ("cache-choice", "Cache eviction policy", "Options: A lru, B ttl")));
        time.Advance(TimeSpan.FromMinutes(2));
        await bridge.WatchAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal("Decision needed: Cache eviction policy. Options: A lru, B ttl. Reply to this message with your answer.", sent.Text);
        Assert.StartsWith("decision:cache-choice:", Assert.Single(bridge.Store.Alerts.Values).Key);

        time.Advance(TimeSpan.FromMinutes(2));
        await bridge.WatchAsync();
        Assert.Single(harness.Telegram.SentMessages());
    }

    [Fact]
    public async Task Two_new_holds_in_one_snapshot_are_both_on_record_across_a_restart()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayTime();
        var bridge = await harness.StartInstance(time: time).InitializedAsync();
        harness.FirstMate.SetFleetSnapshotText(HoldsOnly(("billing-choice", "Billing API versioning", "Options: A path prefix, B header")));
        await bridge.WatchAsync(); // the first decisions read seeds the holds it finds without alerting

        time.Advance(TimeSpan.FromMinutes(2));
        harness.FirstMate.SetFleetSnapshotText(HoldsOnly(
            ("billing-choice", "Billing API versioning", "Options: A path prefix, B header"),
            ("cache-choice", "Cache eviction policy", "Options: A lru, B ttl"),
            ("dns-choice", "DNS provider", "Options: A keep, B switch")));
        await bridge.WatchAsync();
        Assert.Equal(2, harness.Telegram.SentMessages().Count);

        var restarted = await harness.StartInstance(time: time).InitializedAsync();
        time.Advance(TimeSpan.FromMinutes(2));
        await restarted.WatchAsync();

        Assert.Equal(2, harness.Telegram.SentMessages().Count); // both holds were on record before their alerts went out
    }

    [Fact]
    public async Task A_hold_closed_for_two_snapshots_in_a_row_is_forgotten_so_the_same_task_can_alert_again()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayTime();
        var bridge = await harness.StartInstance(time: time).InitializedAsync();
        const string held = "Billing API versioning";
        var snapshot = HoldsOnly(("billing-choice", held, "Options: A path prefix, B header"));
        var closed = HoldsOnly();

        harness.FirstMate.SetFleetSnapshotText(snapshot);
        await bridge.WatchAsync(); // seeds

        time.Advance(TimeSpan.FromMinutes(2));
        harness.FirstMate.SetFleetSnapshotText(closed);
        await bridge.WatchAsync(); // absent once: still remembered
        Assert.Single(bridge.Store.State.Decisions!);

        time.Advance(TimeSpan.FromMinutes(2));
        harness.FirstMate.SetFleetSnapshotText(closed);
        await bridge.WatchAsync(); // absent twice: closed
        Assert.Empty(bridge.Store.State.Decisions!);

        time.Advance(TimeSpan.FromMinutes(2));
        harness.FirstMate.SetFleetSnapshotText(snapshot);
        await bridge.WatchAsync(); // held again: a new event with a new key

        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.StartsWith("Decision needed: Billing API versioning", sent.Text);
        Assert.StartsWith("decision:billing-choice:", Assert.Single(bridge.Store.Alerts.Values).Key);
    }

    [Theory]
    [InlineData("needs-decision", "Waiting on a decision: Ship the CSV export. it waits on your decision")]
    [InlineData("blocked", "Blocked: Ship the CSV export. the build is red")]
    [InlineData("failed", "Failed: Ship the CSV export. tests failed")]
    public async Task A_worker_status_alerts_only_if_still_open_after_the_settle_window(string state, string expected)
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayTime();
        harness.Config = harness.Config with { AlertSettleMinutes = 15 };
        var statusText = state switch
        {
            "needs-decision" => "it waits on your decision",
            "blocked" => "the build is red",
            _ => "tests failed",
        };
        harness.FirstMate.SetFleetSnapshotText(TaskSnapshot("csv-export", "Ship the CSV export", state));
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.WatchAsync();
        harness.FirstMate.AppendLedger($$"""{"v":1,"ts":"2026-09-26T14:10:00Z","event":"task.status","task":"csv-export","state":"{{state}}","key":"k1","text":"{{statusText}}"}""");
        await bridge.WatchAsync();
        Assert.Empty(harness.Telegram.SentMessages()); // the settle window is still running
        Assert.Single(bridge.Store.State.Settles);

        time.Advance(TimeSpan.FromMinutes(15));
        await bridge.WatchAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal(expected, sent.Text);
        Assert.Equal($"status:csv-export:{state}:k1", Assert.Single(bridge.Store.Alerts.Values).Key);
        Assert.Empty(bridge.Store.State.Settles);
    }

    [Fact]
    public async Task A_worker_status_that_closes_within_the_settle_window_never_alerts()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayTime();
        var bridge = await harness.StartInstance(time: time).InitializedAsync();
        await bridge.WatchAsync();

        harness.FirstMate.AppendLedger("""{"v":1,"ts":"2026-09-26T14:10:00Z","event":"task.status","task":"csv-export","state":"blocked","key":"k1","text":"the build is red"}""");
        await bridge.WatchAsync();

        time.Advance(TimeSpan.FromMinutes(15));
        harness.FirstMate.SetFleetSnapshotText(TaskSnapshot("csv-export", "Ship the CSV export", "working"));
        await bridge.WatchAsync();
        await bridge.WatchAsync();

        Assert.Empty(harness.Telegram.SentMessages());
        Assert.Empty(bridge.Store.Alerts);
        Assert.Empty(bridge.Store.State.Settles);
    }

    [Fact]
    public async Task A_worker_status_never_alerts_when_a_decision_alert_covers_the_task()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayTime();
        var bridge = await harness.StartInstance(time: time).InitializedAsync();
        await bridge.WatchAsync(); // seeds the initial holds (none)

        harness.FirstMate.AppendLedger("""{"v":1,"ts":"2026-09-26T14:10:00Z","event":"task.status","task":"csv-export","state":"needs-decision","key":"k1","text":"it waits on your decision"}""");
        await bridge.WatchAsync();

        time.Advance(TimeSpan.FromMinutes(15));
        harness.FirstMate.SetFleetSnapshotText(WithTasksAndRecords(
            [TaskRow("csv-export", "Ship the CSV export", "needs-decision", openDecisionKey: "k1")],
            [HoldRecord("csv-export", "Ship the CSV export", "Options: A or B")]));
        await bridge.WatchAsync(); // the needs-decision record reads the decisions list: the hold alerts first

        Assert.Equal("Decision needed: Ship the CSV export. Options: A or B. Reply to this message with your answer.", Assert.Single(harness.Telegram.SentMessages()).Text);

        await bridge.WatchAsync(); // the settle window passes with the task still held: no second alert
        Assert.Single(harness.Telegram.SentMessages());
        Assert.Contains(bridge.Store.Alerts.Keys, key => key.StartsWith("decision:csv-export:", StringComparison.Ordinal));
        Assert.DoesNotContain(bridge.Store.Alerts.Keys, key => key.StartsWith("status:csv-export:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_needs_decision_status_still_open_only_in_hints_alerts_after_the_settle_window()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayTime();
        var bridge = await harness.StartInstance(time: time).InitializedAsync();
        await bridge.WatchAsync();

        harness.FirstMate.AppendLedger("""{"v":1,"ts":"2026-09-26T14:10:00Z","event":"task.status","task":"csv-export","state":"needs-decision","key":"k9","text":"it waits on your decision"}""");
        await bridge.WatchAsync();

        time.Advance(TimeSpan.FromMinutes(15));
        harness.FirstMate.SetFleetSnapshotText(TaskSnapshot("csv-export", "Ship the CSV export", "working", openDecisionKey: "k9"));
        await bridge.WatchAsync();

        Assert.Equal("Waiting on a decision: Ship the CSV export. it waits on your decision", Assert.Single(harness.Telegram.SentMessages()).Text);
    }

    [Fact]
    public async Task Nothing_before_the_bridges_first_start_alerts()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.AppendLedger("""{"v":1,"ts":"2026-09-26T13:00:00Z","event":"task.pr_ready","task":"old-task","pr":"https://github.com/acme/webapp/pull/1"}""");
        harness.FirstMate.SetFleetSnapshotText(HoldsOnly(("billing-choice", "Billing API versioning", "Options: A path prefix, B header")));
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.WatchAsync();
        await bridge.WatchAsync();

        Assert.Empty(harness.Telegram.SentMessages());
        Assert.Empty(bridge.Store.Alerts);
    }

    [Fact]
    public async Task Muted_alerts_go_out_silently_and_are_never_dropped()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayTime();
        harness.FirstMate.SetFleetSnapshotText(TaskSnapshot("ship-task", "Fix the login redirect", "done"));
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/mute 1h");
        await bridge.PollAsync();
        Assert.Equal("Alerts are silent until 15:05.", Assert.Single(harness.Telegram.SentMessages()).Text);

        await bridge.WatchAsync();
        harness.FirstMate.AppendLedger("""{"v":1,"ts":"2026-09-26T14:10:00Z","event":"task.pr_ready","task":"ship-task","pr":"https://github.com/acme/webapp/pull/7"}""");
        await bridge.WatchAsync();

        var alert = harness.Telegram.SentMessages().Single(message => message.Text.StartsWith("Ready for your review"));
        Assert.True(alert.DisableNotification);
        Assert.Single(bridge.Store.Alerts); // sent, not dropped

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/unmute");
        await bridge.PollAsync();
        harness.FirstMate.AppendLedger("""{"v":1,"ts":"2026-09-26T14:20:00Z","event":"task.pr_ready","task":"ship-task","pr":"https://github.com/acme/webapp/pull/8"}""");
        await bridge.WatchAsync();

        var second = harness.Telegram.SentMessages().Single(message => message.Text.Contains("pull/8"));
        Assert.NotEqual(true, second.DisableNotification);
    }

    [Fact]
    public async Task A_mute_survives_a_restart_and_ends_on_its_own()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayTime();
        var bridge = await harness.StartInstance(time: time).InitializedAsync();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/mute 30m");
        await bridge.PollAsync();

        var restarted = await harness.StartInstance(time: time).InitializedAsync();
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(30), restarted.Store.State.MuteUntil);

        time.Advance(TimeSpan.FromMinutes(31));
        harness.FirstMate.SetFleetSnapshotText(TaskSnapshot("ship-task", "Fix the login redirect", "done"));
        await restarted.WatchAsync();
        harness.FirstMate.AppendLedger("""{"v":1,"ts":"2026-09-26T14:40:00Z","event":"task.pr_ready","task":"ship-task","pr":"https://github.com/acme/webapp/pull/9"}""");
        await restarted.WatchAsync();

        var alert = Assert.Single(harness.Telegram.SentMessages(), message => message.Text.StartsWith("Ready for your review"));
        Assert.NotEqual(true, alert.DisableNotification); // the mute ended on its own
    }

    [Fact]
    public async Task A_reply_to_an_alert_is_a_request_that_carries_the_alerts_text()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayTime();
        harness.FirstMate.SetFleetSnapshotText(HoldsOnly(("billing-choice", "Billing API versioning", "Options: A path prefix, B header")));
        var bridge = await harness.StartInstance(time: time).InitializedAsync();
        await bridge.WatchAsync(); // seeds the hold
        Assert.Empty(harness.Telegram.SentMessages());

        harness.FirstMate.SetFleetSnapshotText(HoldsOnly(("cache-choice", "Cache eviction policy", "Options: A lru, B ttl")));
        time.Advance(TimeSpan.FromMinutes(2));
        await bridge.WatchAsync(); // a new hold alerts
        var entry = Assert.Single(bridge.Store.Alerts.Values);
        var alertMessageId = entry.TelegramMessageId!.Value;
        Assert.Equal("Decision needed: Cache eviction policy. Options: A lru, B ttl. Reply to this message with your answer.", entry.Text);

        var replyId = harness.Telegram.EnqueueReply(BridgeHarness.UserId, alertMessageId, "go with option B");
        await bridge.PollAsync();

        var body = Assert.Single(harness.FirstMate.Calls("note")).StandardInput!;
        Assert.StartsWith("go with option B\n\n", body);
        var sentAt = TimeZoneInfo.ConvertTime(entry.At, time.LocalTimeZone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains($"In reply to the alert sent at {sentAt}: \"{entry.Text}\"", body);
        var request = Assert.Single(bridge.Store.Requests.Values);
        Assert.Equal(RequestKinds.AlertReply, request.Kind);
        Assert.Equal(replyId, request.MessageId);
    }

    [Fact]
    public async Task A_replaced_ledger_rereads_from_the_beginning_but_the_history_stops_repeats()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var time = AwayTime();
        harness.FirstMate.SetFleetSnapshotText(TaskSnapshot("ship-task", "Fix the login redirect", "done"));
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.WatchAsync();
        const string record = """{"v":1,"ts":"2026-09-26T14:10:00Z","event":"task.pr_ready","task":"ship-task","pr":"https://github.com/acme/webapp/pull/7"}""";
        harness.FirstMate.AppendLedger(record);
        await bridge.WatchAsync();
        Assert.Single(harness.Telegram.SentMessages());

        harness.FirstMate.ReplaceLedger(record, """{"v":1,"ts":"2026-09-26T14:11:00Z","event":"task.pr_ready","task":"ship-task","pr":"https://github.com/acme/webapp/pull/7"}""");
        await bridge.WatchAsync();
        await bridge.WatchAsync();

        Assert.Single(harness.Telegram.SentMessages()); // the same key never alerts twice
    }
}
