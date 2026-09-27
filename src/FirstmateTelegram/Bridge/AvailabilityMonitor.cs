using System.Globalization;
using FirstmateTelegram.FirstMate;
using FirstmateTelegram.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FirstmateTelegram.Bridge;

/// <summary>
/// The availability alerts (spec 4.4.1, 7.5.4): it polls <c>ready</c> and the inbox every 60 s and quota every
/// 5 minutes, and sends an alert only while FirstMate is truly in away mode; quiet mode does not count. A bad
/// verdict, and a return to Ready, each need two polls in a row spanning at least two minutes before anything
/// alerts; nothing alerts during the first 5 minutes after a wake; and an outage already present when away mode
/// begins alerts once at the first poll in away mode. Away mode ending stops the alerts: nothing is sent while
/// the user is back, and an outage still present when away mode begins again is the spec's "present when away
/// mode starts" case.
/// </summary>
public sealed class AvailabilityMonitor : BackgroundService
{
    public static readonly TimeSpan PollCadence = TimeSpan.FromSeconds(60);
    static readonly TimeSpan QuotaCadence = TimeSpan.FromMinutes(5);
    static readonly TimeSpan DebounceWindow = TimeSpan.FromMinutes(2);
    static readonly TimeSpan WakeGrace = TimeSpan.FromMinutes(5);

    readonly AvailabilityReader _availability;
    readonly AlertSender _alerts;
    readonly StateStore _store;
    readonly TimeProvider _time;
    readonly string _providerDisplay;
    readonly long _chatId;
    readonly ILogger<AvailabilityMonitor> _logger;
    DateTimeOffset _lastPollAt;
    DateTimeOffset _suppressUntil;
    DateTimeOffset _lastQuotaReadAt;
    QuotaReading _quota = QuotaReading.Unknown;

