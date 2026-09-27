using FirstmateTelegram.Logging;
using Microsoft.Extensions.Logging;

namespace FirstmateTelegram.Tests.Support;

/// <summary>The bridge's real log file writer, pointed at a test folder, so tests can read what would be logged.</summary>
public sealed class TestLogging : IDisposable
{
    readonly BridgeLogFile _file;
    readonly ILoggerFactory _factory;

    public TestLogging(string path)
    {
        Path = path;
        _file = new BridgeLogFile(path);
        _factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(new BridgeLoggerProvider(_file, TimeProvider.System)));
    }

    public string Path { get; }

    public ILogger<T> For<T>() => _factory.CreateLogger<T>();

    public string Text() => File.Exists(Path) ? File.ReadAllText(Path) : "";

    public void Dispose()
    {
        _factory.Dispose();
        _file.Dispose();
    }
}
