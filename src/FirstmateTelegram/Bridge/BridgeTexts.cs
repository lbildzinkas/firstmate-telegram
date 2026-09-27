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

    // /ping and the availability model behind it (spec 4.3.2, 7.5).

    public const string VerdictNotRunning = "Not running; requests will queue";

    public const string VerdictNotPickingUp = "Running, but not picking up requests; requests will queue";

    public const string VerdictUnknown = "Unknown: could not confirm FirstMate is running";

    public const string VerdictProbablyReady = "Probably ready; listening could not be confirmed";

    public const string VerdictReady = "Ready";

    public static string VerdictQuotaOut(string provider, string until) =>
        string.Create(CultureInfo.InvariantCulture, $"Running, but {provider} quota is out until {until}");

    public static string VerdictNotResponding(int minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"Running, but not responding: a request has waited {minutes} min");

    // /ping live (spec 4.3.3).

    public const string NoLiveCheckSent = "No live check was sent, so FirstMate won't answer a stale ping later.";

    public const string AwayModePickupNote = "In away mode, FirstMate can take a couple of minutes to pick up a request.";

    /// <summary>The note body of a live ping: a real FirstMate turn that must do nothing but answer.</summary>
    public const string LivePingBody = "Live availability check from firstmate-telegram: reply with one short line and take no other action.";

    public static string AskingFirstMate(int seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"Asking FirstMate directly (up to {seconds} s).");

    public static string LivePingNotAvailable(int seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"Not available: FirstMate did not answer within {seconds} s.");

    public static string LivePingAnswered(TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"Live: FirstMate answered in {Durations.Format(elapsed)}.");

    public static string LivePingLateAnswer(TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"FirstMate answered the live ping after {Durations.Format(elapsed)}.");

    // /status (spec 4.3.1, 7.2.5).

    public static string RecordsUnreadable(string reason) =>
        string.Create(CultureInfo.InvariantCulture, $"Could not read FirstMate's records: {reason}");

    public static string NotAvailableYet(string command) =>
        string.Create(CultureInfo.InvariantCulture, $"/{command} isn't available yet in this version of firstmate-telegram.");

    public static string StillTrying(string reason) =>
        string.Create(CultureInfo.InvariantCulture, $"Couldn't save your request in FirstMate's inbox yet ({reason}); still trying.");
}
