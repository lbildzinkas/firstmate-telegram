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

    // /mute and /unmute (spec 4.3.4).

    public const string MuteUsage = "Mute for how long? Give a duration like 30m, 2h or 1d, at most 7d; plain /mute means one hour.";

    public static string MutedUntil(string until) =>
        string.Create(CultureInfo.InvariantCulture, $"Alerts are silent until {until}.");

    public const string Unmuted = "Alerts are back on.";

    // /back (spec 4.3.6).

    public const string AwayNotOn = "Away mode is not on.";

    public const string QuietModeNotAway = "FirstMate is in quiet mode, not away mode. Quiet mode ends only at the terminal.";

    public const string BackNeedsNewerFirstMate = "Away mode is still on. Ending it from Telegram needs a newer FirstMate; end it at the terminal for now.";

    public const string BackSaved = "Return saved. FirstMate will run its return and answer here.";

    // /stop (spec 4.3.7).

    public const string Stopped = "Bridge stopped. Start it again on the Mac with `firstmate-telegram start`.";

    // /help (spec 4.3.5).

    public const string Help =
        "/status \u2013 FirstMate at a glance, from its records\n"
        + "/ping \u2013 availability check; /ping live asks FirstMate itself\n"
        + "/mute <duration> \u2013 alerts silent for a while (30m, 2h, 1d; plain means 1h)\n"
        + "/unmute \u2013 alerts back on\n"
        + "/back \u2013 return from away mode\n"
        + "/stop \u2013 stop the bridge until it is started on the Mac\n"
        + "/help \u2013 this list\n"
        + "Anything else is sent to FirstMate as a request.";

    // The command list the bridge registers with Telegram for the user's chat (spec 4.3).

    public static readonly (string Command, string Description)[] TelegramCommands =
    [
        ("status", "FirstMate at a glance, from its records"),
        ("ping", "Availability check; 'live' asks FirstMate itself"),
        ("mute", "Alerts silent for a while"),
        ("unmute", "Alerts back on"),
        ("back", "Return from away mode"),
        ("stop", "Stop the bridge until started on the Mac"),
        ("help", "List the commands"),
    ];

    // Alerts (spec 4.4).

    public static string PrReadyAlert(string title, string url) =>
        string.Create(CultureInfo.InvariantCulture, $"Ready for your review: {title} {url}");

    public const string PrReadyPrivateAlert = "A PR in a private project is ready for your review.";

    public static string ResearchFinishedAlert(string title) =>
        string.Create(CultureInfo.InvariantCulture, $"Research finished: {title}. Reply to this message to get the findings.");

    public const string ResearchFinishedPrivateAlert = "Research in a private project is finished. Reply to this message to get the findings.";

    public static string DecisionAlert(string title, string reason) =>
        string.Create(CultureInfo.InvariantCulture, $"Decision needed: {title}. {reason}. Reply to this message with your answer.");

    public const string DecisionPrivateAlert = "A decision is needed in a private project. Reply to this message with your answer.";

    public static string WaitingOnDecisionAlert(string title, string status) =>
        string.Create(CultureInfo.InvariantCulture, $"Waiting on a decision: {title}. {status}");

    public const string WaitingOnDecisionPrivateAlert = "A task in a private project is waiting on a decision.";

    public static string BlockedAlert(string title, string status) =>
        string.Create(CultureInfo.InvariantCulture, $"Blocked: {title}. {status}");
    public const string BlockedPrivateAlert = "A task in a private project is blocked.";

    public static string FailedAlert(string title, string status) =>
        string.Create(CultureInfo.InvariantCulture, $"Failed: {title}. {status}");

    public const string FailedPrivateAlert = "A task in a private project failed.";

    // Availability alerts (spec 4.4.1).

    public const string AwayStopped = "FirstMate stopped while you are away. Requests will queue until it starts.";

    public const string AwayNotPickingUp = "FirstMate is running but not picking up requests.";

    public static string AwayNotResponding(int minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"FirstMate has not picked up a request for {minutes} min.");

    public static string AwayQuotaOut(string provider, string until) =>
        string.Create(CultureInfo.InvariantCulture, $"{provider} quota is out until {until}. FirstMate can't work until then.");

    public const string AwayQuotaOutNoReset = "FirstMate can't work: the model quota is out.";

    public static string AvailableAgain(string was, TimeSpan forHowLong) =>
        string.Create(CultureInfo.InvariantCulture, $"FirstMate is available again (was {was} for {Durations.Format(forHowLong)}).");
}
