namespace FirstmateTelegram.Infrastructure;

public static class Backoff
{
    /// <summary>The delay before retry number <paramref name="failures"/>: <paramref name="initial"/>, doubling, capped at <paramref name="cap"/>.</summary>
    public static TimeSpan Exponential(int failures, TimeSpan initial, TimeSpan cap)
    {
        if (failures < 1)
            return TimeSpan.Zero;

        var exponent = Math.Min(failures - 1, 30);
        var delay = initial * Math.Pow(2, exponent);
        return delay < cap ? delay : cap;
    }

    /// <summary>A random delay between half of <paramref name="delay"/> and all of it, so retries from several callers spread out.</summary>
    public static TimeSpan Jittered(TimeSpan delay, double randomUnit) => delay * (0.5 + (0.5 * Math.Clamp(randomUnit, 0, 1)));
}
