namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     Adds an automatic columnstore conversion policy
///     (<c>add_columnstore_policy</c>) to the hypertable.
/// </summary>
/// <remarks>
///     Equivalent to the <c>HasColumnstorePolicy()</c> Fluent API. Set exactly one of
///     <see cref="AfterDays" /> / <see cref="After" />.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ColumnstorePolicyAttribute : Attribute
{
    /// <summary>Chunks older than this many days are converted to columnstore.</summary>
    public int AfterDays { get; set; }

    /// <summary>Raw-interval variant of <see cref="AfterDays" />, e.g. <c>"1 month"</c>.</summary>
    public string? After { get; set; }

    /// <summary>How often the policy job runs, in hours. TimescaleDB default: derived from the chunk interval.</summary>
    public int ScheduleIntervalHours { get; set; }

    /// <summary>Raw-interval variant of <see cref="ScheduleIntervalHours" />.</summary>
    public string? ScheduleInterval { get; set; }
}
