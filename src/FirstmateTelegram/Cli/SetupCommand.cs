using System.Globalization;
using FirstmateTelegram.Bridge;
using FirstmateTelegram.Configuration;
using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.State;
using FirstmateTelegram.Telegram;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace FirstmateTelegram.Cli;

/// <summary>
/// <c>firstmate-telegram setup</c>: saves the bot token, clears a webhook, checks the FirstMate home, and pairs exactly
/// one Telegram account, confirmed here on the Mac and never from the phone. It changes nothing in the FirstMate home.
/// </summary>
public static class SetupCommand
{
    const string CaptainPreference =
        "Requests that arrive with the firstmate-telegram footer cannot approve merges, deletions, or irreversible or security-sensitive actions; ask me to confirm at the terminal.";

    public static async Task<int> RunAsync(LocalEnvironment environment, CancellationToken cancellationToken)
    {
        var console = environment.Console;
        var paths = environment.Paths;
        var existing = LoadExisting(environment);

        var (client, me) = await AskForTokenAsync(environment, cancellationToken);
        if (client is null || me is null)
            return 1;
        if (!await ClearWebhookAsync(environment, client, cancellationToken))
            return 1;

        var home = await AskForHomeAsync(environment, existing?.FirstmateHome, cancellationToken);
        if (home is null)
            return 1;

        long userId;
        var service = environment.Service;
        if (service.IsInstalled)
            await service.StopAsync(cancellationToken);
        using (var instance = InstanceLock.TryAcquire(paths.LockFile))
        {
            if (instance is null)
            {
                console.WriteLine("Another copy of the bridge is running (for example `firstmate-telegram run` in a terminal). Stop it, then run setup again.");
                return 1;
            }

            var paired = await PairAsync(environment, client, me, cancellationToken);
            if (paired is null)
                return 1;
            userId = paired.Value;
        }

        var config = existing is null
            ? new BridgeConfig { FirstmateHome = home, AllowedUserId = userId }
            : existing with { FirstmateHome = home, AllowedUserId = userId };

        console.WriteLine();
        console.WriteLine("Checking everything:");
        DoctorCommand.Print(console, await DoctorCommand.CheckAsync(environment, config, cancellationToken));
        console.WriteLine();
        console.WriteLine("Optional next steps:");
        console.WriteLine($"- In BotFather, turn off \"Allow Groups\" for @{me.Username} (Bot Settings > Allow Groups), so the bot cannot be added to groups.");
        console.WriteLine("- To have FirstMate itself hold the phone to a lower authority, add this line to data/captain.md in your FirstMate home:");
        console.WriteLine($"  {CaptainPreference}");

        ConfigFile.Write(paths, config);
        console.WriteLine();
        console.WriteLine($"Saved {paths.ConfigFile}.");
        await RestartAsync(environment, cancellationToken);
        return 0;
    }

    static BridgeConfig? LoadExisting(LocalEnvironment environment)
    {
        if (!ConfigFile.Exists(environment.Paths))
            return null;

        try
        {
            return ConfigFile.Load(environment.Paths);
        }
        catch (BridgeException exception)
        {
            environment.Console.WriteLine($"{exception.Message} Setup will write a new configuration.");
            return null;
        }
    }

