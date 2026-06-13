using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Internal;
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
    /// <param name="scheduleInterval">How often the job runs. TimescaleDB default: 24 hours.</param>
    /// <param name="config">JSONB configuration passed to the procedure.</param>
    /// <param name="fixedSchedule">Fixed cadence (TimescaleDB default: true) vs. sliding interval.</param>
    /// <param name="initialStart">First execution time.</param>
    /// <param name="timezone">Time zone identifier for fixed schedules. TimescaleDB default: UTC.</param>
    /// <param name="maxRuntime">Maximum run time before the job is stopped.</param>
    /// <param name="maxRetries">Retries on failure before the job is considered failed.</param>
    /// <param name="retryPeriod">Delay between retries.</param>
    public static ModelBuilder HasTimescaleDbJob(
        this ModelBuilder modelBuilder,
        string name,
        string procedure,
        TimeSpan? scheduleInterval = null,
        string? config = null,
        bool? fixedSchedule = null,
        DateTimeOffset? initialStart = null,
        string? timezone = null,
        TimeSpan? maxRuntime = null,
        int? maxRetries = null,
        TimeSpan? retryPeriod = null)
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
            ScheduleInterval = scheduleInterval is { } s ? PgInterval.Format(s) : null,
            Config = config,
            FixedSchedule = fixedSchedule,
            InitialStart = initialStart?.ToString("O"),
            Timezone = timezone,
            MaxRuntime = maxRuntime is { } mr ? PgInterval.Format(mr) : null,
            MaxRetries = maxRetries,
            RetryPeriod = retryPeriod is { } rp ? PgInterval.Format(rp) : null,
        });

        modelBuilder.HasAnnotation(TimescaleDbAnnotationNames.Jobs, TimescaleDbJob.Serialize(existing));
        return modelBuilder;
    }
}
