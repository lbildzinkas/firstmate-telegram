using System.Collections.Immutable;
using FirstmateTelegram.FirstMate;
using FirstmateTelegram.State;
using FirstmateTelegram.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FirstmateTelegram.Bridge;

/// <summary>
/// Watches FirstMate's records for what would interrupt the user (spec 4.4, 7.2.6): it tails the fleet activity
/// ledger for PRs ready for review, finished research and worker statuses, and compares successive fleet
/// snapshots for live captain holds. A worker's needs-decision, blocked or failed status first waits out the
/// settle window and alerts only if the task is still open in the snapshot and holds no decision alert. Every
/// alert goes out exactly once per dedupe key, and nothing from before the bridge's first start alerts.
/// </summary>
public sealed class AlertWatcher : BackgroundService
{
    public static readonly TimeSpan LedgerCadence = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DecisionsCadence = TimeSpan.FromMinutes(2);

    readonly FirstMateClient _firstMate;
    readonly FleetLedger _ledger;
    readonly AlertSender _sender;
    readonly StateStore _store;
    readonly Redactor _redactor;
    readonly TimeProvider _time;
    readonly TimeSpan _settle;
    readonly long _chatId;
    readonly ILogger<AlertWatcher> _logger;
    readonly Dictionary<string, string> _dispatchedKinds = new(StringComparer.Ordinal);
    DateTimeOffset _nextDecisionsReadAt;

