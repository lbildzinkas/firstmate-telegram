using System.Security;
using System.Text;

namespace FirstmateTelegram.Service;

public sealed record LaunchAgentSettings(
    string Label,
    string Program,
    IReadOnlyDictionary<string, string> Environment,
    string StandardOutPath,
    string StandardErrorPath);

/// <summary>
/// The login agent's plist: restarted after a crash but not after a deliberate stop (exit 0), at most every 10 s,
/// with an explicit environment. It never carries the bot token.
/// </summary>
public static class LaunchAgentPlist
{
    public static string Build(LaunchAgentSettings settings)
    {
        var plist = new StringBuilder();
        plist.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        plist.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        plist.Append("<plist version=\"1.0\">\n<dict>\n");
        plist.Append($"  <key>Label</key><string>{Escape(settings.Label)}</string>\n");
        plist.Append("  <key>ProgramArguments</key>\n  <array>\n");
        plist.Append($"    <string>{Escape(settings.Program)}</string>\n");
        plist.Append("    <string>run</string>\n  </array>\n");
        plist.Append("  <key>RunAtLoad</key><true/>\n");
        plist.Append("  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>\n");
        plist.Append("  <key>ThrottleInterval</key><integer>10</integer>\n");
        plist.Append("  <key>EnvironmentVariables</key>\n  <dict>\n");
        foreach (var (name, value) in settings.Environment)
            plist.Append($"    <key>{Escape(name)}</key><string>{Escape(value)}</string>\n");
        plist.Append("  </dict>\n");
        plist.Append($"  <key>StandardOutPath</key><string>{Escape(settings.StandardOutPath)}</string>\n");
        plist.Append($"  <key>StandardErrorPath</key><string>{Escape(settings.StandardErrorPath)}</string>\n");
        plist.Append("</dict>\n</plist>\n");
        return plist.ToString();
    }

    static string Escape(string value) => SecurityElement.Escape(value);
}
