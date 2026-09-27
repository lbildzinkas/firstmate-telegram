using System.Globalization;
using FirstmateTelegram.Configuration;
using FirstmateTelegram.Infrastructure;

namespace FirstmateTelegram.Service;

/// <summary>The login-agent layer. launchd is the only implementation; a systemd user unit would be another.</summary>
public interface IServiceInstaller
{
    /// <summary>True when the agent definition is on disk.</summary>
    bool IsInstalled { get; }

    Task<bool> IsLoadedAsync(CancellationToken cancellationToken);

    /// <summary>Writes the agent definition and (re)loads it.</summary>
    Task InstallAsync(string firstmateHome, CancellationToken cancellationToken);

    Task UninstallAsync(CancellationToken cancellationToken);

    /// <summary>Loads the agent if needed and starts it.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Unloads the agent, which stops the bridge.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class LaunchdServiceInstaller : IServiceInstaller
{
    const string Launchctl = "/bin/launchctl";
    static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    const int BootstrapAttempts = 3;
    /// <summary>launchd's exit code when a bootstrap races the teardown of the previous job: Input/output error.</summary>
    const int TeardownRaceExitCode = 5;

    readonly BridgePaths _paths;
    readonly IProcessRunner _runner;
    readonly ToolLocator _tools;
    string? _domain;

    public LaunchdServiceInstaller(BridgePaths paths, IProcessRunner runner, ToolLocator tools)
    {
        _paths = paths;
        _runner = runner;
        _tools = tools;
    }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How long to wait for launchd to finish removing or registering the agent; bounded, never longer.</summary>
    public TimeSpan ReloadWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long to sleep between launchctl polls and bootstrap retries.</summary>
    public TimeSpan ReloadPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public bool IsInstalled => File.Exists(_paths.LaunchAgentPlist);

    public async Task<bool> IsLoadedAsync(CancellationToken cancellationToken)
    {
        var result = await LaunchctlAsync(["print", await ServiceTargetAsync(cancellationToken)], cancellationToken);
        return result.Ran && result.ExitCode == 0;
    }

    public LaunchAgentSettings Settings(string firstmateHome)
    {
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = _tools.BuildPath(),
            ["FM_HOME"] = firstmateHome,
            ["LANG"] = "en_US.UTF-8",
        };
        if (_tools.DotnetRoot() is { } dotnetRoot)
            environment["DOTNET_ROOT"] = dotnetRoot;
        return new LaunchAgentSettings(BridgePaths.ServiceLabel, _paths.InstalledProgram, environment, _paths.LaunchdStandardOut, _paths.LaunchdStandardError);
    }

    public async Task InstallAsync(string firstmateHome, CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.InstalledProgram))
            throw new BridgeException($"The bridge is not installed at {_paths.InstalledProgram}. Run ./install.sh from the firstmate-telegram clone.");

        PrivateFiles.EnsureDirectory(_paths.LogDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.LaunchAgentPlist)!);
        PrivateFiles.WriteAtomic(_paths.LaunchAgentPlist, LaunchAgentPlist.Build(Settings(firstmateHome)), PlistMode);
        await StopAsync(cancellationToken);
        await BootstrapAsync(cancellationToken);
    }

    public async Task UninstallAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        File.Delete(_paths.LaunchAgentPlist);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsInstalled)
            throw new BridgeException("The login agent is not installed. Run ./install.sh, or `firstmate-telegram run` to run the bridge in this terminal.");

        if (!await IsLoadedAsync(cancellationToken))
            await BootstrapAsync(cancellationToken);
        await RequireAsync(["kickstart", await ServiceTargetAsync(cancellationToken)], "start the login agent", cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!await IsLoadedAsync(cancellationToken))
            return;

        await RequireAsync(["bootout", await ServiceTargetAsync(cancellationToken)], "unload the login agent", cancellationToken);
        // bootout can return before launchd has finished removing the job, and a bootstrap racing that
        // teardown fails with error 5. Wait, bounded, for print to report the job gone (best effort;
        // the bootstrap retries below are the backstop).
        await WaitUntilAsync(loaded: false, cancellationToken);
    }

    const UnixFileMode PlistMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>Loads the agent, retrying the teardown race a bounded number of times, then verifies the job is loaded.</summary>
    async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        var arguments = (IReadOnlyList<string>)["bootstrap", await DomainAsync(cancellationToken), _paths.LaunchAgentPlist];
        for (var attempt = 1; ; attempt++)
        {
            var result = await LaunchctlAsync(arguments, cancellationToken);
            if (result.Ran && result.ExitCode == 0)
                break;

            if (attempt < BootstrapAttempts && result.Ran && IsTeardownRace(result))
            {
                await Task.Delay(ReloadPollInterval, Time, cancellationToken);
                continue;
            }

            var detail = result.TimedOut ? "timed out" : result.StartFailure ?? result.StandardError.Trim();
            var attempts = attempt > 1 ? $" ({attempt} attempts)" : "";
            throw new BridgeException(
                $"launchctl bootstrap could not load the login agent{attempts}: {detail}. " +
                LoadRecovery);
        }

        if (!await WaitUntilAsync(loaded: true, cancellationToken))
            throw new BridgeException(
                $"launchctl bootstrap reported success but the login agent is not loaded in launchd. " + LoadRecovery);
    }

    const string LoadRecovery =
        "The login agent is installed but not loaded; run `firstmate-telegram start` to load it, then `firstmate-telegram doctor` to check it.";

    /// <summary>The bootstrap raced launchd's removal of the previous job: Input/output error, exit code 5.</summary>
    static bool IsTeardownRace(ProcessResult result) =>
        result.ExitCode == TeardownRaceExitCode || result.StandardError.Contains("Input/output error", StringComparison.Ordinal);

    /// <summary>Polls print until the job matches <paramref name="loaded"/>, no longer than <see cref="ReloadWait"/>.</summary>
    async Task<bool> WaitUntilAsync(bool loaded, CancellationToken cancellationToken)
    {
        var deadline = Time.GetUtcNow() + ReloadWait;
        while (await IsLoadedAsync(cancellationToken) != loaded)
        {
            if (Time.GetUtcNow() >= deadline)
                return false;
            await Task.Delay(ReloadPollInterval, Time, cancellationToken);
        }
        return true;
    }

    async Task RequireAsync(IReadOnlyList<string> arguments, string purpose, CancellationToken cancellationToken)
    {
        var result = await LaunchctlAsync(arguments, cancellationToken);
        if (result.Ran && result.ExitCode == 0)
            return;

        var detail = result.TimedOut ? "timed out" : result.StartFailure ?? result.StandardError.Trim();
        throw new BridgeException($"launchctl {arguments[0]} could not {purpose}: {detail}");
    }

    Task<ProcessResult> LaunchctlAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        _runner.RunAsync(new ProcessRequest(Launchctl, arguments, CallTimeout), cancellationToken);

    async Task<string> ServiceTargetAsync(CancellationToken cancellationToken) =>
        $"{await DomainAsync(cancellationToken)}/{BridgePaths.ServiceLabel}";

    async Task<string> DomainAsync(CancellationToken cancellationToken)
    {
        if (_domain is not null)
            return _domain;

        var result = await _runner.RunAsync(new ProcessRequest("/usr/bin/id", ["-u"], CallTimeout), cancellationToken);
        if (!result.Ran || result.ExitCode != 0 || !int.TryParse(result.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
            throw new BridgeException("Could not read the current user id with /usr/bin/id -u.");
        _domain = string.Create(CultureInfo.InvariantCulture, $"gui/{uid}");
        return _domain;
    }
}