    public AvailabilityMonitor(
        AvailabilityReader availability,
        AlertSender alerts,
        StateStore store,
        TimeProvider time,
        string providerDisplay,
        long chatId,
        ILogger<AvailabilityMonitor> logger)
    {
        _availability = availability;
        _alerts = alerts;
        _store = store;
        _time = time;
        _providerDisplay = providerDisplay;
        _chatId = chatId;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckOnceAsync(stoppingToken);
            await Task.Delay(PollCadence, _time, stoppingToken);
        }
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        // The wall clock jumping by more than twice the poll interval is the Mac waking: FirstMate's monitoring
        // needs a moment to catch up, so nothing alerts for the first 5 minutes and the debounce starts over.
        if (_lastPollAt != default && now - _lastPollAt > 2 * PollCadence)
        {
            _logger.LogInformation("wall clock jumped {Seconds} s between polls; treating it as a wake and holding alerts for {Minutes} min", (now - _lastPollAt).TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture), WakeGrace.TotalMinutes.ToString(CultureInfo.InvariantCulture));
            _suppressUntil = now + WakeGrace;
            await _store.UpdateStateAsync(state => state with { Availability = null }, cancellationToken);
        }

        _lastPollAt = now;
        var report = await _availability.ReadAsync(cancellationToken, await ReadQuotaAsync(cancellationToken));
        await EvaluateAsync(report, now, cancellationToken);
    }

    async Task EvaluateAsync(AvailabilityReport report, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var away = report.PostureState == "away";
        var watch = _store.State.Availability;

        if (report.Verdict == AvailabilityVerdict.Ready)
        {
            await TrackReadyAsync(watch, now, away, cancellationToken);
            return;
        }

        if (IsAlertable(report.Verdict))
        {
            await TrackBadVerdictAsync(watch, report, now, away, cancellationToken);
            return;
        }

        // An unknown or unconfirmed reading proves nothing either way; both debounces start over, and an outage
        // that was already alerted waits for a confirmed Ready before it can say "available again".
        if (watch is not null)
            await _store.UpdateStateAsync(state => state with { Availability = watch with { BadStreak = 0, GoodStreak = 0, GoodSince = null } }, cancellationToken);
    }

    async Task TrackReadyAsync(AvailabilityWatch? watch, DateTimeOffset now, bool away, CancellationToken cancellationToken)
    {
        if (watch is null)
            return;

        var goodSince = watch.GoodSince ?? now;
        var streak = watch.GoodStreak + 1;
        if (watch.Alerted && streak >= 2 && now - goodSince >= DebounceWindow)
        {
            // "Available again" is sent only for an outage the bridge alerted, and only while away mode is on.
            if (away && now >= _suppressUntil)
            {
                await _alerts.SendAsync(
                    $"availability:back:{watch.BadSince.ToString("O", CultureInfo.InvariantCulture)}",
                    null,
                    BridgeTexts.AvailableAgain(WasPhrase(watch.BadVerdict), now - watch.BadSince),
                    _chatId,
                    cancellationToken);
            }

            await _store.UpdateStateAsync(state => state with { Availability = null }, cancellationToken);
            return;
        }

        await _store.UpdateStateAsync(
            state => state with { Availability = watch with { BadStreak = 0, GoodStreak = streak, GoodSince = goodSince } },
            cancellationToken);
    }

    async Task TrackBadVerdictAsync(AvailabilityWatch? watch, AvailabilityReport report, DateTimeOffset now, bool away, CancellationToken cancellationToken)
    {
        // A different bad verdict begins its own window; the outage it replaced never became Ready, so it gets
        // no "available again" of its own.
        if (watch is null || watch.BadVerdict != VerdictName(report.Verdict))
            watch = new AvailabilityWatch { BadVerdict = VerdictName(report.Verdict), BadSince = now, Alerted = false, BadStreak = 0, GoodStreak = 0, GoodSince = null };

        var streak = watch.BadStreak + 1;
        var settled = streak >= 2 && now - watch.BadSince >= DebounceWindow;
        if (!watch.Alerted && settled)
        {
            // An outage present when away mode begins alerts here, at the first poll inside away mode, because
            // its debounce ran while the user was still present.
            if (away && now >= _suppressUntil)
            {
                var key = $"availability:{VerdictName(report.Verdict)}:{watch.BadSince.ToString("O", CultureInfo.InvariantCulture)}";
                await _alerts.SendAsync(key, null, AlertText(report), _chatId, cancellationToken);
                watch = watch with { Alerted = true };
            }
        }

        watch = watch with { BadStreak = streak, GoodStreak = 0, GoodSince = null };
        await _store.UpdateStateAsync(state => state with { Availability = watch }, cancellationToken);
    }

    static bool IsAlertable(AvailabilityVerdict verdict) =>
        verdict is AvailabilityVerdict.NotRunning or AvailabilityVerdict.NotPickingUp or AvailabilityVerdict.NotResponding or AvailabilityVerdict.QuotaOut;

    static string VerdictName(AvailabilityVerdict verdict) => verdict.ToString();

    static string WasPhrase(string verdict) => verdict switch
    {
        nameof(AvailabilityVerdict.NotRunning) => "stopped",
        nameof(AvailabilityVerdict.NotPickingUp) => "not picking up requests",
        nameof(AvailabilityVerdict.NotResponding) => "not responding",
        nameof(AvailabilityVerdict.QuotaOut) => "out of quota",
        _ => "unavailable",
    };

    string AlertText(AvailabilityReport report) => report.Verdict switch
    {
        AvailabilityVerdict.NotRunning => BridgeTexts.AwayStopped,
        AvailabilityVerdict.NotPickingUp => BridgeTexts.AwayNotPickingUp,
        AvailabilityVerdict.NotResponding => BridgeTexts.AwayNotResponding((int)Math.Max(1, Math.Floor(report.OldestWaitingNoteFor?.TotalMinutes ?? 1))),
        AvailabilityVerdict.QuotaOut => report.QuotaResetsAt is { } reset
            ? BridgeTexts.AwayQuotaOut(_providerDisplay, LocalTime(reset))
            : BridgeTexts.AwayQuotaOutNoReset,
        _ => "",
    };

    string LocalTime(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, _time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);

    async Task<QuotaReading> ReadQuotaAsync(CancellationToken cancellationToken)
    {
        if (_time.GetUtcNow() - _lastQuotaReadAt < QuotaCadence)
            return _quota;

        _quota = await _availability.ReadQuotaAsync(cancellationToken);
        _lastQuotaReadAt = _time.GetUtcNow();
        return _quota;
    }
}
