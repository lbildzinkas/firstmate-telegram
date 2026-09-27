namespace FirstmateTelegram.Service;

/// <summary>
/// Builds the login agent's environment from the installing shell: launchd gives agents only the system folders,
/// which would leave FirstMate's scripts on macOS's old bash and without Homebrew's python3 and jq.
/// </summary>
public sealed class ToolLocator
{
    public const string DefaultDotnetRoot = "/usr/local/share/dotnet";

    public static readonly IReadOnlyList<string> PathTools = ["bash", "python3", "jq", "quota-axi", "dotnet"];
    public static readonly IReadOnlyList<string> SystemDirectories = ["/usr/bin", "/bin", "/usr/sbin", "/sbin"];

    readonly Func<string, string?> _getVariable;

    public ToolLocator(Func<string, string?> getVariable) => _getVariable = getVariable;

    /// <summary>The first executable named <paramref name="tool"/> on the installing shell's PATH.</summary>
    public string? Find(string tool)
    {
        foreach (var directory in (_getVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, tool);
            if (File.Exists(candidate) && IsExecutable(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>The folders where the tools resolve, in order, followed by the system folders.</summary>
    public string BuildPath()
    {
        var directories = new List<string>();
        foreach (var tool in PathTools)
        {
            if (Find(tool) is { } found && Path.GetDirectoryName(found) is { } directory && !directories.Contains(directory))
                directories.Add(directory);
        }

        foreach (var directory in SystemDirectories)
        {
            if (!directories.Contains(directory))
                directories.Add(directory);
        }

        return string.Join(':', directories);
    }

    /// <summary>The .NET root, only when dotnet lives outside its default location, such as a Homebrew install.</summary>
    public string? DotnetRoot()
    {
        var root = _getVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(root) && Find("dotnet") is { } dotnet)
        {
            var resolved = new FileInfo(dotnet).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dotnet;
            root = Path.GetDirectoryName(resolved);
        }

        if (string.IsNullOrEmpty(root))
            return null;
        return Path.TrimEndingDirectorySeparator(root) == DefaultDotnetRoot ? null : root;
    }

    static bool IsExecutable(string path) =>
        (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}