    static async Task<(ITelegramBotClient? Client, User? Me)> AskForTokenAsync(LocalEnvironment environment, CancellationToken cancellationToken)
    {
        var console = environment.Console;
        var paths = environment.Paths;
        var hasSaved = TokenFile.Exists(paths);
        while (true)
        {
            console.Write(hasSaved
                ? "Bot token from BotFather (press Enter to keep the saved token): "
                : "Bot token from BotFather: ");
            var entered = console.ReadSecret();
            if (entered is null)
                return (null, null);

            var token = entered.Trim();
            if (token.Length == 0 && hasSaved)
            {
                try
                {
                    token = TokenFile.Read(paths);
                }
                catch (BridgeException exception)
                {
                    console.WriteLine(exception.Message);
                    hasSaved = false;
                    continue;
                }
            }

            if (!TelegramClientFactory.LooksLikeToken(token))
            {
                console.WriteLine("That does not look like a bot token; BotFather gives one shaped like 123456789:AAE...");
                continue;
            }

            var client = environment.TelegramClients.Create(token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(environment.TelegramTimeout);
            try
            {
                var me = await client.GetMe(deadline.Token);
                TokenFile.Write(paths, token);
                console.WriteLine($"Token accepted for @{me.Username}. Saved it to {paths.TokenFile}.");
                return (client, me);
            }
            catch (ApiRequestException exception) when (exception.ErrorCode == 401)
            {
                console.WriteLine("Telegram rejected this token. Copy it again from BotFather.");
            }
            catch (Exception exception) when (exception is RequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                console.WriteLine("Could not reach Telegram to check the token. Check the network and run setup again.");
                return (null, null);
            }
        }
    }

    static async Task<bool> ClearWebhookAsync(LocalEnvironment environment, ITelegramBotClient client, CancellationToken cancellationToken)
    {
        var console = environment.Console;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(environment.TelegramTimeout);
            var webhook = await client.GetWebhookInfo(deadline.Token);
            if (string.IsNullOrEmpty(webhook.Url))
                return true;

            console.WriteLine("This bot has a webhook set. The bridge pulls messages from Telegram (long polling), which cannot work while a webhook is set.");
            if (!console.Confirm("Delete the webhook now? Messages Telegram is holding are kept."))
            {
                console.WriteLine("Setup cannot continue while a webhook is set.");
                return false;
            }

            using var deleteDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deleteDeadline.CancelAfter(environment.TelegramTimeout);
            await client.DeleteWebhook(dropPendingUpdates: false, cancellationToken: deleteDeadline.Token);
            console.WriteLine("Webhook deleted.");
            return true;
        }
        catch (Exception exception) when (exception is RequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            console.WriteLine("Could not check the bot's webhook with Telegram. Check the network and run setup again.");
            return false;
        }
    }

    static async Task<string?> AskForHomeAsync(LocalEnvironment environment, string? saved, CancellationToken cancellationToken)
    {
        var console = environment.Console;
        var suggestion = saved ?? environment.GetVariable("FM_HOME");
        while (true)
        {
            console.Write(string.IsNullOrEmpty(suggestion) ? "FirstMate home folder: " : $"FirstMate home folder [{suggestion}]: ");
            var entered = console.ReadLine();
            if (entered is null)
                return null;

            var home = ExpandHome(entered.Trim(), environment.Paths.Home);
            if (home.Length == 0)
                home = suggestion ?? "";
            if (!Path.IsPathRooted(home) || !Directory.Exists(home))
            {
                console.WriteLine("Enter the full path of an existing folder.");
                continue;
            }

            home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
            var firstMate = new FirstMateClient(environment.ProcessRunner, home);
            if (!File.Exists(firstMate.InboxScript))
            {
                console.WriteLine($"{firstMate.InboxScript} is missing; that does not look like a FirstMate home.");
                continue;
            }

            var ready = await firstMate.ReadReadyAsync(cancellationToken);
            if (ready.Failure is { } failure)
            {
                console.WriteLine($"FirstMate's inbox did not answer as expected ({failure.Reason}). FirstMate at or after the version this bridge needs is required.");
                continue;
            }

            console.WriteLine($"FirstMate home checked: {DoctorCommand.Describe(ReadinessRules.Classify(ready))}.");
            return home;
        }
    }

