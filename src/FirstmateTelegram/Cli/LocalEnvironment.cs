using FirstmateTelegram.Configuration;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.Service;
using FirstmateTelegram.Telegram;

namespace FirstmateTelegram.Cli;

/// <summary>What the local commands (setup, doctor, start, stop, service) work with; tests replace the outside world.</summary>
public sealed record LocalEnvironment(BridgePaths Paths, IConsoleIO Console)
{
    public TelegramClientFactory TelegramClients { get; init; } = new();
    public IProcessRunner ProcessRunner { get; init; } = new SystemProcessRunner();
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public Func<string, string?> GetVariable { get; init; } = Environment.GetEnvironmentVariable;
    public IServiceInstaller? ServiceInstaller { get; init; }

    /// <summary>How long a Telegram call from a local command may take.</summary>
    public TimeSpan TelegramTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long <c>stop</c> waits for the bridge to release its lock.</summary>
    public TimeSpan StopWait { get; init; } = TimeSpan.FromSeconds(15);

    public ToolLocator Tools => new(GetVariable);

    public IServiceInstaller Service => ServiceInstaller ?? new LaunchdServiceInstaller(Paths, ProcessRunner, Tools);
}
