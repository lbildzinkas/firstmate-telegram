using System.Globalization;
using FirstmateTelegram.Telegram;

namespace FirstmateTelegram.Tests;

public sealed class MessageSplitterTests
{
    [Fact]
    public void Text_within_the_limit_is_one_unmarked_part()
    {
        var text = new string('a', MessageSplitter.TelegramLimit);

        Assert.Equal([text], MessageSplitter.Split(text));
    }

    [Fact]
    public void Long_text_splits_at_paragraph_boundaries_first()
    {
        var paragraphs = Enumerable.Range(1, 12).Select(index => $"P{index} " + new string('p', 900)).ToList();

        var parts = MessageSplitter.Split(string.Join("\n\n", paragraphs));

        Assert.All(parts, part => Assert.True(part.Length <= MessageSplitter.TelegramLimit));
        var bodies = parts.Select(Body).ToList();
        Assert.All(bodies, body => Assert.StartsWith("P", body));
        Assert.Equal(paragraphs, bodies.SelectMany(body => body.Split("\n\n")));
    }

    [Fact]
    public void Without_paragraphs_it_splits_at_line_boundaries()
    {
        var lines = Enumerable.Range(1, 200).Select(index => $"line {index} " + new string('l', 40)).ToList();

        var parts = MessageSplitter.Split(string.Join('\n', lines));

        Assert.Equal(lines, parts.Select(Body).SelectMany(body => body.Split('\n')));
    }

    [Fact]
    public void Without_line_breaks_it_splits_between_words()
    {
        var words = Enumerable.Range(1, 2000).Select(index => $"word{index}").ToList();

        var parts = MessageSplitter.Split(string.Join(' ', words));

        Assert.True(parts.Count > 1);
        Assert.Equal(words, parts.Select(Body).SelectMany(body => body.Split(' ')));
    }

    [Fact]
    public void Numbers_the_parts_and_leaves_room_for_the_marker()
    {
        var parts = MessageSplitter.Split(new string('z', 3 * MessageSplitter.TelegramLimit));

        Assert.Equal(4, parts.Count);
        for (var index = 0; index < parts.Count; index++)
        {
            Assert.StartsWith(string.Create(CultureInfo.InvariantCulture, $"({index + 1}/4)\n"), parts[index]);
            Assert.True(parts[index].Length <= MessageSplitter.TelegramLimit);
        }

        Assert.Equal(3 * MessageSplitter.TelegramLimit, parts.Sum(part => Body(part).Length));
    }

    [Fact]
    public void Two_digit_part_counts_still_fit()
    {
        var parts = MessageSplitter.Split(new string('z', 12 * MessageSplitter.TelegramLimit));

        Assert.Equal(13, parts.Count);
        Assert.StartsWith("(13/13)\n", parts[^1]);
        Assert.All(parts, part => Assert.True(part.Length <= MessageSplitter.TelegramLimit));
    }

    [Fact]
    public void Never_splits_a_surrogate_pair()
    {
        var text = "a" + string.Concat(Enumerable.Repeat("\U0001F600", 5000));

        var parts = MessageSplitter.Split(text);

        Assert.All(parts, part => Assert.True(part.Length <= MessageSplitter.TelegramLimit));
        Assert.All(parts.Select(Body), body =>
        {
            Assert.False(char.IsLowSurrogate(body[0]));
            Assert.False(char.IsHighSurrogate(body[^1]));
        });
        Assert.Equal(text, string.Concat(parts.Select(Body)));
    }

    [Fact]
    public void Never_splits_an_emoji_sequence()
    {
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466";
        var text = string.Concat(Enumerable.Repeat(family, 1000));

        var parts = MessageSplitter.Split(text);

        Assert.All(parts.Select(Body), body => Assert.Equal(0, body.Length % family.Length));
        Assert.Equal(text, string.Concat(parts.Select(Body)));
    }

    static string Body(string part) => part[(part.IndexOf('\n', StringComparison.Ordinal) + 1)..];
}
