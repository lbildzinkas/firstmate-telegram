namespace FirstmateTelegram.Infrastructure;

/// <summary>Lets one loop cut its wait short when another component has new work for it.</summary>
public sealed class WakeSignal : IDisposable
{
    readonly SemaphoreSlim _signal = new(0, 1);

    public void Set()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already set; one pending wake is enough.
        }
    }

    /// <summary>Waits for <paramref name="timeout"/> or until <see cref="Set"/> is called, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, time, wait.Token);
        var signal = _signal.WaitAsync(wait.Token);
        await Task.WhenAny(delay, signal);
        await wait.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose() => _signal.Dispose();
}
