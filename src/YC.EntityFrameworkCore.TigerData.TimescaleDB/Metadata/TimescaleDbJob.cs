using System.Text.Json;
using System.Text.Json.Serialization;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;

/// <summary>
///     A user-defined TimescaleDB background job (<c>add_job</c>), stored as a model annotation
///     and serialized into the model snapshot.
/// </summary>
public sealed record TimescaleDbJob
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Unique job name; becomes the job's <c>application_name</c> and is used to drop it.</summary>
    public required string Name { get; init; }

    /// <summary>Procedure or function to run, e.g. <c>"public.refresh_stats"</c>.</summary>
    public required string Procedure { get; init; }

    /// <summary>How often the job runs, e.g. <c>"1 hour"</c>. TimescaleDB default: 24 hours.</summary>
    public string? ScheduleInterval { get; init; }

    /// <summary>JSONB configuration passed to the procedure.</summary>
    public string? Config { get; init; }

    /// <summary>Fixed cadence (true, TimescaleDB default) vs. sliding interval (false).</summary>
    public bool? FixedSchedule { get; init; }

    /// <summary>First execution time (ISO-8601 timestamptz literal).</summary>
    public string? InitialStart { get; init; }

    /// <summary>Time zone for fixed schedules. TimescaleDB default: UTC.</summary>
    public string? Timezone { get; init; }

    public static string Serialize(IReadOnlyList<TimescaleDbJob> jobs)
        => JsonSerializer.Serialize(jobs.OrderBy(j => j.Name, StringComparer.Ordinal), SerializerOptions);

    public static IReadOnlyList<TimescaleDbJob> Deserialize(string? json)
        => json is null
            ? []
            : JsonSerializer.Deserialize<List<TimescaleDbJob>>(json, SerializerOptions) ?? [];
}
