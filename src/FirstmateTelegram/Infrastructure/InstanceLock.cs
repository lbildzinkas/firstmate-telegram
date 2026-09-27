namespace FirstmateTelegram.Infrastructure;

/// <summary>
/// The single-instance lock: an exclusive lock on the state folder's lock file, released when the process exits.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    readonly FileStream _stream;

    InstanceLock(FileStream stream) => _stream = stream;

    /// <summary>Takes the lock, or returns null when another process holds it.</summary>
    public static InstanceLock? TryAcquire(string path)
    {
        PrivateFiles.EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                UnixCreateMode = PrivateFiles.OwnerOnlyFile,
            };
            return new InstanceLock(new FileStream(path, options));
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new BridgeException($"Cannot open the lock file {path}: {exception.Message}", exception);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Takes the lock, waiting up to <paramref name="wait"/> for another process to release it.</summary>
    public static async Task<InstanceLock?> AcquireAsync(string path, TimeSpan wait, TimeProvider time, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + wait;
        while (true)
        {
            var acquired = TryAcquire(path);
            if (acquired is not null || time.GetUtcNow() >= deadline)
                return acquired;
            await Task.Delay(TimeSpan.FromMilliseconds(200), time, cancellationToken);
        }
    }

    public void Dispose() => _stream.Dispose();
}
