using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.Telegram;

namespace FirstmateTelegram.Configuration;

/// <summary>Reads, validates and writes <c>config.json</c>. An invalid file is refused with a message that names the problem.</summary>
public static partial class ConfigFile
{
    static readonly HashSet<string> KnownKeys =
    [
        "schema", "firstmate_home", "allowed_user_id", "deny_list", "quota_provider",
        "live_ping_timeout_seconds", "unresponsive_after_minutes", "alert_settle_minutes", "replied_reaction",
    ];

    public static bool Exists(BridgePaths paths) => File.Exists(paths.ConfigFile);

    public static BridgeConfig Load(BridgePaths paths)
    {
        var path = paths.ConfigFile;
        if (!File.Exists(path))
            throw new BridgeException($"No configuration at {path}. Run `firstmate-telegram setup` first.");

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (BridgeException exception)
        {
            throw new BridgeException($"The configuration at {path} is invalid: {exception.Message}", exception);
        }
    }

    public static BridgeConfig Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new BridgeException("it is not valid JSON.", exception);
        }

        if (root is not JsonObject document)
            throw new BridgeException("it must be a JSON object.");

        var unknown = document.Select(member => member.Key).Where(key => !KnownKeys.Contains(key)).ToList();
        if (unknown.Count > 0)
            throw new BridgeException($"unknown key {string.Join(", ", unknown.Select(key => $"\"{key}\""))}.");

        var schema = ReadString(document, "schema", required: true);
        if (schema != BridgeConfig.SchemaName)
            throw new BridgeException($"\"schema\" must be \"{BridgeConfig.SchemaName}\".");

        var home = ReadString(document, "firstmate_home", required: true)!;
        if (!Path.IsPathRooted(home))
            throw new BridgeException("\"firstmate_home\" must be an absolute path.");

        var allowedUserId = ReadLong(document, "allowed_user_id", required: true)!.Value;
        if (allowedUserId <= 0)
            throw new BridgeException("\"allowed_user_id\" must be a positive Telegram user id.");

        var defaults = new BridgeConfig { FirstmateHome = home, AllowedUserId = allowedUserId };
        var config = defaults with
        {
            DenyList = ReadStringList(document, "deny_list") ?? defaults.DenyList,
            QuotaProvider = ReadString(document, "quota_provider", required: false) ?? defaults.QuotaProvider,
            LivePingTimeoutSeconds = ReadPositiveInt(document, "live_ping_timeout_seconds") ?? defaults.LivePingTimeoutSeconds,
            UnresponsiveAfterMinutes = ReadPositiveInt(document, "unresponsive_after_minutes") ?? defaults.UnresponsiveAfterMinutes,
            AlertSettleMinutes = ReadPositiveInt(document, "alert_settle_minutes") ?? defaults.AlertSettleMinutes,
            RepliedReaction = ReadString(document, "replied_reaction", required: false) ?? defaults.RepliedReaction,
        };

        if (!BotReactions.Allowed.Contains(config.RepliedReaction))
            throw new BridgeException($"\"replied_reaction\" must be one of Telegram's bot reaction emoji: {string.Join(" ", BotReactions.Allowed)}");

        // This version cannot yet hide private projects, so it refuses a deny list rather than leak one.
        if (config.DenyList.Count > 0)
            throw new BridgeException("\"deny_list\" is not supported by this version of firstmate-telegram yet; leave it empty ([]).");

        return config;
    }

    public static void Write(BridgePaths paths, BridgeConfig config)
    {
        PrivateFiles.EnsureDirectory(paths.ConfigDirectory);
        PrivateFiles.WriteAtomic(paths.ConfigFile, Serialize(config));
    }

    public static string Serialize(BridgeConfig config)
    {
        var document = new JsonObject
        {
            ["schema"] = BridgeConfig.SchemaName,
            ["firstmate_home"] = config.FirstmateHome,
            ["allowed_user_id"] = config.AllowedUserId,
            ["deny_list"] = new JsonArray(config.DenyList.Select(entry => (JsonNode?)JsonValue.Create(entry)).ToArray()),
            ["quota_provider"] = config.QuotaProvider,
            ["live_ping_timeout_seconds"] = config.LivePingTimeoutSeconds,
            ["unresponsive_after_minutes"] = config.UnresponsiveAfterMinutes,
            ["alert_settle_minutes"] = config.AlertSettleMinutes,
            ["replied_reaction"] = config.RepliedReaction,
        };
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return EscapedSurrogatePair().Replace(document.ToJsonString(options), UnescapeSurrogatePair) + "\n";
    }

    // System.Text.Json always escapes characters outside the Basic Multilingual Plane; write emoji as themselves so the file stays readable.
    static string UnescapeSurrogatePair(Match match) =>
        new(
        [
            (char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            (char)int.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        ]);

    [GeneratedRegex(@"\\u(D[89AB][0-9A-F]{2})\\u(D[C-F][0-9A-F]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex EscapedSurrogatePair();

    static string? ReadString(JsonObject document, string key, bool required)
    {
        if (!document.TryGetPropertyValue(key, out var node) || node is null)
            return required ? throw new BridgeException($"\"{key}\" is required.") : null;
        if (node.GetValueKind() != JsonValueKind.String)
            throw new BridgeException($"\"{key}\" must be a string.");

        var value = node.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
            throw new BridgeException($"\"{key}\" must not be empty.");
        return value;
    }

    static long? ReadLong(JsonObject document, string key, bool required)
    {
        if (!document.TryGetPropertyValue(key, out var node) || node is null)
            return required ? throw new BridgeException($"\"{key}\" is required.") : null;
        if (node.GetValueKind() != JsonValueKind.Number || node is not JsonValue value || !value.TryGetValue<long>(out var number))
            throw new BridgeException($"\"{key}\" must be a whole number.");
        return number;
    }

    static int? ReadPositiveInt(JsonObject document, string key)
    {
        var value = ReadLong(document, key, required: false);
        if (value is null)
            return null;
        if (value <= 0 || value > int.MaxValue)
            throw new BridgeException($"\"{key}\" must be a positive whole number.");
        return (int)value.Value;
    }

    static List<string>? ReadStringList(JsonObject document, string key)
    {
        if (!document.TryGetPropertyValue(key, out var node) || node is null)
            return null;
        if (node is not JsonArray array)
            throw new BridgeException($"\"{key}\" must be a list of strings.");

        var entries = new List<string>();
        foreach (var entry in array)
        {
            if (entry is null || entry.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetValue<string>()))
                throw new BridgeException($"\"{key}\" must be a list of non-empty strings.");
            entries.Add(entry.GetValue<string>());
        }

        return entries;
    }
}
