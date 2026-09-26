using FirstmateTelegram.Bridge;
using FirstmateTelegram.Configuration;
using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.Logging;
using FirstmateTelegram.State;
using FirstmateTelegram.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

namespace FirstmateTelegram.Cli;

/// <summary>What <c>firstmate-telegram run</c> is built from; tests replace the Telegram endpoint, the runner and time.</summary>
public sealed record RunEnvironment(BridgePaths Paths)
{
    public TelegramClientFactory TelegramClients { get; init; } = new();
    public IProcessRunner ProcessRunner { get; init; } = new SystemProcessRunner();
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public TextWriter? LogEcho { get; init; }
    public TextWriter Output { get; init; } = Console.Out;
    public TextWriter Error { get; init; } = Console.Error;
}

/// <summary>
/// The long-running bridge that the login agent starts. It exits 0 at once while the stopped flag is set,
/// and non-zero when it cannot run or a background service fails, so launchd restarts it only after a crash.
/// </summary>
public static class RunCommand
{
    public static async Task<int> RunAsync(RunEnvironment environment, CancellationToken cancellationToken = default)
    {
        var paths = environment.Paths;
        using var log = new BridgeLogFile(paths.LogFile, echo: environment.LogEcho);
        using var loggerProvider = new BridgeLoggerProvider(log, environment.Time);
        var logger = loggerProvider.CreateLogger(nameof(RunCommand));

        InstanceLock? instance = null;
        try
        {
            var config = ConfigFile.Load(paths);
            using var store = StateStore.Open(paths);
            if (store.State.Stopped)
            {
                logger.LogInformation("the stopped flag is set; exiting until `firstmate-telegram start`");
                await environment.Output.WriteLineAsync("The bridge is stopped. Start it with `firstmate-telegram start`.");
                return 0;
            }

            var token = TokenFile.Read(paths);
            var client = environment.TelegramClients.Create(token);
            instance = InstanceLock.TryAcquire(paths.LockFile)
                ?? throw new BridgeException("Another copy of the bridge is already running.");
            if (!File.Exists(Path.Combine(config.FirstmateHome, "bin", "fm-inbox.sh")))
                logger.LogWarning("FirstMate's inbox script is missing from the configured home; requests will wait until it is there");

            using var host = BuildHost(environment, loggerProvider, config, store, client);
            var services = host.Services.GetServices<IHostedService>().OfType<BackgroundService>().ToList();
            logger.LogInformation("bridge starting");

            // RunAsync disposes the host when it stops; the services were taken above so their outcome can still be read.
            await host.RunAsync(cancellationToken);
            var failed = services.Any(service => service.ExecuteTask?.IsFaulted == true);
            logger.LogInformation("bridge stopped{Failure}", failed ? " after a failure" : "");
            return failed ? 1 : 0;
        }
        catch (BridgeException exception)
        {
            logger.LogError("cannot run: {Reason}", exception.Message);
            await environment.Error.WriteLineAsync(exception.Message);
            return 1;
        }
        finally
        {
            instance?.Dispose();
        }
    }

    static IHost BuildHost(RunEnvironment environment, BridgeLoggerProvider loggerProvider, BridgeConfig config, StateStore store, ITelegramBotClient client)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(loggerProvider);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);

        var services = builder.Services;
        services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
            options.ShutdownTimeout = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton(config);
        services.AddSingleton(store);
        services.AddSingleton(environment.Time);
        services.AddSingleton(environment.ProcessRunner);
        services.AddSingleton<WakeSignal>();
        services.AddSingleton(provider => new TelegramGateway(client, environment.Time, provider.GetRequiredService<ILogger<TelegramGateway>>()));
        services.AddSingleton(provider => new FirstMateClient(provider.GetRequiredService<IProcessRunner>(), config.FirstmateHome));
        services.AddSingleton(provider => new AccessGate(config.AllowedUserId, provider.GetRequiredService<ILogger<AccessGate>>()));
        services.AddSingleton<CommandRouter>();
        services.AddSingleton<RequestSubmitter>();
        services.AddHostedService<UpdatePoller>();
        services.AddHostedService(provider => new ReplyForwarder(
            provider.GetRequiredService<FirstMateClient>(),
            provider.GetRequiredService<TelegramGateway>(),
            store,
            provider.GetRequiredService<WakeSignal>(),
            config.RepliedReaction,
            environment.Time,
            provider.GetRequiredService<ILogger<ReplyForwarder>>()));
        return builder.Build();
    }
}
