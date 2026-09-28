using FirstmateTelegram.Cli;
using FirstmateTelegram.Configuration;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.State;
using FirstmateTelegram.Telegram;
using FirstmateTelegram.Tests.Fakes;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

public sealed class RunCommandTests
{
    [Fact]
    public async Task Run_hosts_the_bridge_end_to_end_and_exits_0_when_stopped_by_a_signal()
    {
        await using var harness = await BridgeHarness.StartAsync();
        WriteConfigAndToken(harness);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = RunCommand.RunAsync(Environment(harness), cancel.Token);

        var messageId = harness.Telegram.EnqueueText(BridgeHarness.UserId, "end to end request text");
        var noteId = await Eventually(() => harness.FirstMate.NoteIds().SingleOrDefault());
        harness.FirstMate.Reply(noteId, "end to end reply text");
        var reply = await Eventually(() => harness.Telegram.SentMessages().SingleOrDefault());
        await Eventually(() => harness.Telegram.Reactions().Count == 2 ? "done" : null);
        await cancel.CancelAsync();

        Assert.Equal(0, await run);
        Assert.Equal(("end to end reply text", messageId), (reply.Text, reply.ReplyToMessageId));
        var log = await File.ReadAllTextAsync(harness.Paths.LogFile);
        Assert.Contains("bridge starting", log);
        Assert.Contains($"saved message {messageId} as note {noteId}", log);
        Assert.DoesNotContain("end to end", log);
        Assert.DoesNotContain(FakeTelegramServer.Token.Split(':')[1], log);
        using var store = StateStore.Open(harness.Paths);
        Assert.Equal(FakeTelegramServer.BotId, store.State.BotId);
        Assert.NotEmpty(store.State.ReplyCursor);
    }

    // The state file a bridge from before the alert watchers wrote has no settles member, and the first upgraded run
    // wrote it back as null; the alert watcher's first pass must read both as no settle watches, not stop the bridge.
    [Theory]
    [InlineData("")]
    [InlineData(""","settles":null""")]
    public async Task Run_keeps_going_on_a_state_file_written_before_the_alert_watchers(string settlesMember)
    {
        await using var harness = await BridgeHarness.StartAsync();
        WriteConfigAndToken(harness);
        Directory.CreateDirectory(harness.Paths.StateDirectory);
        await File.WriteAllTextAsync(
            harness.Paths.StateFile,
            $$"""{"schema":"firstmate-telegram.state.v1","bot_id":{{FakeTelegramServer.BotId}},"update_offset":0,"handled_update_ids":[],"reply_cursor":"","stopped":false{{settlesMember}}}""");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = RunCommand.RunAsync(Environment(harness), cancel.Token);

        // The alert watcher's first pass reads the fleet snapshot, then checks its settle watches.
        await Eventually(() => run.IsCompleted || harness.FirstMate.SnapshotCalls("fleet").Count > 0 ? "read" : null);
        harness.Telegram.EnqueueText(BridgeHarness.UserId, "request after an upgrade");
        await Eventually(() => run.IsCompleted ? "stopped" : harness.FirstMate.NoteIds().SingleOrDefault());
        await cancel.CancelAsync();
        var exitCode = await run;

        Assert.DoesNotContain("BackgroundService failed", await File.ReadAllTextAsync(harness.Paths.LogFile));
        Assert.Equal(0, exitCode);
        Assert.Contains("\"settles\": []", await File.ReadAllTextAsync(harness.Paths.StateFile));
    }

    [Fact]
    public async Task While_the_stopped_flag_is_set_run_exits_0_at_once_without_polling()
    {
        await using var harness = await BridgeHarness.StartAsync();
        WriteConfigAndToken(harness);
        using (var store = StateStore.Open(harness.Paths))
            await store.UpdateStateAsync(state => state with { Stopped = true });
        var output = new StringWriter();

        var exitCode = await RunCommand.RunAsync(Environment(harness) with { Output = output });

        Assert.Equal(0, exitCode);
        Assert.Contains("firstmate-telegram start", output.ToString());
        Assert.Empty(harness.Telegram.Methods());
    }

    [Fact]
    public async Task An_invalid_configuration_is_refused_with_a_clear_message()
    {
        await using var harness = await BridgeHarness.StartAsync();
        WriteConfigAndToken(harness);
        await File.WriteAllTextAsync(harness.Paths.ConfigFile, """{"schema":"firstmate-telegram.config.v1","firstmate_home":"/h","allowed_user_id":1,"replied_reaction":"✅"}""");
        var error = new StringWriter();

        var exitCode = await RunCommand.RunAsync(Environment(harness) with { Error = error });

        Assert.Equal(1, exitCode);
        Assert.Contains("\"replied_reaction\" must be one of", error.ToString());
        Assert.Empty(harness.Telegram.Methods());
    }

