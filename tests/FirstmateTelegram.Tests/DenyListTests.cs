using System.Text.Json.Nodes;
using FirstmateTelegram.Bridge;
using FirstmateTelegram.Telegram;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

/// <summary>The deny list (spec 6): private projects are named only as "a private project", in structured rows and free text alike, and it ships empty.</summary>
public sealed class DenyListTests
{
    [Fact]
    public void The_deny_list_ships_empty_so_everything_passes_through()
    {
        var redactor = Redactor.None;

        Assert.False(redactor.IsActive);
        Assert.False(redactor.IsDenied("acme/webapp"));
        Assert.Equal(
            "Ready for your review: fix login redirect https://github.com/acme/webapp/pull/7",
            redactor.Apply("Ready for your review: fix login redirect https://github.com/acme/webapp/pull/7"));
    }

    [Fact]
    public void Structured_rows_match_a_project_directory_or_repository_case_insensitively()
    {
        var redactor = new Redactor(["acme/webapp", "Acme-Secret"]);

        Assert.True(redactor.IsDenied("acme/webapp"));
        Assert.True(redactor.IsDenied("ACME/WebApp"));
        Assert.True(redactor.IsDenied("acme-secret/"));
        Assert.False(redactor.IsDenied("acme/webapp-old"));
        Assert.False(redactor.IsDenied(null));
    }

    [Theory]
    [InlineData(
        "Review acme/webapp pull request",
        "Review a private project pull request")]
    [InlineData(
        "The change landed in ACME/WEBAPP today",
        "The change landed in a private project today")]
    [InlineData(
        "See https://github.com/acme/webapp/pull/7 for the fix",
        "See a private project for the fix")]
    [InlineData(
        "Repo webapp at https://github.com/acme/webapp and acme-secret too",
        "Repo a private project at a private project and a private project too")]
    [InlineData(
        "The acme/webapp-legacy repo is fine",
        "The acme/webapp-legacy repo is fine")]
    public void Free_text_names_are_replaced_on_whole_words_and_urls_take_their_entry_with_them(string text, string expected)
    {
        var redactor = new Redactor(["acme/webapp", "webapp", "acme-secret"]);

        Assert.Equal(expected, redactor.Apply(text));
    }

    [Fact]
    public void Two_denied_urls_in_one_message_are_both_masked()
    {
        var redactor = new Redactor(["acme/webapp"]);

        Assert.Equal(
            "See a private project and a private project",
            redactor.Apply("See https://github.com/acme/webapp/pull/7 and https://github.com/acme/webapp/pull/8"));
    }

    [Fact]
    public async Task A_status_answer_shows_a_private_project_row_without_title_state_or_link()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Config = harness.Config with { DenyList = ["acme/webapp"] };
        harness.FirstMate.SetBearings("bearings.json");
        var bridge = await harness.StartInstance().InitializedAsync();

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/status");
        await bridge.PollAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages()).Text;
        Assert.Contains("• a private project", sent);
        Assert.DoesNotContain("Fix the login redirect", sent);
        Assert.DoesNotContain("Ship the CSV export", sent);
        Assert.DoesNotContain("acme/webapp", sent);
    }

    [Fact]
    public async Task A_fleet_status_answer_masks_a_denied_repo_in_the_landed_rows_too()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Config = harness.Config with { DenyList = ["acme/webapp"] };
        harness.FirstMate.SetReady("ready-running-away.json");
        harness.FirstMate.Script("bearings", new { exit = 3, stderr = "away mode is on" });
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/status");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages()).Text;
        Assert.Contains("Recently landed (1)\n• a private project", sent);
        Assert.DoesNotContain("Docs refresh", sent);
    }

    [Fact]
    public async Task A_fleet_next_row_names_no_denied_title_it_waits_on()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Config = harness.Config with { DenyList = ["acme/webapp"] };
        harness.FirstMate.SetReady("ready-running-away.json");
        harness.FirstMate.Script("bearings", new { exit = 3, stderr = "away mode is on" });
        harness.FirstMate.SetFleetSnapshotText(FleetSnapshot(
            Record("publish-docs", "Publish the docs", "queued", repo: "public/site", blockers: ["payroll"]),
            Record("payroll", "Payroll migration", "blocked", repo: "acme/webapp")));
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/status");
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.PollAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages()).Text;
        Assert.EndsWith("Next (1)\n• Publish the docs", sent);
        Assert.DoesNotContain("Payroll migration", sent);
    }

    static string FleetSnapshot(params JsonObject[] records) => new JsonObject
    {
        ["schema"] = "fm-fleet-snapshot.v1",
        ["tasks"] = new JsonArray(),
        ["backlog"] = new JsonObject { ["records"] = new JsonArray(records) },
    }.ToJsonString();

    static JsonObject Record(string id, string title, string state, string repo, string[]? blockers = null) => new()
    {
        ["structured"] = true,
        ["id"] = id,
        ["title"] = title,
        ["state"] = state,
        ["captain_actionable"] = false,
        ["hold_reason"] = null,
        ["repo"] = repo,
        ["unresolved_blocker_ids"] = new JsonArray((blockers ?? []).Select(blocker => (JsonNode)blocker).ToArray()),
    };

    [Fact]
    public async Task A_pr_alert_for_a_private_project_names_no_title_repo_or_link()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Config = harness.Config with { DenyList = ["acme/webapp"] };
        harness.FirstMate.SetFleetSnapshotText(TaskSnapshotJson("ship-task", "Fix the login redirect", "done"));
        var bridge = await harness.StartInstance().InitializedAsync();

        await bridge.WatchAsync();
        harness.FirstMate.AppendLedger("""{"v":1,"ts":"2026-09-26T14:10:00Z","event":"task.pr_ready","task":"ship-task","pr":"https://github.com/acme/webapp/pull/7"}""");
        await bridge.WatchAsync();

        var alert = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal("A PR in a private project is ready for your review.", alert.Text);
    }

    [Fact]
    public async Task FirstMates_free_text_replies_are_filtered_best_effort_on_their_way_out()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Config = harness.Config with { DenyList = ["acme/webapp"] };
        var bridge = await harness.StartInstance().InitializedAsync();

        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "how is the webapp work going");
        await bridge.PollAsync();
        var noteId = Assert.Single(harness.FirstMate.NoteIds());

        // The request itself still carries the user's words: the deny list governs what the bridge SENDS to
        // Telegram, not what it hands FirstMate, which already owns its own records.
        Assert.StartsWith("how is the webapp work going", Assert.Single(harness.FirstMate.Calls("note")).StandardInput);
        _ = messageId;

        harness.FirstMate.Reply(noteId, "The acme/webapp work is nearly done; see https://github.com/acme/webapp/pull/7");
        await bridge.ForwardAsync();

        var reply = Assert.Single(harness.Telegram.SentMessages(), message => message.Text.Contains("nearly done"));
        Assert.Equal("The a private project work is nearly done; see a private project", reply.Text);
    }

    static string TaskSnapshotJson(string id, string title, string state) => new JsonObject
    {
        ["schema"] = "fm-fleet-snapshot.v1",
        ["tasks"] = new JsonArray(new JsonObject
        {
            ["id"] = id,
            ["kind"] = "ship",
            ["project"] = "acme/webapp",
            ["backlog"] = new JsonObject { ["title"] = title, ["repo"] = "acme/webapp" },
            ["current_state"] = new JsonObject { ["state"] = state },
            ["hints"] = new JsonObject { ["open_decisions"] = new JsonArray() },
        }),
        ["backlog"] = new JsonObject { ["records"] = new JsonArray() },
    }.ToJsonString();
}
