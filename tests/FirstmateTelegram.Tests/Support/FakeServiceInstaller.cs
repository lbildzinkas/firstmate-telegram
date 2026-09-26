using FirstmateTelegram.Service;

namespace FirstmateTelegram.Tests.Support;

/// <summary>Stands in for the login agent in command tests; the launchd installer has its own tests.</summary>
public sealed class FakeServiceInstaller : IServiceInstaller
{
    public bool IsInstalled { get; set; }

    public bool IsLoaded { get; set; }

    public List<string> Actions { get; } = [];

    public Task<bool> IsLoadedAsync(CancellationToken cancellationToken) => Task.FromResult(IsLoaded);

    public Task InstallAsync(string firstmateHome, CancellationToken cancellationToken)
    {
        Actions.Add($"install {firstmateHome}");
        IsInstalled = IsLoaded = true;
        return Task.CompletedTask;
    }

    public Task UninstallAsync(CancellationToken cancellationToken)
    {
        Actions.Add("uninstall");
        IsInstalled = IsLoaded = false;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Actions.Add("start");
        IsLoaded = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Actions.Add("stop");
        IsLoaded = false;
        return Task.CompletedTask;
    }
}
