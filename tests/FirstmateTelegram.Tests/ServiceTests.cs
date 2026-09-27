using System.Diagnostics;
using FirstmateTelegram.Configuration;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.Service;
using FirstmateTelegram.Tests.Fakes;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

public sealed class ServiceTests
{
    [Fact]
    public void The_plist_restarts_after_a_crash_only_and_carries_an_explicit_environment()
    {
        var plist = LaunchAgentPlist.Build(new LaunchAgentSettings(
            BridgePaths.ServiceLabel,
            "/Users/me/.local/share/firstmate-telegram/app/firstmate-telegram",
            new Dictionary<string, string> { ["PATH"] = "/opt/homebrew/bin:/usr/bin:/bin", ["FM_HOME"] = "/Users/me/fm & co", ["LANG"] = "en_US.UTF-8" },
            "/Users/me/Library/Logs/firstmate-telegram/launchd.out.log",
            "/Users/me/Library/Logs/firstmate-telegram/launchd.err.log"));

        Assert.Contains("<key>Label</key><string>io.github.lbildzinkas.firstmate-telegram</string>", plist);
        Assert.Contains("<string>/Users/me/.local/share/firstmate-telegram/app/firstmate-telegram</string>\n    <string>run</string>", plist);
        Assert.Contains("<key>RunAtLoad</key><true/>", plist);
        Assert.Contains("<key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>", plist);
        Assert.Contains("<key>ThrottleInterval</key><integer>10</integer>", plist);
        Assert.Contains("<key>FM_HOME</key><string>/Users/me/fm &amp; co</string>", plist);
        Assert.Contains("<key>LANG</key><string>en_US.UTF-8</string>", plist);
        Assert.Contains("<key>StandardErrorPath</key><string>/Users/me/Library/Logs/firstmate-telegram/launchd.err.log</string>", plist);
    }

