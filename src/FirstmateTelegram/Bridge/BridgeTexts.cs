using System.Globalization;

namespace FirstmateTelegram.Bridge;

/// <summary>Everything the bridge itself says in the chat.</summary>
public static class BridgeTexts
{
    public const string OnlyText = "Only text messages are supported for now.";

    public const string QueuedNotRunning = "FirstMate isn't running right now. Your request is saved and it will see it when it starts.";

    public const string QueuedNotPickingUp = "FirstMate is running but not picking up requests right now. Your request is saved and it will see it when it picks requests up again.";

    public const string Paired = "Paired.";

    public const string EmptyReply = "(FirstMate sent an empty reply.)";

    public static string NotAvailableYet(string command) =>
        string.Create(CultureInfo.InvariantCulture, $"/{command} isn't available yet in this version of firstmate-telegram.");

    public static string StillTrying(string reason) =>
        string.Create(CultureInfo.InvariantCulture, $"Couldn't save your request in FirstMate's inbox yet ({reason}); still trying.");
}
