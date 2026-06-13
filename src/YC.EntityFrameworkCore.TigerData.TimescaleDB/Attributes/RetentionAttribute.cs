namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     Adds a data retention policy (<c>add_retention_policy</c>) that <b>automatically drops chunks</b>
///     older than the configured age. Requires a <see cref="PartitionColumnAttribute" /> on the class.
/// </summary>
/// <remarks>
///     The policy deletes whole chunks (fast, irreversible) — make sure the age is safely beyond any
///     window you still query. For an <b>integer</b>-partitioned hypertable an integer-now function is
///     required (configure it via the Fluent API). Equivalent to <c>HasRetentionPolicy(...)</c>.
/// </remarks>
/// <example>
///     <code>
///     [Retention(90, Every.Day)]    // drop chunks whose data is older than 90 days
///     public class Reading { … }
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RetentionAttribute : Attribute
{
    /// <param name="dropAfter">
    ///     Chunks whose data is older than this age (combined with <paramref name="unit" />) are dropped.
    ///     Must be <b>&gt; 0</b>.
    /// </param>
    /// <param name="unit">Calendar unit of <paramref name="dropAfter" />. Default <see cref="Every.Day" />.</param>
    public RetentionAttribute(long dropAfter, Every unit = Every.Day)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropAfter);
        DropAfter = dropAfter;
        Unit = unit;
    }

    /// <summary>Age threshold value (&gt; 0); chunks older than this are dropped.</summary>
    public long DropAfter { get; }

    /// <summary>Calendar unit of <see cref="DropAfter" />.</summary>
    public Every Unit { get; }

    /// <summary>
    ///     How often the retention job runs, combined with <see cref="ScheduleIntervalUnit" />. <c>0</c>
    ///     (default) uses the TimescaleDB default of <b>1 day</b>.
    /// </summary>
    public long ScheduleInterval { get; set; }

    /// <summary>Calendar unit of <see cref="ScheduleInterval" />. Default <see cref="Every.Hour" />.</summary>
    public Every ScheduleIntervalUnit { get; set; } = Every.Hour;
}
