namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     Adds a data retention policy (<c>add_retention_policy</c>) that drops chunks
///     older than the configured age.
/// </summary>
/// <remarks>
///     Equivalent to the <c>HasRetentionPolicy()</c> Fluent API. Set exactly one of
///     <see cref="DropAfterDays" /> / <see cref="DropAfter" />.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RetentionPolicyAttribute : Attribute
{
    /// <summary>Chunks older than this many days are dropped.</summary>
    public int DropAfterDays { get; set; }

    /// <summary>Raw-interval variant of <see cref="DropAfterDays" />, e.g. <c>"6 months"</c>.</summary>
    public string? DropAfter { get; set; }

    /// <summary>How often the policy job runs, in hours. TimescaleDB default: 1 day.</summary>
    public int ScheduleIntervalHours { get; set; }

    /// <summary>Raw-interval variant of <see cref="ScheduleIntervalHours" />.</summary>
    public string? ScheduleInterval { get; set; }
}
