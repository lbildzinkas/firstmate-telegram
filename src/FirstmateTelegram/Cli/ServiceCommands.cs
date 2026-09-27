using FirstmateTelegram.Configuration;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.State;

namespace FirstmateTelegram.Cli;

/// <summary><c>start</c>, <c>stop</c> and <c>service install|uninstall</c>: the bridge's local on/off switch and its login agent.</summary>
public static class ServiceCommands
{
    public static async Task<int> StopAsync(LocalEnvironment environment, CancellationToken cancellationToken)
    {
        var console = environment.Console;
        var service = environment.Service;
        if (service.IsInstalled)
            await service.StopAsync(cancellationToken);

        using var instance = await InstanceLock.AcquireAsync(environment.Paths.LockFile, environment.StopWait, environment.Time, cancellationToken);
        if (instance is null)
        {
            console.WriteLine("The bridge is still running outside the login agent (for example `firstmate-telegram run` in a terminal). Stop it there, then run stop again.");
            return 1;
        }

        using var store = StateStore.Open(environment.Paths);
        await store.UpdateStateAsync(state => state with { Stopped = true }, cancellationToken);
        console.WriteLine("Bridge stopped. It stays stopped, across logins and restarts, until `firstmate-telegram start`.");
        return 0;
    }

    public static async Task<int> StartAsync(LocalEnvironment environment, CancellationToken cancellationToken)
    {
        var console = environment.Console;
        using (var instance = InstanceLock.TryAcquire(environment.Paths.LockFile))
        {
            if (instance is null)
            {
                console.WriteLine("The bridge is already running.");
                return 0;
            }

            using var store = StateStore.Open(environment.Paths);
            await store.UpdateStateAsync(state => state with { Stopped = false }, cancellationToken);
        }

        await environment.Service.StartAsync(cancellationToken);
        console.WriteLine("Bridge started.");
        return 0;
    }

    public static async Task<int> InstallAsync(LocalEnvironment environment, CancellationToken cancellationToken)
    {
        var config = ConfigFile.Load(environment.Paths);
        await environment.Service.InstallAsync(config.FirstmateHome, cancellationToken);
        environment.Console.WriteLine($"Login agent installed: {environment.Paths.LaunchAgentPlist}");

        using var store = StateStore.Open(environment.Paths);
        if (store.State.Stopped)
            environment.Console.WriteLine("The stopped flag is set, so the bridge stays stopped until `firstmate-telegram start`.");
        return 0;
    }

    public static async Task<int> UninstallAsync(LocalEnvironment environment, CancellationToken cancellationToken)
    {
        await environment.Service.UninstallAsync(cancellationToken);
        environment.Console.WriteLine("Login agent removed.");
        return 0;
    }
}
