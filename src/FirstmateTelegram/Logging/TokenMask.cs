using System.Text.RegularExpressions;

namespace FirstmateTelegram.Logging;

/// <summary>
/// Masks anything shaped like a bot token, such as the token inside a Telegram request URL that an HTTP error repeats.
/// </summary>
public static partial class TokenMask
{
    public const string Replacement = "<bot-token>";

    public static string Apply(string text) => TokenPattern().Replace(text, Replacement);

    [GeneratedRegex(@"(?<![0-9])[0-9]{3,}:[A-Za-z0-9_-]{20,}")]
    private static partial Regex TokenPattern();
}
