using System.Text.Json.Serialization;

namespace FirstmateTelegram.State;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(BridgeState))]
[JsonSerializable(typeof(RequestsDocument))]
[JsonSerializable(typeof(AlertsDocument))]
internal sealed partial class StateJsonContext : JsonSerializerContext;
