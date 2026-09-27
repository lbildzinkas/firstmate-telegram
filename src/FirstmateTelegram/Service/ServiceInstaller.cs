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
        await RequireAsync(["bootstrap", await DomainAsync(cancellationToken), _paths.LaunchAgentPlist], "load the login agent", cancellationToken);
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
            await RequireAsync(["bootstrap", await DomainAsync(cancellationToken), _paths.LaunchAgentPlist], "load the login agent", cancellationToken);
        await RequireAsync(["kickstart", await ServiceTargetAsync(cancellationToken)], "start the login agent", cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (await IsLoadedAsync(cancellationToken))
            await RequireAsync(["bootout", await ServiceTargetAsync(cancellationToken)], "unload the login agent", cancellationToken);
    }

    const UnixFileMode PlistMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

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