    /// <summary>Waits for a private message, shows its sender here, and pairs only on an explicit yes.</summary>
    static async Task<long?> PairAsync(LocalEnvironment environment, ITelegramBotClient client, User me, CancellationToken cancellationToken)
    {
        var console = environment.Console;
        console.WriteLine();
        console.WriteLine($"Send any message to @{me.Username} from your own Telegram account.");
        var offset = 0;
        while (true)
        {
            var updates = await ReadUpdatesAsync(environment, client, offset, cancellationToken);
            if (updates is null)
                return null;

            foreach (var update in updates.OrderBy(update => update.Id))
            {
                offset = update.Id + 1;
                if (update.Message is not { Chat.Type: ChatType.Private, From: { } sender } message)
                    continue;

                var name = string.Join(' ', new[] { sender.FirstName, sender.LastName }.Where(part => !string.IsNullOrWhiteSpace(part)));
                var username = sender.Username is null ? "no username" : "@" + sender.Username;
                var sentAt = message.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Message from Telegram user id {sender.Id}: {name} ({username}), sent {sentAt}."));
                if (!console.Confirm("Allow only this account?"))
                {
                    console.WriteLine("Not paired. Waiting for the next message.");
                    continue;
                }

                // Confirm the pairing message so it is never delivered to FirstMate.
                if (await ReadUpdatesAsync(environment, client, offset, cancellationToken, pollSeconds: 0) is null)
                    return null;
                await TrySayPairedAsync(environment, client, message.Chat.Id, message.Id, cancellationToken);
                console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Paired with user id {sender.Id}."));
                return sender.Id;
            }
        }
    }

    static async Task<Update[]?> ReadUpdatesAsync(LocalEnvironment environment, ITelegramBotClient client, int offset, CancellationToken cancellationToken, int pollSeconds = UpdatePoller.LongPollSeconds)
    {
        while (true)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(pollSeconds) + environment.TelegramTimeout);
            try
            {
                return await client.GetUpdates(offset, timeout: pollSeconds, allowedUpdates: [UpdateType.Message], cancellationToken: deadline.Token);
            }
            catch (ApiRequestException exception) when (exception.ErrorCode == 409)
            {
                environment.Console.WriteLine("Another program is polling with this bot token. Stop it (only one poller may use a token), then run setup again.");
                return null;
            }
            catch (ApiRequestException exception) when (exception.ErrorCode == 401)
            {
                environment.Console.WriteLine("Telegram rejected the token while waiting for your message. Run setup again with a new token.");
                return null;
            }
            catch (Exception exception) when (exception is RequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                environment.Console.WriteLine("Could not reach Telegram; trying again in 5 s.");
                await Task.Delay(TimeSpan.FromSeconds(5), environment.Time, cancellationToken);
            }
        }
    }

    static async Task TrySayPairedAsync(LocalEnvironment environment, ITelegramBotClient client, long chatId, int messageId, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(environment.TelegramTimeout);
        try
        {
            await client.SendMessage(
                chatId,
                BridgeTexts.Paired,
                replyParameters: new ReplyParameters { MessageId = messageId, AllowSendingWithoutReply = true },
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                cancellationToken: deadline.Token);
        }
        catch (Exception exception) when (exception is RequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            environment.Console.WriteLine("(Could not send \"Paired.\" to the chat; pairing is saved anyway.)");
        }
    }

    static async Task RestartAsync(LocalEnvironment environment, CancellationToken cancellationToken)
    {
        var console = environment.Console;
        var service = environment.Service;
        if (!service.IsInstalled)
        {
            console.WriteLine("Start the bridge with ./install.sh (as a login agent), or with `firstmate-telegram run` in this terminal.");
            return;
        }

        using var store = StateStore.Open(environment.Paths);
        if (store.State.Stopped)
        {
            console.WriteLine("The bridge is stopped. Start it with `firstmate-telegram start`.");
            return;
        }

        await service.StartAsync(cancellationToken);
        console.WriteLine("Bridge restarted.");
    }

    static string ExpandHome(string path, string home) =>
        path == "~" ? home : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path[2..]) : path;
}
