using FirstmateTelegram.Bridge;
using FirstmateTelegram.Cli;
using FirstmateTelegram.Configuration;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.Telegram;
using FirstmateTelegram.Tests.Fakes;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

public sealed class SetupTests
{
    [Fact]
    public async Task Setup_pairs_exactly_the_account_confirmed_on_the_Mac_and_never_sends_the_pairing_message_to_FirstMate()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Telegram.EnqueueText(777, "hi, I am not the owner");
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "group hello", chatId: -1009, chatType: "group");
        var pairing = harness.Telegram.EnqueueText(BridgeHarness.UserId, "pair me");
        var console = new ScriptedConsole(FakeTelegramServer.Token, harness.FirstMate.Home, "n", "y");
        var service = new FakeServiceInstaller { IsInstalled = true, IsLoaded = true };

        var exitCode = await SetupCommand.RunAsync(Local(harness, console, service), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var config = ConfigFile.Load(harness.Paths);
        Assert.Equal((BridgeHarness.UserId, harness.FirstMate.Home), (config.AllowedUserId, config.FirstmateHome));
        Assert.Equal(FakeTelegramServer.Token, TokenFile.Read(harness.Paths));
        Assert.Equal([FakeTelegramServer.Token], console.Secrets);

        Assert.Contains("Message from Telegram user id 777: Ada Lovelace (@ada)", console.Output);
        Assert.Contains("Not paired. Waiting for the next message.", console.Output);
        Assert.Contains($"Message from Telegram user id {BridgeHarness.UserId}", console.Output);
        Assert.Contains($"Paired with user id {BridgeHarness.UserId}.", console.Output);
        Assert.DoesNotContain("group hello", console.Output);
        Assert.DoesNotContain(FakeTelegramServer.Token, console.Output);

        var paired = Assert.Single(harness.Telegram.SentMessages());
        Assert.Equal((BridgeHarness.UserId, BridgeTexts.Paired, pairing), (paired.ChatId, paired.Text, paired.ReplyToMessageId));
        Assert.Equal(0, harness.Telegram.PendingUpdateCount());
        Assert.Empty(harness.FirstMate.Calls("note"));
        Assert.Equal(["stop", "start"], service.Actions);
        Assert.Contains("Allow Groups", console.Output);
        Assert.Contains("data/captain.md", console.Output);
    }

    [Fact]
    public async Task Setup_asks_again_when_Telegram_rejects_the_token_or_it_is_not_token_shaped()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "pair me");
        var console = new ScriptedConsole("not a token", "123456789:AAHrevokedTokenForTests-0000000000", FakeTelegramServer.Token, harness.FirstMate.Home, "y");

        Assert.Equal(0, await SetupCommand.RunAsync(Local(harness, console, new FakeServiceInstaller()), CancellationToken.None));

        Assert.Contains("does not look like a bot token", console.Output);
        Assert.Contains("Telegram rejected this token", console.Output);
        Assert.Equal(FakeTelegramServer.Token, TokenFile.Read(harness.Paths));
        Assert.Contains("./install.sh", console.Output);
    }

    [Fact]
    public async Task Setup_keeps_the_saved_token_and_other_settings_when_rerun()
    {
        await using var harness = await BridgeHarness.StartAsync();
        TokenFile.Write(harness.Paths, FakeTelegramServer.Token);
        ConfigFile.Write(harness.Paths, new BridgeConfig { FirstmateHome = harness.FirstMate.Home, AllowedUserId = 1, RepliedReaction = "\U0001F44D", LivePingTimeoutSeconds = 90 });
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "pair me again");
        var console = new ScriptedConsole("", "", "y");

        Assert.Equal(0, await SetupCommand.RunAsync(Local(harness, console, new FakeServiceInstaller()), CancellationToken.None));

        var config = ConfigFile.Load(harness.Paths);
        Assert.Equal((BridgeHarness.UserId, "\U0001F44D", 90), (config.AllowedUserId, config.RepliedReaction, config.LivePingTimeoutSeconds));
        Assert.Contains($"FirstMate home folder [{harness.FirstMate.Home}]", console.Output);
    }

    [Fact]
    public async Task Setup_refuses_to_continue_while_a_webhook_is_set_unless_the_user_deletes_it()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Telegram.WebhookUrl = "https://example.invalid/hook";
        var declined = new ScriptedConsole(FakeTelegramServer.Token, "n");

        Assert.Equal(1, await SetupCommand.RunAsync(Local(harness, declined, new FakeServiceInstaller()), CancellationToken.None));
        Assert.Contains("Setup cannot continue while a webhook is set.", declined.Output);
        Assert.Empty(harness.Telegram.CallsTo("deleteWebhook"));

        harness.Telegram.EnqueueText(BridgeHarness.UserId, "pair me");
        var accepted = new ScriptedConsole(FakeTelegramServer.Token, "y", harness.FirstMate.Home, "y");
        Assert.Equal(0, await SetupCommand.RunAsync(Local(harness, accepted, new FakeServiceInstaller()), CancellationToken.None));
        Assert.False(Assert.Single(harness.Telegram.CallsTo("deleteWebhook"))["drop_pending_updates"]?.GetValue<bool>() ?? false);
    }

    [Fact]
    public async Task Setup_checks_the_FirstMate_home_and_asks_again_until_it_answers()
    {
        await using var harness = await BridgeHarness.StartAsync();
        using var notHome = new TempDirectory();
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "pair me");
        var console = new ScriptedConsole(FakeTelegramServer.Token, "relative/path", notHome.Path, harness.FirstMate.Home, "y");

        Assert.Equal(0, await SetupCommand.RunAsync(Local(harness, console, new FakeServiceInstaller()), CancellationToken.None));

        Assert.Contains("Enter the full path of an existing folder.", console.Output);
        Assert.Contains("that does not look like a FirstMate home", console.Output);
        Assert.Contains("FirstMate home checked: running, listening.", console.Output);
        Assert.All(harness.FirstMate.Calls("ready"), call => Assert.Equal(["ready"], call.Arguments));
    }

    [Fact]
    public async Task Setup_will_not_pair_while_another_copy_of_the_bridge_is_polling()
    {
        await using var harness = await BridgeHarness.StartAsync();
        using var running = InstanceLock.TryAcquire(harness.Paths.LockFile);
        var console = new ScriptedConsole(FakeTelegramServer.Token, harness.FirstMate.Home);

        Assert.Equal(1, await SetupCommand.RunAsync(Local(harness, console, new FakeServiceInstaller()), CancellationToken.None));

        Assert.Contains("Another copy of the bridge is running", console.Output);
        Assert.False(ConfigFile.Exists(harness.Paths));
    }

    internal static LocalEnvironment Local(BridgeHarness harness, IConsoleIO console, FakeServiceInstaller service) =>
        new(harness.Paths, console)
        {
            TelegramClients = new TelegramClientFactory(harness.Telegram.BaseUrl),
            ServiceInstaller = service,
            GetVariable = name => name == "PATH" ? System.Environment.GetEnvironmentVariable("PATH") : null,
        };
}

