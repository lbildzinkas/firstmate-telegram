using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;

namespace FirstmateTelegram.Infrastructure;

/// <summary>
/// A program to run directly, never through a shell: arguments are passed as a list and
/// <see cref="StandardInput"/> carries any text, so nothing is interpreted and nothing shows in the process list.
/// </summary>
public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout)
{
    public string? StandardInput { get; init; }

    /// <summary>Variables to set for the child; a null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = ImmutableDictionary<string, string?>.Empty;
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool TimedOut { get; init; }

    /// <summary>Why the program could not be started, such as a missing file; null when it ran.</summary>
    public string? StartFailure { get; init; }

    public bool Ran => !TimedOut && StartFailure is null;

    public static ProcessResult NotStarted(string reason) => new(-1, "", "") { StartFailure = reason };

    public static ProcessResult Timeout() => new(-1, "", "") { TimedOut = true };
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    static readonly TimeSpan DrainAfterKill = TimeSpan.FromSeconds(2);

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(request) };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return ProcessResult.NotStarted(exception.Message);
        }

        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout);
        try
        {
            await WriteInputAsync(process, request.StandardInput, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await DrainAsync(output, error);
            cancellationToken.ThrowIfCancellationRequested();
            return ProcessResult.Timeout();
        }

        return new ProcessResult(process.ExitCode, await output, await error);
    }

    static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = PrivateFiles.Utf8,
            StandardOutputEncoding = PrivateFiles.Utf8,
            StandardErrorEncoding = PrivateFiles.Utf8,
        };
        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in request.Environment)
        {
            if (value is null)
                startInfo.Environment.Remove(name);
            else
                startInfo.Environment[name] = value;
        }

        return startInfo;
    }

    static async Task WriteInputAsync(Process process, string? input, CancellationToken cancellationToken)
    {
        try
        {
            if (input is not null)
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The program exited without reading its input; its exit status tells the rest.
        }
    }

    static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    static async Task DrainAsync(Task<string> output, Task<string> error)
    {
        try
        {
            await Task.WhenAll(output, error).WaitAsync(DrainAfterKill);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException)
        {
            // A detached grandchild still holds the pipes, or they broke; the result is a timeout either way.
        }
    }
}
