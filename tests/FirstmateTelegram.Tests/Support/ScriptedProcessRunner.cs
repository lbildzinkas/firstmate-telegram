using FirstmateTelegram.Infrastructure;

namespace FirstmateTelegram.Tests.Support;

/// <summary>An injected runner that records each request and answers from a script, for commands such as launchctl.</summary>
public sealed class ScriptedProcessRunner : IProcessRunner
{
    readonly Func<ProcessRequest, ProcessResult> _answer;

    public ScriptedProcessRunner(Func<ProcessRequest, ProcessResult> answer) => _answer = answer;

    public List<ProcessRequest> Requests { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        lock (Requests)
            Requests.Add(request);
        return Task.FromResult(_answer(request));
    }

    public IEnumerable<string> CommandLines()
    {
        lock (Requests)
            return Requests.Select(request => string.Join(' ', [request.FileName, .. request.Arguments])).ToList();
    }
}
