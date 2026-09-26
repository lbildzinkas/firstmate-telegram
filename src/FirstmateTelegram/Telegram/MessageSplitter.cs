using System.Globalization;

namespace FirstmateTelegram.Telegram;

/// <summary>
/// Splits text that is too long for one Telegram message into numbered parts, <c>(1/3)</c>, <c>(2/3)</c>, <c>(3/3)</c>,
/// breaking at paragraph or line boundaries where possible and never inside a surrogate pair or an emoji sequence.
/// </summary>
public static class MessageSplitter
{
    /// <summary>Telegram's limit, counted here in UTF-16 code units, which is never fewer than Telegram's own count.</summary>
    public const int TelegramLimit = 4096;

    public static IReadOnlyList<string> Split(string text, int limit = TelegramLimit)
    {
        if (text.Length <= limit)
            return [text];

        for (var digits = 1; ; digits++)
        {
            var parts = Chunk(text, limit - MarkerLength(digits));
            if (parts.Count.ToString(CultureInfo.InvariantCulture).Length > digits)
                continue;

            return parts
                .Select((part, index) => string.Create(CultureInfo.InvariantCulture, $"({index + 1}/{parts.Count})\n{part}"))
                .ToList();
        }
    }

    // "(" + part + "/" + count + ")" + "\n", with part and count at most `digits` long.
    static int MarkerLength(int digits) => (2 * digits) + 4;

    static List<string> Chunk(string text, int budget)
    {
        var chunks = new List<string>();
        var start = 0;
        while (text.Length - start > budget)
        {
            var (end, next) = FindBreak(text, start, budget);
            AddChunk(chunks, text[start..end]);
            start = next;
        }

        AddChunk(chunks, text[start..]);
        return chunks;
    }

    static void AddChunk(List<string> chunks, string chunk)
    {
        if (!string.IsNullOrWhiteSpace(chunk))
            chunks.Add(chunk);
    }

    /// <summary>Where the chunk starting at <paramref name="start"/> ends, and where the next one begins.</summary>
    static (int End, int Next) FindBreak(string text, int start, int budget)
    {
        var windowEnd = start + budget;
        var halfway = start + (budget / 2);

        foreach (var minimum in new[] { halfway, start + 1 })
        {
            var paragraph = text.LastIndexOf("\n\n", windowEnd - 1, budget, StringComparison.Ordinal);
            if (paragraph >= minimum)
                return (paragraph, SkipNewlines(text, paragraph));

            var line = text.LastIndexOf('\n', windowEnd - 1, budget);
            if (line >= minimum)
                return (line, line + 1);

            var space = text.LastIndexOf(' ', windowEnd - 1, budget);
            if (space >= minimum)
                return (space, space + 1);
        }

        var boundary = LastTextElementBoundary(text, start, windowEnd);
        return (boundary, boundary);
    }

    static int SkipNewlines(string text, int index)
    {
        while (index < text.Length && text[index] == '\n')
            index++;
        return index;
    }

    static int LastTextElementBoundary(string text, int start, int windowEnd)
    {
        var boundary = start;
        var index = start;
        while (index < text.Length)
        {
            var length = StringInfo.GetNextTextElementLength(text, index);
            if (index + length > windowEnd)
                break;
            index += length;
            boundary = index;
        }

        if (boundary > start)
            return boundary;

        // A single grapheme longer than the budget: cut it, but never between the halves of a surrogate pair.
        var cut = windowEnd;
        if (char.IsLowSurrogate(text[cut]) && char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return cut;
    }
}
