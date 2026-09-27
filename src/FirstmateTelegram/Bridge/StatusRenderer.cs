using System.Globalization;
using System.Text;
using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Telegram;
using Microsoft.Extensions.Logging;

namespace FirstmateTelegram.Bridge;

/// <summary>
/// The /status answer: four parts projected from FirstMate's saved records, without interrupting FirstMate or
/// spending model tokens (spec 4.3.1 and 7.2.5). Away mode makes the bearings projection refuse (exit 3), and the
/// canonical fleet snapshot is used instead with its own simpler projection. A structured row whose project or
/// repository is on the deny list shows as "a private project", with no title, state or link (spec 6).
/// </summary>
public sealed class StatusRenderer
{
    const int ItemsPerPart = 8;
    const int LandedFromFleet = 6;
    const string FleetLabel = "away mode: from FirstMate's fleet records";

    readonly FirstMateClient _firstMate;
    readonly TimeProvider _time;
    readonly Redactor _redactor;
    readonly ILogger<StatusRenderer> _logger;

    public StatusRenderer(FirstMateClient firstMate, TimeProvider time, ILogger<StatusRenderer> logger, Redactor? redactor = null)
    {
        _firstMate = firstMate;
        _time = time;
        _redactor = redactor ?? Redactor.None;
        _logger = logger;
    }

    public async Task<string> RenderAsync(CancellationToken cancellationToken)
    {
        // The header says when away or quiet mode is on, read-only like everything else here.
        var ready = await _firstMate.ReadReadyAsync(cancellationToken);
        var posture = ready.Reading?.PostureState;

        var bearings = await _firstMate.ReadBearingsAsync(cancellationToken);
        if (bearings.AwayRefused)
        {
            var fleet = await _firstMate.ReadFleetSnapshotAsync(cancellationToken);
            if (fleet.Snapshot is not { } snapshot)
            {
                _logger.LogWarning("status fell back to the fleet snapshot but could not read it ({Failure}, exit {ExitCode})", fleet.Failure!.Kind, fleet.Failure.ExitCode);
                return BridgeTexts.RecordsUnreadable(fleet.Failure.Reason);
            }

            return Header(posture, fromFleetRecords: true) + "\n" + Render(FromFleet(snapshot));
        }

        if (bearings.Snapshot is not { } primary)
        {
            _logger.LogWarning("status could not read the bearings projection ({Failure}, exit {ExitCode})", bearings.Failure!.Kind, bearings.Failure.ExitCode);
            return BridgeTexts.RecordsUnreadable(bearings.Failure.Reason);
        }

        return Header(posture, fromFleetRecords: false) + "\n" + Render(FromBearings(primary));
    }

