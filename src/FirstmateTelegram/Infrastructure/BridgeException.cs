namespace FirstmateTelegram.Infrastructure;

/// <summary>
/// An error the user can act on. Its message is printed as-is, so it says what is wrong and how to fix it.
/// </summary>
public sealed class BridgeException : Exception
{
    public BridgeException(string message)
        : base(message)
    {
    }

    public BridgeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BridgeException()
    {
    }
}
