using FirstmateTelegram.Logging;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

public sealed class LoggingTests
{
    public static TheoryData<string> TokenShapedLines()
        => new(
            "POST https://api.telegram.org/bot" + FakeTokens.Telegram() + "/getUpdates failed",
            "token " + FakeTokens.Telegram(7012345678, "AAE_", "abcdefghijklmnopqrstuvwxyz012345") + " in text");

    [Theory]
    [MemberData(nameof(TokenShapedLines))]
    public void Anything_shaped_like_a_bot_token_is_masked(string line)
    {
        var masked = TokenMask.Apply(line);

        Assert.Contains(TokenMask.Replacement, masked);
        Assert.DoesNotMatch("[0-9]{3,}:[A-Za-z0-9_-]{20,}", masked);
    }

    [Fact]
    public void Ids_and_times_are_left_alone()
    {
        const string line = "2026-09-26T14:05:00.000Z info UpdatePoller: saved message 17 as note 1790448791-f1CqDe (created)";

        Assert.Equal(line, TokenMask.Apply(line));
    }

    [Fact]
    public void Every_line_written_to_the_log_file_is_masked_and_the_file_is_private()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("logs", "bridge.log");
        using (var log = new BridgeLogFile(path))
            log.Write("failed at https://api.telegram.org/bot" + FakeTokens.Telegram() + "/sendMessage");

        Assert.DoesNotContain(FakeTokens.Telegram(), File.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void The_log_rotates_at_its_size_limit_and_keeps_three_files()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("bridge.log");
        using (var log = new BridgeLogFile(path, maxBytes: 1000))
        {
            for (var line = 0; line < 100; line++)
                log.Write($"line {line} " + new string('x', 80));
        }

        Assert.Equal(["bridge.log", "bridge.log.1", "bridge.log.2"], Directory.GetFiles(directory.Path).Select(Path.GetFileName).Order());
        Assert.All(Directory.GetFiles(directory.Path), file => Assert.True(new FileInfo(file).Length <= 1000));
        Assert.Contains("line 99 ", File.ReadAllText(path));
    }
}
