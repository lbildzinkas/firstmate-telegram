using System.Globalization;

namespace FirstmateTelegram.Bridge;

/// <summary>
/// The durations <c>/mute</c> takes (spec 4.3.4): <c>30m</c>, <c>2h</c>, <c>1d</c> or combinations such as
/// <c>1h30m</c>. A plain <c>/mute</c> means one hour, and the maximum is 7 days.
/// </summary>
public static class MuteDurations
{
    public static readonly TimeSpan Default = TimeSpan.FromHours(1);
    public static readonly TimeSpan Maximum = TimeSpan.FromDays(7);

    /// <summary>The duration the arguments ask for, or null when they do not name one within the maximum.</summary>
    public static TimeSpan? Parse(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return Default;

        var total = TimeSpan.Zero;
        var position = 0;
        while (position < arguments.Length)
        {
            var digits = position;
            while (digits < arguments.Length && char.IsAsciiDigit(arguments[digits]))
                digits++;
            if (digits == position || digits >= arguments.Length)
                return null;

            var unit = char.ToLowerInvariant(arguments[digits]);
            var length = unit switch
            {
                'm' => TimeSpan.FromMinutes(1),
                'h' => TimeSpan.FromHours(1),
                'd' => TimeSpan.FromDays(1),
                _ => (TimeSpan?)null,
            };
            if (length is null)
                return null;

            if (!int.TryParse(arguments[position..digits], CultureInfo.InvariantCulture, out var amount) || amount <= 0)
                return null;
            if (amount > Maximum.Ticks / length.Value.Ticks || total + length.Value * amount > Maximum)
                return null;
            total += length.Value * amount;
            position = digits + 1;
        }

        return total;
    }
}
