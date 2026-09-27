using System.Globalization;
using FirstmateTelegram.Configuration;
using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.State;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace FirstmateTelegram.Cli;

public sealed record DoctorCheck(string Name, bool IsOk, string Detail);

/// <summary>
/// Checks everything the chat loop needs and says how to fix what is wrong: configuration, the token file and token,
/// the webhook and other pollers, the FirstMate home and its schemas, tool paths, and the login agent.
/// </summary>
public static class DoctorCommand
{
    static readonly string[] RequiredTools = ["bash", "python3", "jq"];

    public static async Task<int> RunAsync(LocalEnvironment environment, CancellationToken cancellationToken)
    {
        var checks = await CheckAsync(environment, config: null, cancellationToken);
        Print(environment.Console, checks);
        return checks.All(check => check.IsOk) ? 0 : 1;
    }

    public static void Print(IConsoleIO console, IReadOnlyList<DoctorCheck> checks)
    {
        foreach (var check in checks)
            console.WriteLine($"{(check.IsOk ? "ok     " : "PROBLEM")}  {check.Name}: {check.Detail}");

        var problems = checks.Count(check => !check.IsOk);
        console.WriteLine(problems == 0 ? "Everything checks out." : string.Create(CultureInfo.InvariantCulture, $"{problems} problem(s) found."));
    }

    /// <summary>Runs every check. Setup passes the configuration it is about to write.</summary>
    public static async Task<IReadOnlyList<DoctorCheck>> CheckAsync(LocalEnvironment environment, BridgeConfig? config, CancellationToken cancellationToken)
    {
        var paths = environment.Paths;
        var checks = new List<DoctorCheck>();

        if (config is null)
        {
            try
            {
                config = ConfigFile.Load(paths);
                checks.Add(new("configuration", true, paths.ConfigFile));
            }
            catch (BridgeException exception)
            {
                checks.Add(new("configuration", false, exception.Message));
            }
        }

        ITelegramBotClient? client = null;
        try
        {
            client = environment.TelegramClients.Create(TokenFile.Read(paths));
            checks.Add(new("token file", true, "private (the folder is 700 and the file 600)"));
        }
        catch (BridgeException exception)
        {
            checks.Add(new("token file", false, exception.Message));
        }

        if (client is not null)
            await CheckTelegramAsync(environment, client, checks, cancellationToken);
        if (config is not null)
            await CheckFirstMateAsync(environment, config, checks, cancellationToken);

        foreach (var tool in RequiredTools)
        {
            var found = environment.Tools.Find(tool);
            checks.Add(found is null
                ? new($"tool {tool}", false, $"not found on PATH; FirstMate needs it. Install it, then run `firstmate-telegram service install` so the login agent's PATH includes it.")
                : new($"tool {tool}", true, found));
        }

        await CheckServiceAsync(environment, checks, cancellationToken);
        return checks;
    }

    static async Task CheckTelegramAsync(LocalEnvironment environment, ITelegramBotClient client, List<DoctorCheck> checks, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(environment.TelegramTimeout);
        try
        {
            var me = await client.GetMe(deadline.Token);
            checks.Add(new("bot token", true, string.Create(CultureInfo.InvariantCulture, $"accepted for @{me.Username} (bot id {me.Id})")));

            var webhook = await client.GetWebhookInfo(deadline.Token);
            checks.Add(string.IsNullOrEmpty(webhook.Url)
                ? new("webhook", true, "none set")
                : new("webhook", false, "a webhook is set, so long polling cannot work. Run `firstmate-telegram setup`, which offers to remove it."));
        }
        catch (ApiRequestException exception) when (exception.ErrorCode == 401)
        {
            checks.Add(new("bot token", false, "Telegram rejected the token (revoked?). Run `firstmate-telegram setup` to save a new one."));
            return;
        }
        catch (Exception exception) when (exception is RequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            checks.Add(new("bot token", false, "could not reach Telegram to check it; check the network and try again."));
            return;
        }

        using var instance = InstanceLock.TryAcquire(environment.Paths.LockFile);
        if (instance is null)
        {
            checks.Add(new("other pollers", true, "not checked while the bridge is running"));
            return;
        }

        try
        {
            // No offset, so nothing is confirmed.
            await client.GetUpdates(limit: 1, timeout: 0, cancellationToken: deadline.Token);
            checks.Add(new("other pollers", true, "none"));
        }
        catch (ApiRequestException exception) when (exception.ErrorCode == 409)
        {
            checks.Add(new("other pollers", false, "another program is polling with this bot token, or a webhook is set. Stop the other copy; only one poller may use a token."));
        }
        catch (Exception exception) when (exception is RequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            checks.Add(new("other pollers", false, "could not reach Telegram to check."));
        }
    }

