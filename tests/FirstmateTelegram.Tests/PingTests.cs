using FirstmateTelegram.Bridge;
using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.Tests.Fakes;
using FirstmateTelegram.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FirstmateTelegram.Tests;

/// <summary>The instant /ping: the bridge answers every ping itself, whatever FirstMate's state (spec 4.3.2, 7.5).</summary>
public sealed class PingTests
{
    static FakeTimeProvider UtcTime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 14, 5, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return time;
    }

    [Theory]
    [InlineData(
        "ready-running-listening.json",
        "Ready\nFirstMate: running, listening\nAway mode: off\nClaude quota: unknown\nBridge: up 1 s")]
    [InlineData(
        "ready-not-running.json",
        "Not running; requests will queue\nFirstMate: not running\nAway mode: off\nClaude quota: unknown\nBridge: up 1 s")]
    [InlineData(
        "ready-not-running-away.json",
        "Not running; requests will queue\nFirstMate: not running\nAway mode: on\nClaude quota: unknown\nBridge: up 1 s")]
    [InlineData(
        "ready-not-picking-up.json",
        "Running, but not picking up requests; requests will queue\nFirstMate: running, not picking up requests\nAway mode: off\nClaude quota: unknown\nBridge: up 1 s")]
    [InlineData(
        "ready-listening-unconfirmed.json",
        "Probably ready; listening could not be confirmed\nFirstMate: running, listening unconfirmed\nAway mode: off\nClaude quota: unknown\nBridge: up 1 s")]
    [InlineData(
        "ready-running-away.json",
        "Ready\nFirstMate: running, listening\nAway mode: on\nClaude quota: unknown\nBridge: up 1 s")]
    public async Task The_verdict_follows_the_readiness_and_posture_checks(string readyFixture, string expected)
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.SetReady(readyFixture);
        var time = UtcTime();
        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "/Ping@FirstMate_Test_Bot");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();

        var sent = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal((expected, messageId), (sent.Text, sent.ReplyToMessageId));
        // A ping never becomes a FirstMate turn and never reads the snapshots.
        Assert.Empty(harness.FirstMate.Calls("note"));
        Assert.Empty(harness.FirstMate.SnapshotCalls("bearings"));
        Assert.Empty(harness.FirstMate.SnapshotCalls("fleet"));
    }

    [Fact]
    public async Task Ping_says_unknown_when_the_readiness_cannot_be_read()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.Script("ready", new { exit = 1, stderr = "ready broke" });
        var time = UtcTime();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/ping");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();

        Assert.Equal(
            "Unknown: could not confirm FirstMate is running\nFirstMate: unknown\nAway mode: unknown\nClaude quota: unknown\nBridge: up 1 s",
            Assert.Single(harness.Telegram.SentMessages()).Text);
    }

    [Fact]
    public async Task Ping_says_not_responding_when_the_oldest_inbox_note_has_waited_too_long()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.AddOtherNote("a note from another inbox client");
        var time = UtcTime();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/ping");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();

        // The other note was written at 00:00 and the clock stands at 14:05.
        Assert.Equal(
            "Running, but not responding: a request has waited 845 min\nFirstMate: running, listening\nAway mode: off\nClaude quota: unknown\nBridge: up 1 s",
            Assert.Single(harness.Telegram.SentMessages()).Text);
        var pending = Assert.Single(harness.FirstMate.Calls("receipts"), call => call.Arguments.Contains("--all-pending"));
        Assert.Equal(["receipts", "--all-pending"], pending.Arguments);
    }

    [Fact]
    public async Task Ping_ignores_a_pending_note_whose_announcement_is_known_to_have_failed()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.FirstMate.AddOtherNote("a note the wake never reached", announced: false);
        var time = UtcTime();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "/ping");
        var bridge = await harness.StartInstance(time: time).InitializedAsync();

        await bridge.PollAsync();

        Assert.StartsWith("Ready\n", Assert.Single(harness.Telegram.SentMessages()).Text);
    }

    static ScriptedProcessRunner ScriptedFirstMateAndQuota(string quotaFixture) => new(request =>
    {
        if (request.FileName.EndsWith("quota-axi"))
            return new ProcessResult(0, Fixtures.ReadQuota(quotaFixture), "");
        return request.Arguments[0] switch
        {
            "ready" => new ProcessResult(0, Fixtures.Read("ready-running-listening.json"), ""),
            "receipts" => new ProcessResult(0, "{\"schema\":\"fm-inbox-receipts.v1\",\"home\":\"me/firstmate\",\"generated\":\"2026-09-26T14:05:00Z\",\"pending\":[],\"handled\":[],\"replies\":[],\"reply_cursor\":\"\",\"omitted\":[]}", ""),
            _ => throw new InvalidOperationException(string.Join(' ', request.Arguments)),
        };
    });

    [Fact]
    public async Task Ping_reports_the_quota_and_its_reset_time_when_quota_axi_answers()
    {
        using var home = new FakeFirstMateHome();
        var runner = ScriptedFirstMateAndQuota("quota-exhausted.json");
        var time = UtcTime();
        var availability = new AvailabilityReader(
            new FirstMateClient(runner, home.Home),
            new QuotaReader(runner, "claude"),
            time,
            time.GetUtcNow(),
            TimeSpan.FromMinutes(15),
            QuotaReader.ProviderDisplay("claude"),
            NullLogger<AvailabilityReader>.Instance);

        var report = await availability.ReadAsync(CancellationToken.None);

        Assert.Equal(
            "Running, but Claude quota is out until 21:40\nFirstMate: running, listening\nAway mode: off\nClaude quota: 0% left, resets 21:40\nBridge: up 1 s",
            report.Answer());
        var quotaCall = Assert.Single(runner.Requests, request => request.FileName.EndsWith("quota-axi"));
        Assert.Equal(["--provider", "claude", "--json", "--no-credential-refresh"], quotaCall.Arguments);
    }

    [Fact]
    public async Task A_read_but_not_exhausted_quota_leaves_the_verdict_ready()
    {
        using var home = new FakeFirstMateHome();
        var runner = ScriptedFirstMateAndQuota("quota-available.json");
        var time = UtcTime();
        var availability = new AvailabilityReader(
            new FirstMateClient(runner, home.Home),
            new QuotaReader(runner, "claude"),
            time,
            time.GetUtcNow(),
            TimeSpan.FromMinutes(15),
            QuotaReader.ProviderDisplay("claude"),
            NullLogger<AvailabilityReader>.Instance);

        var report = await availability.ReadAsync(CancellationToken.None);

        Assert.Equal(AvailabilityVerdict.Ready, report.Verdict);
        Assert.Equal("Claude quota: 42% left", report.QuotaLine);
    }
}