    string Header(string? posture, bool fromFleetRecords)
    {
        var labels = new List<string>();
        if (posture == "away")
            labels.Add("away mode on");
        else if (posture == "quiet")
            labels.Add("quiet mode on");
        if (fromFleetRecords)
            labels.Add(FleetLabel);
        var time = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);
        return labels.Count == 0 ? $"FirstMate status, {time}" : $"FirstMate status, {time} ({string.Join("; ", labels)})";
    }

    Parts FromBearings(BearingsSnapshot snapshot)
    {
        var reviewed = snapshot.RecordedPrs
            .GroupBy(pr => pr.TaskId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Url, StringComparer.Ordinal);

        var needsYou = snapshot.OpenDecisions.Select(summary => $"Decide: {summary}").ToList();
        var underWay = new List<string>();
        foreach (var work in snapshot.InFlight)
        {
            if (_redactor.IsDenied(work.Repo))
            {
                underWay.Add(Redactor.Mask);
                continue;
            }

            if (work.State == "done")
            {
                if (reviewed.TryGetValue(work.Id, out var url))
                {
                    needsYou.Add(string.IsNullOrWhiteSpace(url) ? $"Review: {work.Name}" : $"Review: {work.Name} {url}");
                    continue;
                }

                if (work.Kind == "scout")
                {
                    needsYou.Add($"Research: {work.Name}");
                    continue;
                }
            }

            underWay.Add(string.IsNullOrWhiteSpace(work.Doing) ? $"{work.Name} ({work.State ?? "under way"})" : $"{work.Name} ({work.Doing})");
        }

        var landed = snapshot.Landed
            .Select(item => string.IsNullOrWhiteSpace(item.Artifact) ? item.What : $"{item.What} {item.Artifact}")
            .ToList();
        var next = snapshot.Gates
            .Select(gate => !string.IsNullOrWhiteSpace(gate.BlockedBy) ? $"{gate.Title} (waits on: {gate.BlockedBy})"
                : !string.IsNullOrWhiteSpace(gate.Reason) ? $"{gate.Title} ({gate.Reason})"
                : gate.Title)
            .ToList();
        return new Parts(needsYou, landed, underWay, next);
    }

    Parts FromFleet(FleetSnapshot snapshot)
    {
        var titles = snapshot.Records
            .Where(record => record.Id is not null)
            .GroupBy(record => record.Id!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Title, StringComparer.Ordinal);

        var needsYou = snapshot.Records
            .Where(record => record.CaptainActionable)
            .Select(record => _redactor.IsDenied(record.Repo) ? Redactor.Mask
                : string.IsNullOrWhiteSpace(record.HoldReason) ? $"Decide: {record.Title}" : $"Decide: {record.Title}. {record.HoldReason}")
            .ToList();
        var landed = snapshot.Records
            .Where(record => record.State == "done")
            .OrderByDescending(record => record.CompletedAt)
            .Take(LandedFromFleet)
            .Select(record => _redactor.IsDenied(record.Repo) ? Redactor.Mask : record.Title)
            .ToList();
        var underWay = snapshot.Tasks
            .Where(task => !task.SecondMate)
            .Select(task => _redactor.IsDenied(task.Project) || _redactor.IsDenied(task.Repo) ? Redactor.Mask
                : string.IsNullOrWhiteSpace(task.Project) ? $"{task.Title} ({task.State ?? "under way"})" : $"{task.Project}: {task.Title} ({task.State ?? "under way"})")
            .ToList();
        var next = snapshot.Records
            .Where(record => record.State == "queued" && !record.CaptainActionable)
            .Select(record =>
            {
                if (_redactor.IsDenied(record.Repo))
                    return Redactor.Mask;
                var waitsOn = record.UnresolvedBlockerIds
                    .Select(id => titles.TryGetValue(id, out var title) ? title : null)
                    .Where(title => title is not null)
                    .ToList();
                return waitsOn.Count == 0 ? record.Title : $"{record.Title} (waits on: {string.Join(", ", waitsOn!)})";
            })
            .ToList();
        return new Parts(needsYou, landed, underWay, next);
    }

    static string Render(Parts parts) => string.Join("\n", PartsOf("Needs you", parts.NeedsYou).Concat(PartsOf("Recently landed", parts.Landed)).Concat(PartsOf("Under way", parts.UnderWay)).Concat(PartsOf("Next", parts.Next)));

    static IEnumerable<string> PartsOf(string title, IReadOnlyList<string> items)
    {
        yield return items.Count == 0 ? $"{title} (0)" : $"{title} ({items.Count.ToString(CultureInfo.InvariantCulture)})";
        if (items.Count == 0)
        {
            yield return "• nothing";
            yield break;
        }

        foreach (var item in items.Take(ItemsPerPart))
            yield return $"• {item}";
        if (items.Count > ItemsPerPart)
            yield return $"+{(items.Count - ItemsPerPart).ToString(CultureInfo.InvariantCulture)} more";
    }

    sealed record Parts(IReadOnlyList<string> NeedsYou, IReadOnlyList<string> Landed, IReadOnlyList<string> UnderWay, IReadOnlyList<string> Next);
}