    [Fact]
    public async Task The_plist_passes_plutil_lint()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("agent.plist");
        await File.WriteAllTextAsync(path, LaunchAgentPlist.Build(new LaunchAgentSettings(
            BridgePaths.ServiceLabel,
            "/Users/me/app/firstmate-telegram",
            new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["FM_HOME"] = "/Users/me/<firstmate> \"home\"" },
            "/tmp/out.log",
            "/tmp/err.log")));

        if (!File.Exists("/usr/bin/plutil"))
            return;
        using var plutil = Process.Start(new ProcessStartInfo("/usr/bin/plutil", ["-lint", path]) { RedirectStandardOutput = true })!;
        var output = await plutil.StandardOutput.ReadToEndAsync();
        await plutil.WaitForExitAsync();

        Assert.Equal(0, plutil.ExitCode);
        Assert.EndsWith("OK\n", output);
    }

    [Fact]
    public void Path_is_built_from_where_the_tools_resolve_then_the_system_folders()
    {
        using var directory = new TempDirectory();
        var first = Executable(directory, "shells", "bash");
        Executable(directory, "brew", "python3");
        Executable(directory, "brew", "jq");
        Executable(directory, "dotnet-home", "dotnet");
        Directory.CreateDirectory(directory.Combine("empty"));
        var path = string.Join(':', directory.Combine("empty"), Path.GetDirectoryName(first), directory.Combine("brew"), directory.Combine("dotnet-home"), "/usr/bin");
        var tools = new ToolLocator(name => name == "PATH" ? path : null);

        Assert.Equal(
            string.Join(':', directory.Combine("shells"), directory.Combine("brew"), directory.Combine("dotnet-home"), "/usr/bin", "/bin", "/usr/sbin", "/sbin"),
            tools.BuildPath());
    }

    [Fact]
    public void Dotnet_root_is_set_only_outside_the_default_location()
    {
        using var directory = new TempDirectory();
        var dotnet = Executable(directory, "cellar", "dotnet");
        Directory.CreateDirectory(directory.Combine("bin"));
        File.CreateSymbolicLink(directory.Combine("bin", "dotnet"), dotnet);

        Assert.Equal(directory.Combine("cellar"), new ToolLocator(name => name == "PATH" ? directory.Combine("bin") : null).DotnetRoot());
        Assert.Equal("/custom/root", new ToolLocator(name => name == "DOTNET_ROOT" ? "/custom/root" : null).DotnetRoot());
        Assert.Null(new ToolLocator(name => name == "DOTNET_ROOT" ? ToolLocator.DefaultDotnetRoot : null).DotnetRoot());
    }

    [Fact]
    public async Task Install_writes_the_plist_without_the_token_and_reloads_the_agent_with_bootout_then_bootstrap()
    {
        using var directory = new TempDirectory();
        var paths = BridgePaths.FromEnvironment(name => name == "HOME" ? directory.Path : null);
        Directory.CreateDirectory(paths.AppDirectory);
        await File.WriteAllTextAsync(paths.InstalledProgram, "");
        TokenFile.Write(paths, FakeTelegramServer.Token);
        var launchd = new FakeLaunchd { IsLoaded = true };
        var tools = new ToolLocator(name => name switch { "PATH" => "/usr/bin:/bin", "DOTNET_ROOT" => "/opt/homebrew/opt/dotnet/libexec", _ => null });
        var installer = new LaunchdServiceInstaller(paths, launchd.Runner, tools);

        await installer.InstallAsync("/Users/me/firstmate", CancellationToken.None);

        var target = "gui/501/io.github.lbildzinkas.firstmate-telegram";
        Assert.Equal(
            [
                $"/bin/launchctl print {target}",
                $"/bin/launchctl bootout {target}",
                // After bootout, wait for print to report the job gone before bootstrapping.
                $"/bin/launchctl print {target}",
                $"/bin/launchctl bootstrap gui/501 {paths.LaunchAgentPlist}",
                // After bootstrap, verify the job is loaded.
                $"/bin/launchctl print {target}",
            ],
            launchd.Runner.CommandLines().Where(line => line.StartsWith("/bin/launchctl", StringComparison.Ordinal)));
        var plist = await File.ReadAllTextAsync(paths.LaunchAgentPlist);
        Assert.Contains("<key>FM_HOME</key><string>/Users/me/firstmate</string>", plist);
        Assert.Contains("<key>DOTNET_ROOT</key><string>/opt/homebrew/opt/dotnet/libexec</string>", plist);
        Assert.Contains($"<string>{paths.InstalledProgram}</string>", plist);
        Assert.DoesNotContain(FakeTelegramServer.Token, plist);
        Assert.DoesNotContain(FakeTelegramServer.Token.Split(':')[1], plist);
        Assert.DoesNotContain("token", plist, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            File.GetUnixFileMode(paths.LaunchAgentPlist));
        Assert.True(Directory.Exists(paths.LogDirectory));
    }

    [Fact]
    public async Task Install_refuses_before_the_app_is_installed()
    {
        using var directory = new TempDirectory();
        var paths = BridgePaths.FromEnvironment(name => name == "HOME" ? directory.Path : null);
        var launchd = new FakeLaunchd();

        var error = await Assert.ThrowsAsync<BridgeException>(() => new LaunchdServiceInstaller(paths, launchd.Runner, new ToolLocator(_ => null)).InstallAsync("/h", CancellationToken.None));

        Assert.Contains("install.sh", error.Message);
        Assert.Empty(launchd.Runner.Requests);
    }

    [Fact]
    public async Task Install_retries_bootstrap_while_launchd_is_still_removing_the_old_job()
    {
        using var directory = new TempDirectory();
        var paths = BridgePaths.FromEnvironment(name => name == "HOME" ? directory.Path : null);
        Directory.CreateDirectory(paths.AppDirectory);
        await File.WriteAllTextAsync(paths.InstalledProgram, "");
        // The first bootstrap after the upgrade hits the teardown race; the second succeeds.
        var launchd = new FakeLaunchd { IsLoaded = true, BootstrapError5s = 1 };
        var installer = FastInstaller(paths, launchd);

        await installer.InstallAsync("/Users/me/firstmate", CancellationToken.None);

        var commands = launchd.Runner.CommandLines()
            .Where(line => line.StartsWith("/bin/launchctl", StringComparison.Ordinal))
            .Select(line => line["/bin/launchctl ".Length..])
            .ToList();
        Assert.Equal(
            [$"bootstrap gui/501 {paths.LaunchAgentPlist}", $"bootstrap gui/501 {paths.LaunchAgentPlist}"],
            commands.Where(line => line.StartsWith("bootstrap ", StringComparison.Ordinal)));
        Assert.True(await installer.IsLoadedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Install_waits_for_launchd_to_finish_removing_the_old_job_before_bootstrapping()
    {
        using var directory = new TempDirectory();
        var paths = BridgePaths.FromEnvironment(name => name == "HOME" ? directory.Path : null);
        Directory.CreateDirectory(paths.AppDirectory);
        await File.WriteAllTextAsync(paths.InstalledProgram, "");
        // bootout returns while print still reports the job for two more polls.
        var launchd = new FakeLaunchd { IsLoaded = true, PrintsStillLoadedAfterBootout = 2 };
        var installer = FastInstaller(paths, launchd);

        await installer.InstallAsync("/Users/me/firstmate", CancellationToken.None);

        var target = "gui/501/io.github.lbildzinkas.firstmate-telegram";
        Assert.Equal(
            [
                $"print {target}",
                $"bootout {target}",
                $"print {target}",
                $"print {target}",
                $"print {target}",
                $"bootstrap gui/501 {paths.LaunchAgentPlist}",
                $"print {target}",
            ],
            launchd.Runner.CommandLines()
                .Where(line => line.StartsWith("/bin/launchctl", StringComparison.Ordinal))
                .Select(line => line["/bin/launchctl ".Length..]));
    }

    [Fact]
    public async Task A_bootstrap_error_5_that_never_clears_is_reported_with_the_fix()
    {
        using var directory = new TempDirectory();
        var paths = BridgePaths.FromEnvironment(name => name == "HOME" ? directory.Path : null);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LaunchAgentPlist)!);
        await File.WriteAllTextAsync(paths.LaunchAgentPlist, "");
        var launchd = new FakeLaunchd { FailBootstrap = true };
        var installer = FastInstaller(paths, launchd);

        var error = await Assert.ThrowsAsync<BridgeException>(() => installer.StartAsync(CancellationToken.None));

        Assert.Contains("launchctl bootstrap could not load the login agent (3 attempts): Bootstrap failed: 5: Input/output error", error.Message);
        Assert.Contains("run `firstmate-telegram start` to load it, then `firstmate-telegram doctor` to check it", error.Message);
        Assert.Equal(3, launchd.Runner.CommandLines().Count(line => line.Contains("/bin/launchctl bootstrap ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_bootstrap_that_never_loads_the_job_is_reported_with_the_fix()
    {
        using var directory = new TempDirectory();
        var paths = BridgePaths.FromEnvironment(name => name == "HOME" ? directory.Path : null);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LaunchAgentPlist)!);
        await File.WriteAllTextAsync(paths.LaunchAgentPlist, "");
        var launchd = new FakeLaunchd { BootstrapDoesNotLoad = true };
        var installer = FastInstaller(paths, launchd);

        var error = await Assert.ThrowsAsync<BridgeException>(() => installer.StartAsync(CancellationToken.None));

        Assert.Contains("bootstrap reported success but the login agent is not loaded in launchd", error.Message);
        Assert.Contains("run `firstmate-telegram start` to load it, then `firstmate-telegram doctor` to check it", error.Message);
    }

    [Fact]
    public async Task Start_bootstraps_then_kickstarts_and_stop_boots_out()
    {
        using var directory = new TempDirectory();
        var paths = BridgePaths.FromEnvironment(name => name == "HOME" ? directory.Path : null);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LaunchAgentPlist)!);
        await File.WriteAllTextAsync(paths.LaunchAgentPlist, "");
        var launchd = new FakeLaunchd();
        var installer = FastInstaller(paths, launchd);

        await installer.StartAsync(CancellationToken.None);
        await installer.StopAsync(CancellationToken.None);
        await installer.StopAsync(CancellationToken.None);

        var target = "gui/501/io.github.lbildzinkas.firstmate-telegram";
        Assert.Equal(
            [
                $"print {target}",
                $"bootstrap gui/501 {paths.LaunchAgentPlist}",
                $"print {target}",
                $"kickstart {target}",
                $"print {target}",
                $"bootout {target}",
                $"print {target}",
                $"print {target}",
            ],
            launchd.Runner.CommandLines()
                .Where(line => line.StartsWith("/bin/launchctl", StringComparison.Ordinal))
                .Select(line => line["/bin/launchctl ".Length..]));
    }

    static LaunchdServiceInstaller FastInstaller(BridgePaths paths, FakeLaunchd launchd) =>
        new(paths, launchd.Runner, new ToolLocator(_ => null))
        {
            ReloadWait = TimeSpan.FromMilliseconds(200),
            ReloadPollInterval = TimeSpan.FromMilliseconds(1),
        };

    static string Executable(TempDirectory directory, string folder, string name)
    {
        Directory.CreateDirectory(directory.Combine(folder));
        var path = directory.Combine(folder, name);
        File.WriteAllText(path, "#!/bin/sh\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>Answers <c>id -u</c> and <c>launchctl</c> like a real user session, tracking whether the agent is loaded.</summary>
    sealed class FakeLaunchd
    {
        public FakeLaunchd() => Runner = new ScriptedProcessRunner(Answer);

        public ScriptedProcessRunner Runner { get; }

        public bool IsLoaded { get; set; }

        /// <summary>Every bootstrap fails with the error-5 teardown race.</summary>
        public bool FailBootstrap { get; init; }

        /// <summary>How many bootstraps fail with error 5 before one succeeds.</summary>
        public int BootstrapError5s { get; set; }

        /// <summary>How many prints after a bootout still report the job, as launchd finishes removing it.</summary>
        public int PrintsStillLoadedAfterBootout { get; init; }

        /// <summary>bootstrap exits 0 without loading the job, so the load never verifies.</summary>
        public bool BootstrapDoesNotLoad { get; init; }

        int printsStillLoaded;

        ProcessResult Answer(ProcessRequest request)
        {
            if (request.FileName == "/usr/bin/id")
                return new ProcessResult(0, "501\n", "");

            switch (request.Arguments[0])
            {
                case "print" when printsStillLoaded > 0:
                    printsStillLoaded--;
                    return new ProcessResult(0, "", "");
                case "print":
                    return new ProcessResult(IsLoaded ? 0 : 113, "", IsLoaded ? "" : "Could not find service");
                case "bootstrap" when BootstrapError5s > 0:
                    BootstrapError5s--;
                    return new ProcessResult(5, "", "Bootstrap failed: 5: Input/output error\n");
                case "bootstrap" when FailBootstrap:
                    return new ProcessResult(5, "", "Bootstrap failed: 5: Input/output error\n");
                case "bootstrap":
                    if (!BootstrapDoesNotLoad)
                        IsLoaded = true;
                    return new ProcessResult(0, "", "");
                case "bootout":
                    IsLoaded = false;
                    printsStillLoaded = PrintsStillLoadedAfterBootout;
                    return new ProcessResult(0, "", "");
                default:
                    return new ProcessResult(0, "", "");
            }
        }
    }
}
