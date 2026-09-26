using System.Text;

namespace FirstmateTelegram.Cli;

/// <summary>The terminal, for the interactive commands. Tests script it.</summary>
public interface IConsoleIO
{
    void WriteLine(string text = "");

    void Write(string text);

    /// <summary>Reads a line, or null at the end of input.</summary>
    string? ReadLine();

    /// <summary>Reads a line without echoing it, for the bot token.</summary>
    string? ReadSecret();
}

public sealed class SystemConsoleIO : IConsoleIO
{
    public void WriteLine(string text = "") => Console.WriteLine(text);

    public void Write(string text) => Console.Write(text);

    public string? ReadLine() => Console.ReadLine();

    public string? ReadSecret()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                    secret.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar))
                secret.Append(key.KeyChar);
        }

        Console.WriteLine();
        return secret.ToString();
    }
}

public static class ConsolePrompts
{
    /// <summary>Asks a yes/no question where anything but an explicit yes means no.</summary>
    public static bool Confirm(this IConsoleIO console, string question)
    {
        console.Write($"{question} [y/N] ");
        var answer = console.ReadLine()?.Trim();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
