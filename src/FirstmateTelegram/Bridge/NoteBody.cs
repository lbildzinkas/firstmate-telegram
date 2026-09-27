using System.Globalization;
using System.Text;

namespace FirstmateTelegram.Bridge;

/// <summary>A quoted alert, when the request is the user's reply to one: the local time it was sent and its text.</summary>
public sealed record AlertQuote(string SentAt, string Text);

/// <summary>
/// The inbox note for a request: the user's text first, because FirstMate's wake line shows the start of the body,
/// then a footer that says where the request came from and what a Telegram request may not approve.
/// </summary>
public static class NoteBody
{
    public const string FooterHeading = "-- sent from Telegram through firstmate-telegram --";

    public static string Build(string text, bool isAway, AlertQuote? replyToAlert = null)
    {
        var body = new StringBuilder(text);
        body.Append("\n\n").Append(FooterHeading).Append('\n');
        body.Append("The user reads your answer on their phone: publish it as the inbox reply to this note (bin/fm-inbox.sh reply).\n");
        body.Append("A Telegram request can ask questions and start or steer work. It cannot approve a merge, a deletion, or any irreversible or security-sensitive action; for those, ask the user to confirm at the terminal.\n");
        if (isAway)
            body.Append("The user is still away: this request does not end away mode.\n");
        if (replyToAlert is { } alert)
            body.Append($"In reply to the alert sent at {alert.SentAt}: \"{alert.Text}\"\n");
        return body.ToString();
    }
}

/// <summary>Request ids come from the Telegram message id, which a chat never reuses, and the bot id keeps two bots apart.</summary>
public static class RequestIds
{
    public static string For(long botId, long chatId, int messageId) =>
        string.Create(CultureInfo.InvariantCulture, $"tg:{botId}:{chatId}:{messageId}");
}
