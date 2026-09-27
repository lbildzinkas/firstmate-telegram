namespace FirstmateTelegram.Configuration;

/// <summary>
/// Where the bridge keeps its own files. It never writes inside the FirstMate home.
/// <c>XDG_CONFIG_HOME</c> and <c>XDG_STATE_HOME</c> are honoured when set.
/// </summary>
public sealed record BridgePaths(
    string Home,
    string ConfigDirectory,
    string StateDirectory,
    string LogDirectory,
    string AppDirectory,
    string BinLink,
    string LaunchAgentPlist)
{
    public const string ServiceLabel = "io.github.lbildzinkas.firstmate-telegram";
    public const string ProgramName = "firstmate-telegram";

    public string TokenFile => Path.Combine(ConfigDirectory, "token");
    public string ConfigFile => Path.Combine(ConfigDirectory, "config.json");
    public string StateFile => Path.Combine(StateDirectory, "state.json");
    public string RequestsFile => Path.Combine(StateDirectory, "requests.json");
    public string AlertsFile => Path.Combine(StateDirectory, "alerts.json");
    public string LockFile => Path.Combine(StateDirectory, "lock");
    public string LogFile => Path.Combine(LogDirectory, "bridge.log");
    public string LaunchdStandardOut => Path.Combine(LogDirectory, "launchd.out.log");
    public string LaunchdStandardError => Path.Combine(LogDirectory, "launchd.err.log");
    public string InstalledProgram => Path.Combine(AppDirectory, ProgramName);

    public static BridgePaths FromEnvironment(Func<string, string?> getVariable)
    {
        var home = getVariable("HOME");
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var configRoot = AbsoluteOrNull(getVariable("XDG_CONFIG_HOME")) ?? Path.Combine(home, ".config");
        var stateRoot = AbsoluteOrNull(getVariable("XDG_STATE_HOME")) ?? Path.Combine(home, ".local", "state");
        return new BridgePaths(
            Home: home,
            ConfigDirectory: Path.Combine(configRoot, ProgramName),
            StateDirectory: Path.Combine(stateRoot, ProgramName),
            LogDirectory: Path.Combine(home, "Library", "Logs", ProgramName),
            AppDirectory: Path.Combine(home, ".local", "share", ProgramName, "app"),
            BinLink: Path.Combine(home, ".local", "bin", ProgramName),
            LaunchAgentPlist: Path.Combine(home, "Library", "LaunchAgents", ServiceLabel + ".plist"));
    }

    public static BridgePaths FromProcessEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    // The XDG base directory spec says a relative value is invalid and must be ignored.
    static string? AbsoluteOrNull(string? value) => string.IsNullOrEmpty(value) || !Path.IsPathRooted(value) ? null : value;
}
