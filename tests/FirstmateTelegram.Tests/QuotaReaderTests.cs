using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

/// <summary>How the bridge reads quota-axi (spec 7.2.7): read-only, version-checked, and unknown on any gap.</summary>
public sealed class QuotaReaderTests
{
    [Fact]
    public async Task The_binding_scope_is_the_one_with_the_least_quota_left()
    {
        var reader = new QuotaReader(Scripted("quota-available.json"), "claude");

        var reading = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(new QuotaReading(Out: false, PercentLeft: 42, ResetsAt: new DateTimeOffset(2026, 9, 26, 21, 40, 0, TimeSpan.Zero)), reading);
    }

    [Fact]
    public async Task An_exhausted_runway_means_the_quota_is_out_with_its_reset_time()
    {
        var reader = new QuotaReader(Scripted("quota-exhausted.json"), "claude");

        var reading = await reader.ReadAsync(CancellationToken.None);

        Assert.True(reading.IsKnown);
        Assert.True(reading.Out);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 21, 40, 0, TimeSpan.Zero), reading.ResetsAt);
    }

    [Fact]
    public async Task An_exhausted_quota_without_a_reset_time_is_out_with_no_reset_time()
    {
        var reader = new QuotaReader(Scripted("quota-exhausted-no-reset.json"), "claude");

        var reading = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(new QuotaReading(Out: true, PercentLeft: 0, ResetsAt: null), reading);
    }

    [Theory]
    [InlineData("quota-unknown-semantics.json")]
    [InlineData("quota-keychain.json")]
    [InlineData("quota-newer-schema.json")]
    [InlineData("quota-missing-provider.json")]
    public async Task Anything_the_bridge_cannot_read_is_unknown(string fixture)
    {
        var reader = new QuotaReader(Scripted(fixture), "claude");

        Assert.Equal(QuotaReading.Unknown, await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_or_failing_quota_axi_is_unknown()
    {
        Assert.Equal(QuotaReading.Unknown, await new QuotaReader(new ScriptedProcessRunner(_ => ProcessResult.NotStarted("no such file")), "claude").ReadAsync(CancellationToken.None));
        Assert.Equal(QuotaReading.Unknown, await new QuotaReader(new ScriptedProcessRunner(_ => new ProcessResult(1, "", "quota-axi: not authorised")), "claude").ReadAsync(CancellationToken.None));
        Assert.Equal(QuotaReading.Unknown, await new QuotaReader(new ScriptedProcessRunner(_ => ProcessResult.Timeout()), "claude").ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_call_is_read_only_and_bounded()
    {
        var runner = new ScriptedProcessRunner(_ => new ProcessResult(0, Fixtures.ReadQuota("quota-available.json"), ""));
        await new QuotaReader(runner, "claude").ReadAsync(CancellationToken.None);

        var request = Assert.Single(runner.Requests);
        Assert.Equal(["--provider", "claude", "--json", "--no-credential-refresh"], request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(20), request.Timeout);
        Assert.DoesNotContain("--allow-keychain-prompt", request.Arguments);
    }

    [Fact]
    public async Task The_provider_names_the_row_and_the_label()
    {
        var runner = new ScriptedProcessRunner(_ => new ProcessResult(0, "{\"schemaVersion\":6,\"providers\":[{\"name\":\"codex\"}]}", ""));
        Assert.Equal(QuotaReading.Unknown, await new QuotaReader(runner, "claude").ReadAsync(CancellationToken.None));
        Assert.Equal("Claude", QuotaReader.ProviderDisplay("claude"));
        Assert.Equal("Codex", QuotaReader.ProviderDisplay("codex"));
    }

    static ScriptedProcessRunner Scripted(string fixture) => new(_ => new ProcessResult(0, Fixtures.ReadQuota(fixture), ""));
}