    [Fact]
    public async Task A_token_file_readable_by_others_is_refused()
    {
        await using var harness = await BridgeHarness.StartAsync();
        WriteConfigAndToken(harness);
        File.SetUnixFileMode(harness.Paths.TokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var error = new StringWriter();

        Assert.Equal(1, await RunCommand.RunAsync(Environment(harness) with { Error = error }));
        Assert.Contains("chmod 600", error.ToString());
    }

    [Fact]
    public async Task A_second_copy_is_refused_while_the_first_holds_the_lock()
    {
        await using var harness = await BridgeHarness.StartAsync();
        WriteConfigAndToken(harness);
        using var first = InstanceLock.TryAcquire(harness.Paths.LockFile);
        var error = new StringWriter();

        Assert.Equal(1, await RunCommand.RunAsync(Environment(harness) with { Error = error }));
        Assert.Contains("Another copy of the bridge is already running", error.ToString());
    }

    [Fact]
    public async Task A_failing_background_service_exits_non_zero_so_launchd_restarts_the_bridge()
    {
        await using var harness = await BridgeHarness.StartAsync();
        WriteConfigAndToken(harness);
        Directory.CreateDirectory(harness.Paths.StateDirectory);
        await File.WriteAllTextAsync(harness.Paths.LockFile, "");
        File.SetUnixFileMode(harness.Paths.StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Assert.Equal(1, await RunCommand.RunAsync(Environment(harness), cancel.Token));
        }
        finally
        {
            File.SetUnixFileMode(harness.Paths.StateDirectory, PrivateFiles.OwnerOnlyDirectory);
        }
    }

    internal static void WriteConfigAndToken(BridgeHarness harness)
    {
        ConfigFile.Write(harness.Paths, harness.Config);
        TokenFile.Write(harness.Paths, FakeTelegramServer.Token);
    }

    static RunEnvironment Environment(BridgeHarness harness) => new(harness.Paths)
    {
        TelegramClients = new TelegramClientFactory(harness.Telegram.BaseUrl),
        Output = TextWriter.Null,
        Error = TextWriter.Null,
    };

    static async Task<T> Eventually<T>(Func<T?> probe)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            if (probe() is { } value)
                return value;
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("the bridge did not get there in time");
            await Task.Delay(50);
        }
    }
}

public sealed class StartStopTests
{
    [Fact]
    public async Task Stop_unloads_the_agent_and_sets_the_stopped_flag()
    {
        using var directory = new TempDirectory();
        var service = new FakeServiceInstaller { IsInstalled = true, IsLoaded = true };
        var environment = Local(directory, service);

        Assert.Equal(0, await ServiceCommands.StopAsync(environment, CancellationToken.None));

        Assert.Equal(["stop"], service.Actions);
        using var store = StateStore.Open(environment.Paths);
        Assert.True(store.State.Stopped);
    }

    [Fact]
    public async Task Stop_cannot_set_the_flag_under_a_bridge_running_outside_the_agent()
    {
        using var directory = new TempDirectory();
        var environment = Local(directory, new FakeServiceInstaller());
        using var running = InstanceLock.TryAcquire(environment.Paths.LockFile);

        Assert.Equal(1, await ServiceCommands.StopAsync(environment, CancellationToken.None));

        Assert.Contains("still running outside the login agent", ((ScriptedConsole)environment.Console).Output);
        using var store = StateStore.Open(environment.Paths);
        Assert.False(store.State.Stopped);
    }

    [Fact]
    public async Task Start_clears_the_stopped_flag_and_starts_the_agent()
    {
        using var directory = new TempDirectory();
        var service = new FakeServiceInstaller { IsInstalled = true };
        var environment = Local(directory, service);
        using (var store = StateStore.Open(environment.Paths))
            await store.UpdateStateAsync(state => state with { Stopped = true, UpdateOffset = 77 });

        Assert.Equal(0, await ServiceCommands.StartAsync(environment, CancellationToken.None));

        Assert.Equal(["start"], service.Actions);
        using var reopened = StateStore.Open(environment.Paths);
        Assert.False(reopened.State.Stopped);
        Assert.Equal(77, reopened.State.UpdateOffset);
    }

    [Fact]
    public async Task Service_install_takes_FirstMates_home_from_the_configuration()
    {
        using var directory = new TempDirectory();
        var service = new FakeServiceInstaller();
        var environment = Local(directory, service);
        ConfigFile.Write(environment.Paths, new BridgeConfig { FirstmateHome = "/Users/me/firstmate", AllowedUserId = 42 });

        Assert.Equal(0, await ServiceCommands.InstallAsync(environment, CancellationToken.None));

        Assert.Equal(["install /Users/me/firstmate"], service.Actions);
    }

    static LocalEnvironment Local(TempDirectory directory, FakeServiceInstaller service) =>
        new(BridgePaths.FromEnvironment(name => name == "HOME" ? directory.Path : null), new ScriptedConsole())
        {
            ServiceInstaller = service,
            StopWait = TimeSpan.FromMilliseconds(200),
        };
}