    static async Task CheckFirstMateAsync(LocalEnvironment environment, BridgeConfig config, List<DoctorCheck> checks, CancellationToken cancellationToken)
    {
        var client = new FirstMateClient(environment.ProcessRunner, config.FirstmateHome);
        if (!File.Exists(client.InboxScript))
        {
            checks.Add(new("FirstMate home", false, $"{client.InboxScript} is missing. Check \"firstmate_home\" in {environment.Paths.ConfigFile}."));
            return;
        }

        var ready = await client.ReadReadyAsync(cancellationToken);
        checks.Add(ready.Failure is { } readyFailure
            ? new("FirstMate ready", false, readyFailure.Reason)
            : new("FirstMate ready", true, $"{FirstMateClient.ReadySchema}: {Describe(ReadinessRules.Classify(ready))}, posture {ready.Reading!.PostureState}"));

        var receipts = await client.ReadReceiptsAsync("", cancellationToken);
        checks.Add(receipts.Failure is { } receiptsFailure
            ? new("FirstMate receipts", false, receiptsFailure.Reason)
            : new("FirstMate receipts", true, FirstMateClient.ReceiptsSchema));

        checks.Add(CheckFleetLedger(config));
    }

    /// <summary>The ledger is opt-in and off by default; the bridge never creates the flag (spec 7.1).</summary>
    static DoctorCheck CheckFleetLedger(BridgeConfig config)
    {
        var flag = Path.Combine(config.FirstmateHome, "config", "fleet-ledger");
        if (!File.Exists(flag))
            return new("fleet ledger", false, "off. Create an empty file config/fleet-ledger in the FirstMate home to turn on FirstMate's fleet ledger; without it only decision alerts work.");

        var ledger = new FleetLedger(config.FirstmateHome);
        if (!File.Exists(ledger.FilePath))
            return new("fleet ledger", true, "on; no events recorded yet");

        var read = ledger.Read(new LedgerPosition(0, 0, 0));
        return read is { SkippedOtherVersions: > 0 } skipped
            ? new("fleet ledger", true, $"on; skipped {skipped.SkippedOtherVersions.ToString(CultureInfo.InvariantCulture)} record(s) with a newer format")
            : new("fleet ledger", true, "on");
    }

    static async Task CheckServiceAsync(LocalEnvironment environment, List<DoctorCheck> checks, CancellationToken cancellationToken)
    {
        var service = environment.Service;
        if (!service.IsInstalled)
            checks.Add(new("login agent", false, "not installed. Run ./install.sh from the firstmate-telegram clone."));
        else if (!await service.IsLoadedAsync(cancellationToken))
            checks.Add(new("login agent", false, "installed but not loaded. Run `firstmate-telegram start`."));
        else
            checks.Add(new("login agent", true, "installed and loaded"));

        try
        {
            using var store = StateStore.Open(environment.Paths);
            checks.Add(store.State.Stopped
                ? new("stopped flag", false, "set; the bridge stays stopped until `firstmate-telegram start`.")
                : new("stopped flag", true, "not set"));
        }
        catch (BridgeException exception)
        {
            checks.Add(new("bridge state", false, exception.Message));
        }
    }

    public static string Describe(Readiness readiness) => readiness switch
    {
        Readiness.NotRunning => "not running",
        Readiness.NotPickingUp => "running, but not picking up requests",
        Readiness.RunningListening => "running, listening",
        Readiness.RunningListeningUnconfirmed => "running; listening could not be confirmed",
        _ => "unknown",
    };
}
