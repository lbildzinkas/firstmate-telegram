using System.Globalization;
using FirstmateTelegram.Infrastructure;
using Microsoft.Extensions.Logging;

namespace FirstmateTelegram.Logging;

/// <summary>
/// The local log file: times and ids only, every line token-masked, rotated at a size limit with a fixed number of files kept.
/// </summary>
public sealed class BridgeLogFile : IDisposable
{
    public const long DefaultMaxBytes = 5 * 1024 * 1024;
    public const int DefaultKeptFiles = 3;

    readonly string _path;
    readonly long _maxBytes;
    readonly int _keptFiles;
    readonly TextWriter? _echo;
    readonly Lock _gate = new();
    FileStream? _stream;

    public BridgeLogFile(string path, long maxBytes = DefaultMaxBytes, int keptFiles = DefaultKeptFiles, TextWriter? echo = null)
    {
        _path = path;
        _maxBytes = maxBytes;
        _keptFiles = keptFiles;
        _echo = echo;
    }

    public void Write(string line)
    {
        var masked = TokenMask.Apply(line);
        var bytes = PrivateFiles.Utf8.GetBytes(masked + "\n");
        lock (_gate)
        {
            _echo?.WriteLine(masked);
            try
            {
                var stream = _stream ??= Open();
                if (stream.Length > 0 && stream.Length + bytes.Length > _maxBytes)
                    stream = Rotate();
                stream.Write(bytes);
                stream.Flush();
            }
            catch (IOException)
            {
                // Logging must never stop the bridge; the next line tries again.
                _stream?.Dispose();
                _stream = null;
            }
        }
    }

    FileStream Open()
    {
        PrivateFiles.EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var options = new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite,
            UnixCreateMode = PrivateFiles.OwnerOnlyFile,
        };
        return new FileStream(_path, options);
    }

    FileStream Rotate()
    {
        _stream?.Dispose();
        _stream = null;
        File.Delete(RotatedPath(_keptFiles - 1));
        for (var index = _keptFiles - 2; index >= 1; index--)
        {
            if (File.Exists(RotatedPath(index)))
                File.Move(RotatedPath(index), RotatedPath(index + 1), overwrite: true);
        }

        if (_keptFiles > 1)
            File.Move(_path, RotatedPath(1), overwrite: true);
        else
            File.Delete(_path);
        _stream = Open();
        return _stream;
    }

    string RotatedPath(int index) => $"{_path}.{index.ToString(CultureInfo.InvariantCulture)}";

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}

public sealed class BridgeLoggerProvider : ILoggerProvider
{
    readonly BridgeLogFile _file;
    readonly TimeProvider _time;

    public BridgeLoggerProvider(BridgeLogFile file, TimeProvider time)
    {
        _file = file;
        _time = time;
    }

    public ILogger CreateLogger(string categoryName) => new BridgeLogger(_file, _time, ShortCategory(categoryName));

    public void Dispose()
    {
    }

    static string ShortCategory(string categoryName)
    {
        var lastDot = categoryName.LastIndexOf('.');
        return lastDot < 0 ? categoryName : categoryName[(lastDot + 1)..];
    }

    sealed class BridgeLogger(BridgeLogFile file, TimeProvider time, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var timestamp = time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var line = $"{timestamp} {LevelName(logLevel)} {category}: {formatter(state, exception)}";
            if (exception is not null)
                line += $" ({exception.GetType().Name}: {exception.Message})";
            file.Write(line);
        }

        static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            _ => "fatal",
        };
    }
}
