using System.Reflection;
using FirstmateTelegram.Configuration;
using FirstmateTelegram.Infrastructure;

namespace FirstmateTelegram.Cli;

public static class CommandLine
{
    const string Usage = """
        Usage: firstmate-telegram <command>

          run                  Run the bridge (the login agent starts this)
          setup                Save the bot token, check the FirstMate home, and pair your Telegram account
          start                Clear the stopped flag and start the login agent
          stop                 Stop the bridge until `firstmate-telegram start`
          doctor               Check everything and say how to fix what is wrong
          service install      Write and load the login agent (install.sh runs this)
          service uninstall    Unload and remove the login agent
          version              Print the version
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var paths = BridgePaths.FromProcessEnvironment();
        var local = new LocalEnvironment(paths, new SystemConsoleIO());
        try
        {
            return args switch
            {
                ["run"] => await RunCommand.RunAsync(new RunEnvironment(paths) { LogEcho = Console.IsErrorRedirected ? null : Console.Error }, cancellationToken),
                ["setup"] => await SetupCommand.RunAsync(local, cancellationToken),
                ["start"] => await ServiceCommands.StartAsync(local, cancellationToken),
                ["stop"] => await ServiceCommands.StopAsync(local, cancellationToken),
                ["doctor"] => await DoctorCommand.RunAsync(local, cancellationToken),
                ["service", "install"] => await ServiceCommands.InstallAsync(local, cancellationToken),
                ["service", "uninstall"] => await ServiceCommands.UninstallAsync(local, cancellationToken),
                ["version"] or ["--version"] => PrintVersion(),
                [] or ["help"] or ["--help"] or ["-h"] => PrintUsage(Console.Out, 0),
                _ => PrintUsage(Console.Error, 2),
            };
        }
        catch (BridgeException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
    }

    static int PrintUsage(TextWriter writer, int exitCode)
    {
        writer.WriteLine(Usage);
        return exitCode;
    }

    static int PrintVersion()
    {
        var version = typeof(CommandLine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        Console.WriteLine($"firstmate-telegram {version}");
        return 0;
    }
}
