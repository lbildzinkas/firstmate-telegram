namespace FirstmateTelegram.FirstMate;

/// <summary>Running and listening, read from <c>fm-inbox.sh ready</c>.</summary>
public enum Readiness
{
    Unknown,
    NotRunning,
    NotPickingUp,
    RunningListeningUnconfirmed,
    RunningListening,
}

public static class ReadinessRules
{
    public static Readiness Classify(ReadyResult result)
    {
        if (result.Reading is not { } reading)
            return Readiness.Unknown;

        return reading.LockState switch
        {
            "free" or "stale" => Readiness.NotRunning,
            "held" when reading.WakeConsumerState == "down" || reading.CanReceive == false => Readiness.NotPickingUp,
            "held" when reading.CanReceive == true => Readiness.RunningListening,
            "held" => Readiness.RunningListeningUnconfirmed,
            _ => Readiness.Unknown,
        };
    }
}
