using System.Text;
using FirstmateTelegram.Cli;

namespace FirstmateTelegram.Tests.Support;

/// <summary>A terminal that answers prompts from a script and keeps everything written to it.</summary>
public sealed class ScriptedConsole : IConsoleIO
{
    readonly Queue<string> _answers;
    readonly StringBuilder _output = new();

    public ScriptedConsole(params string[] answers) => _answers = new Queue<string>(answers);

    public string Output
    {
        get
        {
            lock (_output)
                return _output.ToString();
        }
    }

    public List<string> Secrets { get; } = [];

    public void WriteLine(string text = "")
    {
        lock (_output)
            _output.Append(text).Append('\n');
    }

    public void Write(string text)
    {
        lock (_output)
            _output.Append(text);
    }

    public string? ReadLine() => _answers.TryDequeue(out var answer) ? answer : null;

    public string? ReadSecret()
    {
        var secret = ReadLine();
        if (secret is not null)
            Secrets.Add(secret);
        return secret;
    }
}
