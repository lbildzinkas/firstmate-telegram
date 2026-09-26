using FirstmateTelegram.Infrastructure;

namespace FirstmateTelegram.Configuration;

/// <summary>
/// The bot token, one line in a mode-600 file inside a mode-700 folder. It never goes into the plist, a command line or the environment.
/// </summary>
public static class TokenFile
{
    public static bool Exists(BridgePaths paths) => File.Exists(paths.TokenFile);

    /// <summary>Reads the token, refusing when the file or its folder is readable by group or others.</summary>
    public static string Read(BridgePaths paths)
    {
        var directory = paths.ConfigDirectory;
        var file = paths.TokenFile;
        if (!File.Exists(file))
            throw new BridgeException($"No bot token at {file}. Run `firstmate-telegram setup` first.");
        if (PrivateFiles.IsReadableByOthers(directory))
            throw new BridgeException($"{directory} is readable by other users. Fix it with: chmod 700 '{directory}'");
        if (PrivateFiles.IsReadableByOthers(file))
            throw new BridgeException($"{file} is readable by other users. Fix it with: chmod 600 '{file}'");

        var token = File.ReadLines(file).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(token))
            throw new BridgeException($"{file} is empty. Run `firstmate-telegram setup` to save a token.");
        return token;
    }

    public static void Write(BridgePaths paths, string token)
    {
        PrivateFiles.EnsureDirectory(paths.ConfigDirectory);
        File.SetUnixFileMode(paths.ConfigDirectory, PrivateFiles.OwnerOnlyDirectory);
        PrivateFiles.WriteAtomic(paths.TokenFile, token.Trim() + "\n");
    }
}
