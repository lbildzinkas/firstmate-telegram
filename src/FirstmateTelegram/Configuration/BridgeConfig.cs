using FirstmateTelegram.Telegram;

namespace FirstmateTelegram.Configuration;

/// <summary>The contents of <c>config.json</c>. Only the FirstMate home and the allowed user are required.</summary>
public sealed record BridgeConfig
{
    public const string SchemaName = "firstmate-telegram.config.v1";

    public required string FirstmateHome { get; init; }
    public required long AllowedUserId { get; init; }
    public IReadOnlyList<string> DenyList { get; init; } = [];
    public string QuotaProvider { get; init; } = "claude";
    public int LivePingTimeoutSeconds { get; init; } = 60;
    public int UnresponsiveAfterMinutes { get; init; } = 15;
    public int AlertSettleMinutes { get; init; } = 15;
    public string RepliedReaction { get; init; } = BotReactions.DefaultReplied;
}
