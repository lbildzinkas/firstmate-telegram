using System.Collections.Immutable;
using FirstmateTelegram.State;
using FirstmateTelegram.Telegram;
using Microsoft.Extensions.Logging;

namespace FirstmateTelegram.Bridge;

/// <summary>
/// Sends an alert exactly once per dedupe key (spec 8.3): the key is written to the alert history before the
/// message is sent, and the Telegram message id is recorded after it, so a crash between the two can lose
/// reply-matching for that one alert but never sends it twice. While a mute is on the alert goes out silently;
/// it is never dropped. Entries are kept for 90 days, and the history is the only place the bridge stores
/// message text on disk.
/// </summary>
public sealed class AlertSender
{
    static readonly TimeSpan KeepAlerts = TimeSpan.FromDays(90);

    readonly TelegramGateway _telegram;
    readonly StateStore _store;
    readonly TimeProvider _time;
    readonly ILogger<AlertSender> _logger;

    public AlertSender(TelegramGateway telegram, StateStore store, TimeProvider time, ILogger<AlertSender> logger)
    {
        _telegram = telegram;
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <summary>True when the alert went out now; false when its key was already alerted, so never twice.</summary>
    public async Task<bool> SendAsync(string dedupeKey, string? task, string text, long chatId, CancellationToken cancellationToken)
    {
        if (_store.Alerts.ContainsKey(dedupeKey))
            return false;

        var at = _time.GetUtcNow();
        await _store.UpdateAlertsAsync(alerts => alerts.SetItem(dedupeKey, new AlertEntry { Key = dedupeKey, At = at, Task = task, Text = text }), cancellationToken);
        var silent = _store.State.MuteUntil is { } until && until > at;
        var messageId = await _telegram.SendTextAsync(chatId, text, replyToMessageId: null, silent: silent, cancellationToken);
        await _store.UpdateAlertsAsync(
            alerts => alerts.TryGetValue(dedupeKey, out var entry) ? alerts.SetItem(dedupeKey, entry with { TelegramMessageId = messageId }) : alerts,
            cancellationToken);
        await PruneAsync(cancellationToken);
        _logger.LogInformation("sent alert {AlertKey} for task {Task}{Silent}", dedupeKey, task, silent ? " (silent)" : "");
        return true;
    }

    async Task PruneAsync(CancellationToken cancellationToken)
    {
        var cutoff = _time.GetUtcNow() - KeepAlerts;
        var expired = _store.Alerts.Values.Where(entry => entry.At < cutoff).Select(entry => entry.Key).ToList();
        if (expired.Count > 0)
            await _store.UpdateAlertsAsync(alerts => alerts.RemoveRange(expired), cancellationToken);
    }
}
