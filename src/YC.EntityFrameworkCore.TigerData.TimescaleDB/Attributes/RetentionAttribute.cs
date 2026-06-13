namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     Adds a data retention policy (<c>add_retention_policy</c>) that drops chunks older than the
///     configured age. Requires a <see cref="PartitionColumnAttribute" /> on the class.
/// </summary>
/// <remarks>Equivalent to the <c>HasRetentionPolicy(...)</c> Fluent API.</remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RetentionAttribute : Attribute
{
    /// <param name="dropAfter">Chunks older than this (with <paramref name="unit" />) are dropped.</param>
    /// <param name="unit">Unit of <paramref name="dropAfter" />.</param>
    public RetentionAttribute(long dropAfter, Every unit = Every.Day)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropAfter);
        DropAfter = dropAfter;
        Unit = unit;
    }

    public long DropAfter { get; }

    public Every Unit { get; }

    /// <summary>How often the policy job runs (with <see cref="ScheduleIntervalUnit" />). <c>0</c> uses the default (1 day).</summary>
    public long ScheduleInterval { get; set; }

    /// <summary>Unit of <see cref="ScheduleInterval" />.</summary>
    public Every ScheduleIntervalUnit { get; set; } = Every.Hour;
}