    public AlertWatcher(
        FirstMateClient firstMate,
        FleetLedger ledger,
        AlertSender sender,
        StateStore store,
        Redactor redactor,
        TimeProvider time,
        TimeSpan settleWindow,
        long chatId,
        ILogger<AlertWatcher> logger)
    {
        _firstMate = firstMate;
        _ledger = ledger;
        _sender = sender;
        _store = store;
        _redactor = redactor;
        _time = time;
        _settle = settleWindow;
        _chatId = chatId;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await WatchOnceAsync(stoppingToken);
            await Task.Delay(LedgerCadence, _time, stoppingToken);
        }
    }

    public async Task WatchOnceAsync(CancellationToken cancellationToken)
    {
        var readDecisionsNow = await TailLedgerAsync(cancellationToken);
        var now = _time.GetUtcNow();
        if (readDecisionsNow || now >= _nextDecisionsReadAt)
        {
            // The decisions are read before the settles are judged, so a hold that covers a task is already in
            // the alert history when its worker status's settle window closes (spec 7.2.6).
            await ReadDecisionsAsync(cancellationToken);
            _nextDecisionsReadAt = now + DecisionsCadence;
        }

        await FireDueSettlesAsync(cancellationToken);
    }

    /// <summary>Reads the ledger's new records and turns them into alerts and settle watches. True when a needs-decision record asks for the decisions list right away.</summary>
    async Task<bool> TailLedgerAsync(CancellationToken cancellationToken)
    {
        var stored = _store.State.Ledger;
        var read = _ledger.Read(stored is { } position ? new LedgerPosition(position.Device, position.Inode, position.Offset) : null);
        if (read is null)
            return false;

        if (read.SkippedOtherVersions > 0)
            _logger.LogWarning("skipped {Count} fleet ledger record(s) with an unknown version", read.SkippedOtherVersions);
        await _store.UpdateStateAsync(
            state => state with { Ledger = new LedgerPositionView(read.Position.Device, read.Position.Inode, read.Position.Offset) },
            cancellationToken);

        var readDecisions = false;
        foreach (var anEvent in read.Events)
        {
            switch (anEvent)
            {
                case LedgerEvent.Dispatched dispatched when dispatched.Kind is not null:
                    _dispatchedKinds[dispatched.Task] = dispatched.Kind;
                    break;

                case LedgerEvent.PrReady prReady:
                    await SendPrReadyAsync(prReady, cancellationToken);
                    break;

                case LedgerEvent.Status status:
                    readDecisions |= await HandleStatusAsync(status, cancellationToken);
                    break;
            }
        }

        return readDecisions;
    }

    async Task SendPrReadyAsync(LedgerEvent.PrReady prReady, CancellationToken cancellationToken)
    {
        var key = $"pr:{prReady.Task}:{prReady.PrUrl}";
        var task = await FindTaskAsync(prReady.Task, cancellationToken);
        if (task is { } found && IsDenied(found))
        {
            await _sender.SendAsync(key, prReady.Task, BridgeTexts.PrReadyPrivateAlert, _chatId, cancellationToken);
            return;
        }

        var title = task?.Title ?? prReady.Task;
        await _sender.SendAsync(key, prReady.Task, BridgeTexts.PrReadyAlert(title, prReady.PrUrl), _chatId, cancellationToken);
    }

    async Task<bool> HandleStatusAsync(LedgerEvent.Status status, CancellationToken cancellationToken)
    {
        switch (status.State)
        {
            case "done":
                if (await IsScoutAsync(status.Task, cancellationToken))
                    await _sender.SendAsync($"research:{status.Task}", status.Task, await ResearchTextAsync(status.Task, cancellationToken), _chatId, cancellationToken);
                return false;

            case "needs-decision" or "blocked" or "failed":
                var stamp = status.Key ?? status.At?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? _time.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                var key = $"status:{status.Task}:{status.State}:{stamp}";
                if (_store.Alerts.ContainsKey(key))
                    return status.State == "needs-decision";

                var existing = _store.State.Settles.Where(watch => watch.Task == status.Task && watch.State == status.State && watch.Key == status.Key).ToList();
                if (existing.Count == 0)
                    await _store.UpdateStateAsync(
                        state => state with { Settles = [.. state.Settles, new SettleWatch(status.Task, status.State, status.Key, status.Text, _time.GetUtcNow() + _settle, key)] },
                        cancellationToken);
                return status.State == "needs-decision";

            default:
                return false;
        }
    }

    /// <summary>A task's settle-window check: the alert fires only if the snapshot still shows the task in the same state and no decision alert exists for it.</summary>
    async Task FireDueSettlesAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var due = _store.State.Settles.Where(watch => watch.DueAt <= now).OrderBy(watch => watch.DueAt).ToList();
        if (due.Count == 0)
            return;

        var snapshot = await ReadSnapshotAsync(cancellationToken);
        if (snapshot is null)
            return; // The watches stay; the check runs again on the next pass.

        var settled = new List<SettleWatch>();
        foreach (var watch in due)
        {
            var task = snapshot.Tasks.FirstOrDefault(row => row.Id == watch.Task);
            var stillOpen = task is not null && IsStillOpen(task, watch);
            var hasDecisionAlert = _store.Alerts.Keys.Any(key => key.StartsWith($"decision:{watch.Task}:", StringComparison.Ordinal));
            if (stillOpen && !hasDecisionAlert)
            {
                var text = task is not null && IsDenied(task)
                    ? PrivateSettleText(watch.State)
                    : SettleText(watch.State, task?.Title ?? watch.Task, watch.Text);
                await _sender.SendAsync(watch.AlertKey, watch.Task, text, _chatId, cancellationToken);
            }
            else
            {
                _logger.LogInformation(
                    "dropped the {State} alert for task {Task}: {Why}",
                    watch.State,
                    watch.Task,
                    stillOpen ? "a decision alert already covers the task" : "the snapshot no longer shows it open");
            }

            settled.Add(watch);
        }

        await _store.UpdateStateAsync(state => state with { Settles = state.Settles.Except(settled).ToList() }, cancellationToken);
    }

    static bool IsStillOpen(FleetTask task, SettleWatch watch) =>
        task.State == watch.State
        || (watch.State == "needs-decision" && task.HasOpenDecisions && (watch.Key is null || task.OpenDecisionsRaw.Contains(watch.Key, StringComparison.Ordinal)));

    static string SettleText(string state, string title, string? status) => state switch
    {
        "needs-decision" => string.IsNullOrWhiteSpace(status) ? BridgeTexts.WaitingOnDecisionAlert(title, "it waits on you") : BridgeTexts.WaitingOnDecisionAlert(title, status),
        "blocked" => string.IsNullOrWhiteSpace(status) ? BridgeTexts.BlockedAlert(title, "it is blocked") : BridgeTexts.BlockedAlert(title, status),
        _ => string.IsNullOrWhiteSpace(status) ? BridgeTexts.FailedAlert(title, "it failed") : BridgeTexts.FailedAlert(title, status),
    };

    static string PrivateSettleText(string state) => state switch
    {
        "needs-decision" => BridgeTexts.WaitingOnDecisionPrivateAlert,
        "blocked" => BridgeTexts.BlockedPrivateAlert,
        _ => BridgeTexts.FailedPrivateAlert,
    };

    /// <summary>Compares the fleet snapshot's live captain holds against the ones already alerted (spec 7.2.6): a new hold alerts, and a hold closed for two snapshots in a row is forgotten.</summary>
    async Task ReadDecisionsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ReadSnapshotAsync(cancellationToken);
        if (snapshot is null)
            return;

        var firstRead = _store.State.Decisions is null;
        var known = _store.State.Decisions ?? new Dictionary<string, DecisionWatch>();
        var held = snapshot.Records
            .Where(record => record.CaptainActionable && record.Id is not null)
            .GroupBy(record => record.Id!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var now = _time.GetUtcNow();
        var updated = new Dictionary<string, DecisionWatch>(known, StringComparer.Ordinal);
        foreach (var (id, record) in held)
        {
            if (updated.TryGetValue(id, out var watch))
            {
                updated[id] = watch with { AbsentStreak = 0 };
                continue;
            }

            var firstSeen = now;
            updated[id] = new DecisionWatch(firstSeen, 0);
            if (!firstRead)
            {
                await _store.UpdateStateAsync(state => state with { Decisions = updated }, cancellationToken);
                var text = _redactor.IsDenied(record.Repo)
                    ? BridgeTexts.DecisionPrivateAlert
                    : string.IsNullOrWhiteSpace(record.HoldReason)
                        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Decision needed: {record.Title}. Reply to this message with your answer.")
                        : BridgeTexts.DecisionAlert(record.Title, record.HoldReason);
                await _sender.SendAsync($"decision:{id}:{firstSeen.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}", id, text, _chatId, cancellationToken);
            }
        }

        foreach (var (id, watch) in updated.ToList())
        {
            if (held.ContainsKey(id))
                continue;
            var streak = watch.AbsentStreak + 1;
            if (streak >= 2)
                updated.Remove(id);
            else
                updated[id] = watch with { AbsentStreak = streak };
        }

        await _store.UpdateStateAsync(state => state with { Decisions = updated }, cancellationToken);
    }

    async Task<FleetSnapshot?> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var read = await _firstMate.ReadFleetSnapshotAsync(cancellationToken);
        if (read.Snapshot is not { } snapshot)
        {
            _logger.LogWarning("could not read the fleet snapshot for alerts ({Failure}, exit {ExitCode})", read.Failure!.Kind, read.Failure.ExitCode);
            return null;
        }

        return snapshot;
    }

    async Task<FleetTask?> FindTaskAsync(string taskId, CancellationToken cancellationToken) =>
        (await ReadSnapshotAsync(cancellationToken))?.Tasks.FirstOrDefault(task => task.Id == taskId);

    async Task<bool> IsScoutAsync(string taskId, CancellationToken cancellationToken) =>
        _dispatchedKinds.TryGetValue(taskId, out var kind) ? kind == "scout"
        : (await FindTaskAsync(taskId, cancellationToken))?.Kind == "scout";

    async Task<string> ResearchTextAsync(string taskId, CancellationToken cancellationToken)
    {
        var task = await FindTaskAsync(taskId, cancellationToken);
        return task is not null && IsDenied(task) ? BridgeTexts.ResearchFinishedPrivateAlert : BridgeTexts.ResearchFinishedAlert(task?.Title ?? taskId);
    }

    bool IsDenied(FleetTask task) => _redactor.IsDenied(task.Project) || _redactor.IsDenied(task.Repo);
}
