using System.Globalization;
using System.Text;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Internal;

/// <summary>
///     Formats <see cref="TimeSpan" /> values as deterministic PostgreSQL interval literals,
///     e.g. <c>"7 days"</c>, <c>"01:30:00"</c>, <c>"2 days 04:00:00"</c>.
/// </summary>
public static class PgInterval
{
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
