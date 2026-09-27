using System.Globalization;
using FirstmateTelegram.FirstMate;
using Microsoft.Extensions.Logging;

namespace FirstmateTelegram.Bridge;

/// <summary>An availability verdict, in the order the spec's /ping table applies them (spec 4.3.2).</summary>
public enum AvailabilityVerdict
{
    NotRunning,
    NotPickingUp,
    Unknown,
    QuotaOut,
    NotResponding,
    ProbablyReady,
    Ready,
}

/// <summary>One availability check: the verdict plus the lines behind it. Shared by /ping and the live ping timeout.</summary>
public sealed record AvailabilityReport
{
    public required AvailabilityVerdict Verdict { get; init; }

    public required string VerdictLine { get; init; }

    public required string FirstMateLine { get; init; }

    public required string AwayModeLine { get; init; }

    public required string QuotaLine { get; init; }

    public required string BridgeLine { get; init; }

    /// <summary>Raw posture state from <c>ready</c>: present, away, quiet or unknown.</summary>
    public required string PostureState { get; init; }

    /// <summary>When the binding quota window resets, when that is known.</summary>
    public DateTimeOffset? QuotaResetsAt { get; init; }

    /// <summary>How long the oldest unacknowledged inbox note has waited, when there is one.</summary>
    public TimeSpan? OldestWaitingNoteFor { get; init; }

    public Readiness Readiness { get; init; }

    public bool IsAwayOrQuiet => PostureState is "away" or "quiet";

    /// <summary>The lines shown under the verdict.</summary>
    public IReadOnlyList<string> CheckLines => [FirstMateLine, AwayModeLine, QuotaLine, BridgeLine];

    /// <summary>The full /ping answer: the verdict first, then the checks behind it.</summary>
    public string Answer() => string.Join("\n", [VerdictLine, .. CheckLines]);
}

/// <summary>The posture and readiness from one <c>ready</c> read: what <c>/back</c> decides on.</summary>
public sealed record PostureReading(string PostureState, Readiness? Readiness, CallFailure? Failure);

/// <summary>
/// The availability model the ping commands share (spec 7.5): running and listening from <c>ready</c>, stopped
/// responding from the oldest unacknowledged inbox note, and quota from quota-axi. Every source is read-only;
/// none of them wakes FirstMate or spends model tokens.
/// </summary>
public sealed class AvailabilityReader
{
    readonly FirstMateClient _firstMate;
    readonly QuotaReader _quota;
    readonly TimeProvider _time;
    readonly DateTimeOffset _startedAt;
    readonly TimeSpan _unresponsiveAfter;
    readonly string _providerDisplay;
    readonly ILogger<AvailabilityReader> _logger;

    public AvailabilityReader(
        FirstMateClient firstMate,
        QuotaReader quota,
        TimeProvider time,
        DateTimeOffset bridgeStartedAt,
        TimeSpan unresponsiveAfter,
        string providerDisplay,
        ILogger<AvailabilityReader> logger)
    {
        _firstMate = firstMate;
        _quota = quota;
        _time = time;
        _startedAt = bridgeStartedAt;
        _unresponsiveAfter = unresponsiveAfter;
        _providerDisplay = providerDisplay;
        _logger = logger;
    }

    /// <summary>Reads every check. A caller on a quota budget, such as the availability monitor, passes a quota reading it already has.</summary>
    public async Task<AvailabilityReport> ReadAsync(CancellationToken cancellationToken, QuotaReading? quota = null)
    {
        var now = _time.GetUtcNow();
        var ready = await _firstMate.ReadReadyAsync(cancellationToken);
        quota ??= await _quota.ReadAsync(cancellationToken);
        var pending = await _firstMate.ReadAllPendingAsync(cancellationToken);
        if (pending.Failure is { } failure)
            _logger.LogWarning("could not read pending notes for the not-responding check ({Failure}, exit {ExitCode})", failure.Kind, failure.ExitCode);

        var readiness = ReadinessRules.Classify(ready);
        var posture = ready.Reading?.PostureState ?? "unknown";
        var oldestWaiting = pending.Notes?.OldestUnacknowledgedAt is { } oldest ? now - oldest : (TimeSpan?)null;

        var verdict = VerdictOf(readiness, quota, oldestWaiting);
        var reset = quota.ResetsAt is { } at ? LocalTime(at) : null;
        return new AvailabilityReport
        {
            Verdict = verdict,
            VerdictLine = VerdictLine(verdict, quota, oldestWaiting, reset),
            FirstMateLine = "FirstMate: " + readiness switch
            {
                Readiness.NotRunning => "not running",
                Readiness.NotPickingUp => "running, not picking up requests",
                Readiness.RunningListening => "running, listening",
                Readiness.RunningListeningUnconfirmed => "running, listening unconfirmed",
                _ => "unknown",
            },
            AwayModeLine = "Away mode: " + posture switch
            {
                "away" => "on",
                "quiet" => "quiet mode",
                "present" => "off",
                _ => "unknown",
            },
            QuotaLine = quota.IsKnown
                ? $"{_providerDisplay} quota: {quota.PercentLeft?.ToString(CultureInfo.InvariantCulture)}% left{ResetSuffix(quota, reset)}"
                : $"{_providerDisplay} quota: unknown",
            BridgeLine = "Bridge: up " + Durations.Format(now - _startedAt),
            PostureState = posture,
            Readiness = readiness,
            QuotaResetsAt = quota.ResetsAt,
            OldestWaitingNoteFor = oldestWaiting,
        };
    }

