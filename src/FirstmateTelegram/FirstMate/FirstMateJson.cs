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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(NoteJson))]
[JsonSerializable(typeof(ReceiptsJson))]
[JsonSerializable(typeof(ReadyJson))]
internal sealed partial class FirstMateJsonContext : JsonSerializerContext;
