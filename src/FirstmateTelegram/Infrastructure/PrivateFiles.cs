using System.Text;

namespace FirstmateTelegram.Infrastructure;

/// <summary>
/// Owner-only files and folders, and atomic writes: a temporary file, flushed to disk, then renamed over the target.
/// </summary>
public static class PrivateFiles
{
    public const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode OwnerOnlyDirectory = OwnerOnlyFile | UnixFileMode.UserExecute;

    const UnixFileMode ReadableByOthers = UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Creates the folder with mode 700 when it is missing. An existing folder keeps its mode.</summary>
    public static void EnsureDirectory(string path)
    {
        if (Directory.Exists(path))
            return;

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        Directory.CreateDirectory(path, OwnerOnlyDirectory);
    }

    public static bool IsReadableByOthers(string path) => (File.GetUnixFileMode(path) & ReadableByOthers) != 0;

    public static void WriteAtomic(string path, string contents, UnixFileMode mode = OwnerOnlyFile)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = mode,
            };
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(Utf8.GetBytes(contents));
                stream.Flush(flushToDisk: true);
            }

            File.SetUnixFileMode(temporary, mode);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }
}