public sealed class DoctorTests
{
    [Fact]
    public async Task Doctor_passes_when_everything_is_in_place()
    {
        await using var harness = await BridgeHarness.StartAsync();
        RunCommandTests.WriteConfigAndToken(harness);
        using var tools = FakeTools();
        var console = new ScriptedConsole();

        var exitCode = await DoctorCommand.RunAsync(Local(harness, console, tools, new FakeServiceInstaller { IsInstalled = true, IsLoaded = true }), CancellationToken.None);

        Assert.True(exitCode == 0, console.Output);
        Assert.Contains($"bot token: accepted for @{FakeTelegramServer.BotUsername}", console.Output);
        Assert.Contains("FirstMate ready: fm-primary-ready.v1: running, listening, posture present", console.Output);
        Assert.Contains("FirstMate receipts: fm-inbox-receipts.v1", console.Output);
        Assert.Contains("Everything checks out.", console.Output);
        Assert.All(harness.Telegram.CallsTo("getUpdates"), call => Assert.Null(call["offset"]));
    }

    [Fact]
    public async Task Doctor_names_each_problem_and_how_to_fix_it()
    {
        await using var harness = await BridgeHarness.StartAsync();
        harness.Config = harness.Config with { FirstmateHome = "/nonexistent/firstmate" };
        RunCommandTests.WriteConfigAndToken(harness);
        harness.Telegram.WebhookUrl = "https://example.invalid/hook";
        using var tools = new TempDirectory();
        var console = new ScriptedConsole();

        var exitCode = await DoctorCommand.RunAsync(Local(harness, console, tools, new FakeServiceInstaller()), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("PROBLEM  webhook: a webhook is set", console.Output);
        Assert.Contains("PROBLEM  FirstMate home: /nonexistent/firstmate/bin/fm-inbox.sh is missing", console.Output);
        Assert.Contains("PROBLEM  tool jq: not found on PATH", console.Output);
        Assert.Contains("PROBLEM  login agent: not installed. Run ./install.sh", console.Output);
    }

    [Fact]
    public async Task Doctor_reports_a_rejected_token_and_a_set_stopped_flag()
    {
        await using var harness = await BridgeHarness.StartAsync();
        RunCommandTests.WriteConfigAndToken(harness);
        harness.Telegram.AcceptedToken = "1:other";
        using (var store = FirstmateTelegram.State.StateStore.Open(harness.Paths))
            await store.UpdateStateAsync(state => state with { Stopped = true });
        using var tools = FakeTools();
        var console = new ScriptedConsole();

        Assert.Equal(1, await DoctorCommand.RunAsync(Local(harness, console, tools, new FakeServiceInstaller { IsInstalled = true, IsLoaded = true }), CancellationToken.None));

        Assert.Contains("PROBLEM  bot token: Telegram rejected the token", console.Output);
        Assert.Contains("PROBLEM  stopped flag: set", console.Output);
    }

    static TempDirectory FakeTools()
    {
        var directory = new TempDirectory();
        foreach (var tool in new[] { "bash", "python3", "jq" })
        {
            var path = directory.Combine(tool);
            File.WriteAllText(path, "#!/bin/sh\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return directory;
    }

    static LocalEnvironment Local(BridgeHarness harness, IConsoleIO console, TempDirectory tools, FakeServiceInstaller service) =>
        new(harness.Paths, console)
        {
            TelegramClients = new TelegramClientFactory(harness.Telegram.BaseUrl),
            ServiceInstaller = service,
            GetVariable = name => name == "PATH" ? tools.Path : null,
        };
}
