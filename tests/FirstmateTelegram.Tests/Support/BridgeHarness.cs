using FirstmateTelegram.Bridge;
using FirstmateTelegram.Configuration;
using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.State;
using FirstmateTelegram.Telegram;
using FirstmateTelegram.Tests.Fakes;

namespace FirstmateTelegram.Tests.Support;

/// <summary>
/// A bridge wired to the fake Telegram server and the fake FirstMate home, with its own state folders under a
/// test HOME. Each <see cref="StartInstance"/> is one bridge process; starting another over the same folders is a restart.
/// </summary>
public sealed class BridgeHarness : IAsyncDisposable
{
    public const long UserId = 42;

    readonly TempDirectory _root = new();
    readonly List<BridgeInstance> _instances = [];

    BridgeHarness(FakeTelegramServer telegram)
    {
        Telegram = telegram;
        Paths = BridgePaths.FromEnvironment(name => name == "HOME" ? _root.Combine("home") : null);
        Config = new BridgeConfig { FirstmateHome = FirstMate.Home, AllowedUserId = UserId };
        Logging = new TestLogging(Paths.LogFile);
    }

    public FakeTelegramServer Telegram { get; }

    public FakeFirstMateHome FirstMate { get; } = new();

    public BridgePaths Paths { get; }

    public BridgeConfig Config { get; set; }

    public TestLogging Logging { get; }

    public FirstMateTimeouts Timeouts { get; set; } = new();

    public TelegramRetryOptions Retry { get; set; } = new()
    {
        InitialBackoff = TimeSpan.FromMilliseconds(5),
        MaxBackoff = TimeSpan.FromMilliseconds(20),
        ConflictBackoff = TimeSpan.FromMilliseconds(20),
        TokenRejectedRecheck = TimeSpan.FromMilliseconds(50),
    };

    public static async Task<BridgeHarness> StartAsync() => new(await FakeTelegramServer.StartAsync());

    public BridgeInstance StartInstance(IStateWriter? writer = null, TimeProvider? time = null)
    {
        var instance = new BridgeInstance(this, writer, time ?? TimeProvider.System);
        _instances.Add(instance);
        return instance;
    }

    public static string RequestIdFor(int messageId) => RequestIds.For(FakeTelegramServer.BotId, UserId, messageId);

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _instances)
            instance.Dispose();
        Logging.Dispose();
        FirstMate.Dispose();
        await Telegram.DisposeAsync();
        _root.Dispose();
    }
}

public sealed class BridgeInstance : IDisposable
{
    readonly WakeSignal _replyWaiting = new();

    internal BridgeInstance(BridgeHarness harness, IStateWriter? writer, TimeProvider time)
    {
        Store = StateStore.Open(harness.Paths, writer);
        var client = new TelegramClientFactory(harness.Telegram.BaseUrl).Create(FakeTelegramServer.Token);
        Gateway = new TelegramGateway(client, time, harness.Logging.For<TelegramGateway>(), harness.Retry, random: () => 0.5);
        var firstMate = new FirstMateClient(new SystemProcessRunner(), harness.Config.FirstmateHome, harness.Timeouts);
        Submitter = new RequestSubmitter(firstMate, Gateway, Store, _replyWaiting, time, harness.Logging.For<RequestSubmitter>());
        Poller = new UpdatePoller(
            Gateway,
            Store,
            new AccessGate(harness.Config.AllowedUserId, harness.Logging.For<AccessGate>()),
            new CommandRouter(Gateway),
            Submitter,
            time,
            harness.Logging.For<UpdatePoller>());
        Forwarder = new ReplyForwarder(firstMate, Gateway, Store, _replyWaiting, harness.Config.RepliedReaction, time, harness.Logging.For<ReplyForwarder>());
    }

    public StateStore Store { get; }

    public TelegramGateway Gateway { get; }

    public RequestSubmitter Submitter { get; }

    public UpdatePoller Poller { get; }

    public ReplyForwarder Forwarder { get; }

    public async Task<BridgeInstance> InitializedAsync()
    {
        await Poller.InitializeAsync(CancellationToken.None);
        return this;
    }

    public Task PollAsync() => Poller.PollOnceAsync(CancellationToken.None);

    public Task ForwardAsync() => Forwarder.ForwardOnceAsync(CancellationToken.None);

    public void Dispose()
    {
        Poller.Dispose();
        Forwarder.Dispose();
        Store.Dispose();
        _replyWaiting.Dispose();
    }
}

/// <summary>A state writer that simulates the process dying just before a chosen write, once it is armed.</summary>
public sealed class CrashingStateWriter : IStateWriter
{
    readonly AtomicStateWriter _inner = new();
    string? _file;
    int _skip;

    /// <summary>Crashes on the write to <paramref name="fileName"/> after <paramref name="skip"/> successful ones.</summary>
    public void CrashOn(string fileName, int skip = 0)
    {
        _file = fileName;
        _skip = skip;
    }

    public void Write(string path, string contents)
    {
        if (_file == Path.GetFileName(path))
        {
            if (_skip == 0)
            {
                _file = null;
                throw new SimulatedCrashException($"simulated crash before writing {Path.GetFileName(path)}");
            }

            _skip--;
        }

        _inner.Write(path, contents);
    }
}

public sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException(string message)
        : base(message)
    {
    }

    public SimulatedCrashException()
    {
    }

    public SimulatedCrashException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
