using System.Globalization;
using System.Text;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Internal;

/// <summary>
///     Formats interval values as deterministic PostgreSQL interval literals, e.g. <c>"7 days"</c>,
///     <c>"1 month"</c>, <c>"01:30:00"</c>. Both the <see cref="TimeSpan" /> (Fluent) and the
///     <c>(value, <see cref="Every" />)</c> (attributes / Fluent) forms canonicalize here so the
///     migration engine sees identical strings regardless of how the user expressed them.
/// </summary>
public static class PgInterval
{
    /// <summary>Formats a <c>(value, unit)</c> pair, e.g. <c>(1, Every.Month)</c> → <c>"1 month"</c>.</summary>
    public static string Format(long value, Every unit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        var word = unit switch
        {
            Every.Second => "second",
            Every.Minute => "minute",
            Every.Hour => "hour",
            Every.Day => "day",
            Every.Week => "week",
            Every.Month => "month",
            Every.Year => "year",
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
        };

        return value == 1
            ? $"1 {word}"
            : $"{value.ToString(CultureInfo.InvariantCulture)} {word}s";
    }

    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, "TimescaleDB intervals must be non-negative.");
        }

        var builder = new StringBuilder();

        if (value.Days > 0)
        {
            builder.Append(value.Days.ToString(CultureInfo.InvariantCulture));
            builder.Append(value.Days == 1 ? " day" : " days");
        }

        var timePart = value - TimeSpan.FromDays(value.Days);
        if (timePart > TimeSpan.Zero || value == TimeSpan.Zero)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(timePart.ToString(
                timePart.Milliseconds > 0 || timePart.Microseconds > 0 ? @"hh\:mm\:ss\.FFFFFF" : @"hh\:mm\:ss",
                CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
