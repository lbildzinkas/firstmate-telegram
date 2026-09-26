using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Arguments_are_passed_as_a_list_and_never_interpreted_by_a_shell()
    {
        using var directory = new TempDirectory();
        var marker = directory.Combine("pwned");
        var script = WriteScript(directory, "printf '%s\\n' \"$@\"");

        var result = await new SystemProcessRunner().RunAsync(
            new ProcessRequest(script, ["two words", "$(touch " + marker + ")", "; rm -rf /", "`id`"], TimeSpan.FromSeconds(10)),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["two words", "$(touch " + marker + ")", "; rm -rf /", "`id`"], result.StandardOutput.TrimEnd('\n').Split('\n'));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Standard_input_carries_text_exactly_without_a_byte_order_mark()
    {
        using var directory = new TempDirectory();
        var script = WriteScript(directory, "cat | od -An -tx1 | tr -d ' \\n'");

        var result = await new SystemProcessRunner().RunAsync(
            new ProcessRequest(script, [], TimeSpan.FromSeconds(10)) { StandardInput = "é\U0001F44C\n" },
            CancellationToken.None);

        Assert.Equal("c3a9f09f918c0a", result.StandardOutput);
    }

    [Fact]
    public async Task Environment_variables_are_set_for_the_child()
    {
        using var directory = new TempDirectory();
        var script = WriteScript(directory, "printf '%s' \"$FM_HOME\"");

        var result = await new SystemProcessRunner().RunAsync(
            new ProcessRequest(script, [], TimeSpan.FromSeconds(10)) { Environment = new Dictionary<string, string?> { ["FM_HOME"] = "/Users/me/firstmate" } },
            CancellationToken.None);

        Assert.Equal("/Users/me/firstmate", result.StandardOutput);
    }

    [Fact]
    public async Task A_program_that_runs_too_long_is_killed_and_reported_as_a_timeout()
    {
        using var directory = new TempDirectory();
        var script = WriteScript(directory, "sleep 30");
        var started = DateTimeOffset.UtcNow;

        var result = await new SystemProcessRunner().RunAsync(new ProcessRequest(script, [], TimeSpan.FromMilliseconds(300)), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(result.Ran);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_missing_program_is_reported_not_thrown()
    {
        var result = await new SystemProcessRunner().RunAsync(new ProcessRequest("/nonexistent/fm-inbox.sh", [], TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.NotNull(result.StartFailure);
        Assert.False(result.Ran);
    }

    [Fact]
    public async Task Exit_status_and_standard_error_are_returned()
    {
        using var directory = new TempDirectory();
        var script = WriteScript(directory, "echo 'fm-inbox: broken' >&2; exit 3");

        var result = await new SystemProcessRunner().RunAsync(new ProcessRequest(script, [], TimeSpan.FromSeconds(10)), CancellationToken.None);

        Assert.Equal((3, "fm-inbox: broken\n"), (result.ExitCode, result.StandardError));
    }

    static string WriteScript(TempDirectory directory, string body)
    {
        var path = directory.Combine("script.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
