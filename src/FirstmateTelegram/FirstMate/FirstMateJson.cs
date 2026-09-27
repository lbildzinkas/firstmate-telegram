using System.Text.Json;
using System.Text.Json.Serialization;

namespace FirstmateTelegram.FirstMate;

// The JSON shapes printed by FirstMate's bin/fm-inbox.sh. Members the bridge does not use are left out and ignored.

public sealed class NoteJson
{
    public string? Schema { get; init; }
    public string? Outcome { get; init; }
    public string? Id { get; init; }
    public string? RequestId { get; init; }
    public bool? Saved { get; init; }
    public bool? Announced { get; init; }
    public bool? Acknowledged { get; init; }
}

public sealed class ReceiptsJson
{
    public string? Schema { get; init; }
    public List<NoteRowJson>? Pending { get; init; }
    public List<NoteRowJson>? Handled { get; init; }
    public List<ReplyRowJson>? Replies { get; init; }
    public string? ReplyCursor { get; init; }
    public List<OmittedJson>? Omitted { get; init; }
}

public sealed class NoteRowJson
{
    public string? Id { get; init; }
    public string? At { get; init; }
    public string? RequestId { get; init; }
    public bool? Acknowledged { get; init; }
    public bool? Announced { get; init; }
}

public sealed class ReplyRowJson
{
    public string? Id { get; init; }
    public string? At { get; init; }
    public string? Body { get; init; }
    public string? Cursor { get; init; }
}

public sealed class OmittedJson
{
    public string? Surface { get; init; }
    public string? Reveal { get; init; }
}

public sealed class ReadyJson
{
    public string? Schema { get; init; }
    public string? ObservedAt { get; init; }
    public ReadyLockJson? Lock { get; init; }
    public ReadyWakeConsumerJson? WakeConsumer { get; init; }
    public ReadyPostureJson? Posture { get; init; }

    /// <summary><c>true</c>, <c>false</c> or the string <c>"unknown"</c>.</summary>
    public JsonElement CanReceive { get; init; }
}

public sealed class ReadyLockJson
{
    public string? State { get; init; }
}

public sealed class ReadyWakeConsumerJson
{
    public string? State { get; init; }
}

public sealed class ReadyPostureJson
{
    public string? State { get; init; }
}

/// <summary>A decision waiting on the user, from <c>fm-bearings.v1</c>.</summary>
public sealed class BearingsDecisionJson
{
    public string? Summary { get; init; }
}

/// <summary>Work in flight, from <c>fm-bearings.v1</c>. A <c>done</c> row of kind <c>scout</c> is finished research.</summary>
public sealed class BearingsWorkJson
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? State { get; init; }
    public string? Doing { get; init; }
    public string? Repo { get; init; }
    public string? Kind { get; init; }
}

/// <summary>A PR FirstMate recorded for a task, from <c>fm-bearings.v1</c>. <c>Id</c> is the task id.</summary>
public sealed class RecordedPrJson
{
    public string? Id { get; init; }
    public string? Url { get; init; }
}

public sealed class LandedJson
{
    public string? What { get; init; }
    public string? Artifact { get; init; }
}

public sealed class GateJson
{
    public string? Title { get; init; }
    public string? BlockedBy { get; init; }
    public string? Reason { get; init; }
}

public sealed class BearingsJson
{
    public string? Schema { get; init; }
    public List<BearingsDecisionJson>? DecisionsOpen { get; init; }
    public List<BearingsWorkJson>? InFlight { get; init; }
    public List<RecordedPrJson>? RecordedPrs { get; init; }
    public List<LandedJson>? Landed { get; init; }
    public List<GateJson>? Gates { get; init; }
}

public sealed class FleetTaskStateJson
{
    public string? State { get; init; }
}

public sealed class FleetTaskBacklogJson
{
    public string? Id { get; init; }
    public string? Title { get; init; }
}

/// <summary>A worker task, from <c>fm-fleet-snapshot.v1</c>. A task of kind <c>secondmate</c> is not the user's own work.</summary>
public sealed class FleetTaskJson
{
    public string? Id { get; init; }
    public string? Project { get; init; }
    public string? Kind { get; init; }
    public FleetTaskBacklogJson? Backlog { get; init; }
    public FleetTaskStateJson? CurrentState { get; init; }
}

public sealed class FleetCompletionJson
{
    public string? Date { get; init; }
}

public sealed class FleetRecordJson
{
    public bool? Structured { get; init; }
    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? State { get; init; }
    public bool? CaptainActionable { get; init; }
    public string? HoldReason { get; init; }
    public List<string>? UnresolvedBlockerIds { get; init; }
    public FleetCompletionJson? Completion { get; init; }
}

public sealed class FleetBacklogJson
{
    public List<FleetRecordJson>? Records { get; init; }
}

public sealed class FleetSnapshotJson
{
    public string? Schema { get; init; }
    public List<FleetTaskJson>? Tasks { get; init; }
    public FleetBacklogJson? Backlog { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(NoteJson))]
[JsonSerializable(typeof(ReceiptsJson))]
[JsonSerializable(typeof(ReadyJson))]
[JsonSerializable(typeof(BearingsJson))]
[JsonSerializable(typeof(FleetSnapshotJson))]
internal sealed partial class FirstMateJsonContext : JsonSerializerContext;

// quota-axi is a separate tool with its own camelCase JSON, so it gets its own context.

public sealed class QuotaAvailabilityJson
{
    public string? Scope { get; init; }
    public string? Status { get; init; }
    public double? EffectivePercentRemaining { get; init; }
    public QuotaRunwayJson? Runway { get; init; }
    public List<string>? LimitingWindowIds { get; init; }
}

public sealed class QuotaRunwayJson
{
    public string? Status { get; init; }
}

public sealed class QuotaSemanticsJson
{
    public string? Status { get; init; }
    public List<QuotaAvailabilityJson>? EffectiveAvailability { get; init; }
}

public sealed class QuotaStateJson
{
    public string? Status { get; init; }
}

public sealed class QuotaWindowJson
{
    public string? Id { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
}

public sealed class QuotaProviderJson
{
    public string? Name { get; init; }
    public QuotaStateJson? State { get; init; }
    public QuotaSemanticsJson? QuotaSemantics { get; init; }
    public List<QuotaWindowJson>? Windows { get; init; }
}

public sealed class QuotaJson
{
    public int? SchemaVersion { get; init; }
    public List<QuotaProviderJson>? Providers { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(QuotaJson))]
internal sealed partial class QuotaJsonContext : JsonSerializerContext;
