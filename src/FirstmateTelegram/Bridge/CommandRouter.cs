using FirstmateTelegram.Telegram;
using Telegram.Bot.Types;

namespace FirstmateTelegram.Bridge;

public sealed record ChatCommand(string Name, string Arguments);

public static class CommandParser
{
    /// <summary>
    /// Reads a leading <c>/command</c>, case-insensitively, also in Telegram's <c>/command@BotName</c> form for this bot.
    /// Returns null for anything that is not a command addressed to this bot.
    /// </summary>
    public static ChatCommand? Parse(string text, string? botUsername)
    {
        if (!text.StartsWith('/'))
            return null;

        var end = 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;

        var word = text[1..end];
        var at = word.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            var mention = word[(at + 1)..];
            if (botUsername is null || !mention.Equals(botUsername, StringComparison.OrdinalIgnoreCase))
                return null;
            word = word[..at];
        }

        if (word.Length == 0)
            return null;
        return new ChatCommand(word.ToLowerInvariant(), text[end..].Trim());
    }
}

/// <summary>
/// The bridge's own commands. This version answers each of them with "not available yet"; anything else,
/// including an unknown <c>/word</c>, is a request for FirstMate.
/// </summary>
public sealed class CommandRouter
{
    static readonly HashSet<string> LaterCommands = new(StringComparer.Ordinal) { "status", "ping", "mute", "unmute", "back", "stop", "help" };

    readonly TelegramGateway _telegram;

    public CommandRouter(TelegramGateway telegram) => _telegram = telegram;

    /// <summary>Answers <paramref name="message"/> when it is one of the bridge's commands; returns false for a request.</summary>
    public async Task<bool> TryAnswerAsync(Message message, string text, string? botUsername, CancellationToken cancellationToken)
    {
        var command = CommandParser.Parse(text, botUsername);
        if (command is null || !LaterCommands.Contains(command.Name))
            return false;

        await _telegram.TrySendTextAsync(message.Chat.Id, BridgeTexts.NotAvailableYet(command.Name), message.Id, cancellationToken);
        return true;
    }
}