    /// <summary>Reads the quota alone, for a monitor that spaces its quota-axi calls out.</summary>
    public Task<QuotaReading> ReadQuotaAsync(CancellationToken cancellationToken) => _quota.ReadAsync(cancellationToken);

    /// <summary>Reads only the posture and readiness, the check <c>/back</c> decides on.</summary>
    public async Task<PostureReading> ReadPostureAsync(CancellationToken cancellationToken)
    {
        var ready = await _firstMate.ReadReadyAsync(cancellationToken);
        return new PostureReading(ready.Reading?.PostureState ?? "unknown", ready.Failure is null ? ReadinessRules.Classify(ready) : null, ready.Failure);
    }

    static string ResetSuffix(QuotaReading quota, string? reset)
    {
        if (quota.Out != true)
            return "";
        return reset is { } time ? $", resets {time}" : ", reset time unknown";
    }

    AvailabilityVerdict VerdictOf(Readiness readiness, QuotaReading quota, TimeSpan? oldestWaiting) =>
        readiness == Readiness.NotRunning ? AvailabilityVerdict.NotRunning
        : readiness == Readiness.NotPickingUp ? AvailabilityVerdict.NotPickingUp
        : readiness == Readiness.Unknown ? AvailabilityVerdict.Unknown
        : quota.IsKnown && quota.Out == true ? AvailabilityVerdict.QuotaOut
        : readiness == Readiness.RunningListening && oldestWaiting > _unresponsiveAfter ? AvailabilityVerdict.NotResponding
        : readiness == Readiness.RunningListeningUnconfirmed ? AvailabilityVerdict.ProbablyReady
        : AvailabilityVerdict.Ready;

    string VerdictLine(AvailabilityVerdict verdict, QuotaReading quota, TimeSpan? oldestWaiting, string? reset) => verdict switch
    {
        AvailabilityVerdict.NotRunning => BridgeTexts.VerdictNotRunning,
        AvailabilityVerdict.NotPickingUp => BridgeTexts.VerdictNotPickingUp,
        AvailabilityVerdict.Unknown => BridgeTexts.VerdictUnknown,
        AvailabilityVerdict.QuotaOut => BridgeTexts.VerdictQuotaOut(_providerDisplay, reset ?? "reset time unknown"),
        AvailabilityVerdict.NotResponding => BridgeTexts.VerdictNotResponding((int)Math.Max(1, Math.Floor(oldestWaiting!.Value.TotalMinutes))),
        AvailabilityVerdict.ProbablyReady => BridgeTexts.VerdictProbablyReady,
        _ => BridgeTexts.VerdictReady,
    };

    string LocalTime(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, _time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>Durations as the bridge writes them: <c>14 s</c>, <c>4 min</c>, <c>2 h</c>, <c>3 days</c>.</summary>
public static class Durations
{
    public static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        var seconds = (int)Math.Floor(span.TotalSeconds);
        if (seconds < 60)
            return $"{Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture)} s";
        if (span.TotalMinutes < 60)
            return $"{((int)Math.Floor(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture)} min";
        if (span.TotalHours < 24)
            return $"{((int)Math.Floor(span.TotalHours)).ToString(CultureInfo.InvariantCulture)} h";
        var days = (int)Math.Floor(span.TotalDays);
        return days == 1 ? "1 day" : $"{days.ToString(CultureInfo.InvariantCulture)} days";
    }
}
