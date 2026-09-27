using System.Globalization;
using System.Text.Json;
using FirstmateTelegram.Infrastructure;

namespace FirstmateTelegram.FirstMate;

/// <summary>What quota-axi said about the model quota: unknown, or a percentage left with a reset time when it is out.</summary>
public sealed record QuotaReading(bool? Out, int? PercentLeft, DateTimeOffset? ResetsAt)
{
    public bool IsKnown => Out is not null;

    public static readonly QuotaReading Unknown = new(null, null, null);
}

/// <summary>
/// Reads the model quota through <c>quota-axi</c>, read-only: <c>--no-credential-refresh</c> keeps the call from
/// renewing vendor credentials, and a missing, slow, failed or unreadable answer is always <see cref="QuotaReading.Unknown"/>,
/// never a guess (spec 7.2.7).
/// </summary>
public sealed class QuotaReader
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    readonly IProcessRunner _runner;
    readonly string _provider;
    readonly string _binary;

    public QuotaReader(IProcessRunner runner, string provider, string? binary = null)
    {
        _runner = runner;
        _provider = provider;
        _binary = binary ?? "quota-axi";
    }

    /// <summary>The provider as it reads in a sentence: <c>claude</c> becomes <c>Claude</c>.</summary>
    public static string ProviderDisplay(string provider) =>
        string.IsNullOrEmpty(provider) ? provider : char.ToUpper(provider[0], CultureInfo.InvariantCulture) + provider[1..];

    public async Task<QuotaReading> ReadAsync(CancellationToken cancellationToken)
    {
        var request = new ProcessRequest(_binary, ["--provider", _provider, "--json", "--no-credential-refresh"], Timeout);
        var result = await _runner.RunAsync(request, cancellationToken);
        if (!result.Ran || result.ExitCode != 0)
            return QuotaReading.Unknown;

        QuotaJson? document;
        try
        {
            document = JsonSerializer.Deserialize(result.StandardOutput, QuotaJsonContext.Default.QuotaJson);
        }
        catch (JsonException)
        {
            return QuotaReading.Unknown;
        }

        if (document?.SchemaVersion is not (5 or 6))
            return QuotaReading.Unknown;

        var row = (document.Providers ?? []).FirstOrDefault(provider => provider?.Name == _provider);
        if (row is null)
            return QuotaReading.Unknown;

        // An authorisation gap such as a Keychain prompt is as good as no answer.
        if (row.State?.Status == "keychain_prompt_required")
            return QuotaReading.Unknown;

        var semantics = row.QuotaSemantics;
        if (semantics is null || semantics.Status == "unknown")
            return QuotaReading.Unknown;

        // The binding scope is the known entry with the least quota left.
        var binding = (semantics.EffectiveAvailability ?? [])
            .Where(entry => entry?.EffectivePercentRemaining is not null)
            .OrderBy(entry => entry.EffectivePercentRemaining)
            .FirstOrDefault();
        if (binding is null)
            return QuotaReading.Unknown;

        var percent = (int)Math.Round(binding.EffectivePercentRemaining!.Value, MidpointRounding.AwayFromZero);
        var isOut = binding.Runway?.Status == "exhausted_now" || binding.EffectivePercentRemaining <= 0;

        DateTimeOffset? resetsAt = null;
        var windows = row.Windows ?? [];
        foreach (var id in binding.LimitingWindowIds ?? [])
        {
            var reset = windows.FirstOrDefault(window => window?.Id == id)?.ResetsAt;
            if (reset is { } at)
            {
                resetsAt = at;
                break;
            }
        }

        return new QuotaReading(isOut, percent, resetsAt);
    }
}
