using System.Text.Json.Serialization;

namespace FirstmateTelegram.State;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(BridgeState))]
[JsonSerializable(typeof(RequestsDocument))]
internal sealed partial class StateJsonContext : JsonSerializerContext;
