using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     TimescaleDB Fluent API for the model: user-defined background jobs.
/// </summary>
public static class TimescaleDbModelBuilderExtensions
{
    /// <summary>
    ///     Registers a user-defined TimescaleDB background job (<c>add_job</c>). The job is created,
    ///     altered and dropped through migrations, keyed by <paramref name="name" />.
    /// </summary>
    /// <param name="name">Unique job name (used as the job's <c>application_name</c>).</param>
    /// <param name="procedure">Procedure or function to run, e.g. <c>"public.refresh_stats"</c>.</param>
    /// <param name="scheduleInterval">How often the job runs, e.g. <c>"1 hour"</c>. TimescaleDB default: 24 hours.</param>
    /// <param name="config">JSONB configuration passed to the procedure.</param>
    /// <param name="fixedSchedule">Fixed cadence (TimescaleDB default: true) vs. sliding interval.</param>
    /// <param name="initialStart">First execution time (ISO-8601 timestamptz literal).</param>
    /// <param name="timezone">Time zone for fixed schedules. TimescaleDB default: UTC.</param>
    public static ModelBuilder HasTimescaleDbJob(
        this ModelBuilder modelBuilder,
        string name,
        string procedure,
        string? scheduleInterval = null,
        string? config = null,
        bool? fixedSchedule = null,
        string? initialStart = null,
        string? timezone = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(procedure);

        var existing = TimescaleDbJob
            .Deserialize(modelBuilder.Model.FindAnnotation(TimescaleDbAnnotationNames.Jobs)?.Value as string)
            .Where(j => !string.Equals(j.Name, name, StringComparison.Ordinal))
            .ToList();

        existing.Add(new TimescaleDbJob
        {
            Name = name,
            Procedure = procedure,
            ScheduleInterval = scheduleInterval,
            Config = config,
            FixedSchedule = fixedSchedule,
            InitialStart = initialStart,
            Timezone = timezone,
        });

        modelBuilder.HasAnnotation(TimescaleDbAnnotationNames.Jobs, TimescaleDbJob.Serialize(existing));
        return modelBuilder;
    }
}
