using System.Diagnostics;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

/// <summary>Runs install.sh only with --dry-run and a throwaway HOME, so nothing on this Mac changes.</summary>
public sealed class InstallScriptTests
{
    [Fact]
    public async Task A_first_install_builds_links_runs_setup_then_installs_the_agent_and_runs_doctor()
    {
        using var home = new TempDirectory();

        var (exitCode, output) = await RunAsync(home, "--dry-run");

        Assert.Equal(0, exitCode);
        var program = home.Combine(".local", "share", "firstmate-telegram", "app", "firstmate-telegram");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, line => Assert.StartsWith("would run: ", line));
        var steps = lines.Select(line => line["would run: ".Length..]).ToList();
        Assert.Contains(steps, step => step.StartsWith("dotnet publish ", StringComparison.Ordinal)
            && step.Contains("--runtime osx-", StringComparison.Ordinal)
            && step.Contains("--self-contained false", StringComparison.Ordinal));
        Assert.Contains($"ln -sfn {program} {home.Combine(".local", "bin", "firstmate-telegram")}", steps);
        Assert.Equal([$"{program} setup", $"{program} service install", $"{program} doctor"], steps.TakeLast(3));
        Assert.Empty(Directory.EnumerateFileSystemEntries(home.Path));
    }

    [Fact]
    public async Task An_upgrade_keeps_the_configuration_and_skips_setup()
    {
        using var home = new TempDirectory();
        Directory.CreateDirectory(home.Combine(".config", "firstmate-telegram"));
        await File.WriteAllTextAsync(home.Combine(".config", "firstmate-telegram", "config.json"), "{}");
        Directory.CreateDirectory(home.Combine(".local", "share", "firstmate-telegram", "app"));

        var (exitCode, output) = await RunAsync(home, "--dry-run");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(" setup\n", output);
        Assert.Contains("mv " + home.Combine(".local", "share", "firstmate-telegram", "app") + " ", output);
        Assert.Contains(" service install\n", output);
    }

    [Fact]
    public async Task Uninstall_keeps_configuration_and_state_unless_purge_is_given()
    {
        using var home = new TempDirectory();

        var (keepExit, keep) = await RunAsync(home, "--uninstall", "--dry-run");
        var (purgeExit, purge) = await RunAsync(home, "--uninstall", "--purge", "--dry-run");

        Assert.Equal((0, 0), (keepExit, purgeExit));
        Assert.Contains("would run: rm -rf " + home.Combine(".local", "share", "firstmate-telegram"), keep);
        Assert.DoesNotContain(".config", keep.Split('\n').Where(line => line.StartsWith("would run", StringComparison.Ordinal)).Aggregate("", string.Concat));
        Assert.Contains(home.Combine(".config", "firstmate-telegram"), purge);
        Assert.Contains(home.Combine(".local", "state", "firstmate-telegram"), purge);
        Assert.Contains(home.Combine("Library", "Logs", "firstmate-telegram"), purge);
    }

    [Theory]
    [InlineData("--purge")]
    [InlineData("--bogus")]
    public async Task Bad_options_are_refused_before_anything_happens(string option)
    {
        using var home = new TempDirectory();

        var (exitCode, _) = await RunAsync(home, option, "--dry-run");

        Assert.NotEqual(0, exitCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(home.Path));
    }

    static async Task<(int ExitCode, string Output)> RunAsync(TempDirectory home, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(RepositoryRoot(), "install.sh"), arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["HOME"] = home.Path;
        start.Environment.Remove("XDG_CONFIG_HOME");
        start.Environment.Remove("XDG_STATE_HOME");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "install.sh")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("install.sh not found above the test output folder");
    }
}
