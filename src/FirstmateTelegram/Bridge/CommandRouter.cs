using FirstmateTelegram.FirstMate;
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

/// <summary>The outcome of a bridge command. A live ping whose note could not be saved carries the failure, so the poller retries it like a request.</summary>
public sealed record CommandAnswer(CallFailure? NoteSaveFailure = null)
{
    public static CommandAnswer Handled { get; } = new();
}

/// <summary>
/// The bridge's own commands. <c>/status</c> and <c>/ping</c> are answered from records and checks alone;
/// <c>/mute</c>, <c>/unmute</c>, <c>/back</c>, <c>/stop</c> and <c>/help</c> come in a later version. Anything else,
/// including an unknown <c>/word</c>, is a request for FirstMate.
/// </summary>
public sealed class CommandRouter
{
    /// <summary>Commands the bridge answers itself, including the ones a later version adds.</summary>
    public static readonly IReadOnlySet<string> Commands = new HashSet<string>(StringComparer.Ordinal)
    {
        "status", "ping", "mute", "unmute", "back", "stop", "help",
    };

    readonly TelegramGateway _telegram;
    readonly AvailabilityReader _availability;
    readonly StatusRenderer _status;
    readonly RequestSubmitter _submitter;
    readonly TimeSpan _livePingTimeout;

    public CommandRouter(
        TelegramGateway telegram,
        AvailabilityReader availability,
        StatusRenderer status,
        RequestSubmitter submitter,
        TimeSpan livePingTimeout)
    {
        _telegram = telegram;
        _availability = availability;
        _status = status;
        _submitter = submitter;
        _livePingTimeout = livePingTimeout;
    }

    public static bool IsBridgeCommand(string name) => Commands.Contains(name);

    /// <summary>True for <c>/ping live</c>, the only command that saves a note in FirstMate's inbox and so keeps the requests' order.</summary>
    public static bool SendsANote(ChatCommand command) =>
        command.Name == "ping" && command.Arguments.Equals("live", StringComparison.OrdinalIgnoreCase);

    /// <summary>Answers <paramref name="command"/>; the poller calls this only for the bridge's own commands.</summary>
    public async Task<CommandAnswer> AnswerAsync(Message message, ChatCommand command, BotIdentity bot, CancellationToken cancellationToken)
    {
        switch (command.Name)
        {
            case "status":
                await SendAsync(message, await _status.RenderAsync(cancellationToken), cancellationToken);
                return CommandAnswer.Handled;

            case "ping":
                return await AnswerPingAsync(message, command, bot, cancellationToken);

            default:
                await SendAsync(message, BridgeTexts.NotAvailableYet(command.Name), cancellationToken);
                return CommandAnswer.Handled;
        }
    }

    async Task<CommandAnswer> AnswerPingAsync(Message message, ChatCommand command, BotIdentity bot, CancellationToken cancellationToken)
    {
        var report = await _availability.ReadAsync(cancellationToken);

        // A plain /ping is answered by the bridge alone; /ping live is a real round trip through FirstMate.
        if (!SendsANote(command))
        {
            await SendAsync(message, report.Answer(), cancellationToken);
            return CommandAnswer.Handled;
        }

        if (report.Readiness is Readiness.NotRunning or Readiness.NotPickingUp)
        {
            await SendAsync(message, report.Answer() + "\n" + BridgeTexts.NoLiveCheckSent, cancellationToken);
            return CommandAnswer.Handled;
        }

        var saved = await _submitter.SubmitLivePingAsync(message, bot, report.IsAwayOrQuiet, _livePingTimeout, cancellationToken);
        return saved.IsSaved ? CommandAnswer.Handled : new CommandAnswer(saved.Failure);
    }

    async Task SendAsync(Message message, string text, CancellationToken cancellationToken) =>
        await _telegram.TrySendTextAsync(message.Chat.Id, text, message.Id, cancellationToken);
}
