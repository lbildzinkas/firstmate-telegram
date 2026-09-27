using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.Telegram;
using FirstmateTelegram.Tests.Fakes;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

public sealed class TelegramGatewayTests
{
    [Fact]
    public async Task Too_many_requests_waits_the_time_Telegram_gives_then_retries()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Retry = harness.Retry with { InitialBackoff = TimeSpan.FromMinutes(5), MaxBackoff = TimeSpan.FromMinutes(5) };
        var bridge = harness.StartInstance();
        harness.Telegram.FailNext("sendMessage", 429, "Too Many Requests: retry after 1", retryAfter: 1);
        var started = DateTimeOffset.UtcNow;

        await bridge.Gateway.SendTextAsync(BridgeHarness.UserId, "hello", null, cancellationToken: CancellationToken.None);

        var waited = DateTimeOffset.UtcNow - started;
        Assert.InRange(waited, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(30));
        Assert.Equal(2, harness.Telegram.CallsTo("sendMessage").Count);
        Assert.Contains("too many requests; waiting 1 s", harness.Logging.Text());
    }

    [Fact]
    public async Task A_rejected_token_is_logged_once_and_checked_again_until_it_works()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var bridge = harness.StartInstance();
        harness.Telegram.FailNext("getMe", 401, "Unauthorized", times: 3);

        var me = await bridge.Gateway.GetMeAsync(CancellationToken.None);

        Assert.Equal(FakeTelegramServer.BotId, me.Id);
        Assert.Equal(4, harness.Telegram.CallsTo("getMe").Count);
        var log = harness.Logging.Text();
        Assert.Single(log.Split('\n'), line => line.Contains("token rejected", StringComparison.Ordinal));
        Assert.Contains("token accepted again", log);
    }

    [Fact]
    public async Task A_conflict_with_another_poller_is_logged_once_and_backed_off()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var bridge = harness.StartInstance();
        harness.Telegram.FailNext("getUpdates", 409, "Conflict: terminated by other getUpdates request", times: 2);

        await bridge.Gateway.GetUpdatesAsync(0, 0, CancellationToken.None);

        Assert.Equal(3, harness.Telegram.CallsTo("getUpdates").Count);
        Assert.Single(harness.Logging.Text().Split('\n'), line => line.Contains("conflict; another poller", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Best_effort_calls_give_up_after_a_few_attempts()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var bridge = harness.StartInstance();
        harness.Telegram.FailNext("setMessageReaction", 400, "Bad Request: message to react not found", times: 10);

        var done = await bridge.Gateway.TrySetReactionAsync(BridgeHarness.UserId, 1, BotReactions.Received, CancellationToken.None);

        Assert.False(done);
        Assert.Equal(3, harness.Telegram.CallsTo("setMessageReaction").Count);
    }

    [Fact]
    public async Task Best_effort_calls_do_not_wait_out_a_rejected_token()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Retry = harness.Retry with { TokenRejectedRecheck = TimeSpan.FromHours(1) };
        var bridge = harness.StartInstance();
        harness.Telegram.FailNext("sendMessage", 401, "Unauthorized");

        Assert.False(await bridge.Gateway.TrySendTextAsync(BridgeHarness.UserId, "notice", 1, CancellationToken.None));
        Assert.Single(harness.Telegram.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task Long_polls_ask_only_for_messages()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var bridge = harness.StartInstance();

        await bridge.Gateway.GetUpdatesAsync(17, 50, CancellationToken.None);

        var call = Assert.Single(harness.Telegram.CallsTo("getUpdates"));
        Assert.Equal((17, 50), (call["offset"]!.GetValue<int>(), call["timeout"]!.GetValue<int>()));
        Assert.Equal(["message"], call["allowed_updates"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Theory]
    [InlineData(1, 1.0, 1000)]
    [InlineData(2, 1.0, 2000)]
    [InlineData(7, 1.0, 60000)]
    [InlineData(30, 1.0, 60000)]
    [InlineData(1, 0.0, 500)]
    public void Backoff_starts_at_one_second_doubles_and_caps_with_jitter(int failures, double random, int expectedMilliseconds)
    {
        var delay = Backoff.Jittered(Backoff.Exponential(failures, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)), random);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), delay);
    }

    [Fact]
    public void Request_retries_back_off_from_one_second_to_five_minutes()
    {
        var delays = Enumerable.Range(1, 11).Select(failures => Backoff.Exponential(failures, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5)).TotalSeconds);

        Assert.Equal([1, 2, 4, 8, 16, 32, 64, 128, 256, 300, 300], delays);
    }
}
