using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Scaffolding.Internal;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Design.Scaffolding;

/// <summary>
///     Decorates the Npgsql database model factory: after the relational model is read,
///     queries the <c>timescaledb_information</c> views and attaches the TimescaleDB
///     annotations so that scaffolded entities round-trip hypertable configuration.
/// </summary>
public class TimescaleDbDatabaseModelFactory : IDatabaseModelFactory
{
    private readonly NpgsqlDatabaseModelFactory _inner;

    public TimescaleDbDatabaseModelFactory(IDiagnosticsLogger<DbLoggerCategory.Scaffolding> logger)
        => _inner = new NpgsqlDatabaseModelFactory(logger);

    public virtual DatabaseModel Create(string connectionString, DatabaseModelFactoryOptions options)
    {
        var model = _inner.Create(connectionString, options);

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        Enrich(model, connection);

        return model;
    }

    public virtual DatabaseModel Create(DbConnection connection, DatabaseModelFactoryOptions options)
    {
        var model = _inner.Create(connection, options);

        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            connection.Open();
        }

        try
        {
            Enrich(model, connection);
        }
        finally
        {
            if (!wasOpen)
            {
                connection.Close();
            }
        }

        return model;
    }

    private static void Enrich(DatabaseModel model, DbConnection connection)
    {
        if (!TimescaleDbInstalled(connection))
        {
            return;
        }

        EnrichHypertables(model, connection);
        EnrichTablespaces(model, connection);
        EnrichColumnstoreSettings(model, connection);
        EnrichPoliciesAndJobs(model, connection);
    }

    private static void EnrichTablespaces(DatabaseModel model, DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT hypertable_schema, hypertable_name, unnest(tablespaces) AS tablespace
            FROM timescaledb_information.hypertables
            WHERE tablespaces IS NOT NULL
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var table = FindTable(model, reader.GetString(0), reader.GetString(1));
            if (table is null)
            {
                continue;
            }

            var name = reader.GetString(2);
            table[TimescaleDbAnnotationNames.Tablespaces] =
                table[TimescaleDbAnnotationNames.Tablespaces] is string existing ? $"{existing}, {name}" : name;
        }
    }

    private static bool TimescaleDbInstalled(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pg_extension WHERE extname = 'timescaledb'";
        return command.ExecuteScalar() is not null;
    }

    private static DatabaseTable? FindTable(DatabaseModel model, string? schema, string? name)
        => model.Tables.FirstOrDefault(t =>
            t.Name == name && (t.Schema == schema || (t.Schema is null && schema == "public")));

    private static void EnrichHypertables(DatabaseModel model, DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d.hypertable_schema, d.hypertable_name, d.column_name, d.dimension_type,
                   d.time_interval::text, d.integer_interval, d.num_partitions, d.integer_now_func,
                   d.dimension_number
            FROM timescaledb_information.dimensions d
            ORDER BY d.hypertable_schema, d.hypertable_name, d.dimension_number
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var table = FindTable(model, reader.GetString(0), reader.GetString(1));
            if (table is null)
            {
                continue;
            }

            var column = reader.GetString(2);
            var dimensionType = reader.GetString(3);
            var dimensionNumber = reader.GetInt32(8);

            // Reconstruct the range interval literal from the interval (time) or integer form.
            string? Interval() => reader.IsDBNull(4)
                ? reader.IsDBNull(5) ? null : reader.GetInt64(5).ToString(CultureInfo.InvariantCulture)
                : reader.GetString(4);

            if (dimensionNumber == 1)
            {
                // The primary partitioning column is always dimension 1 and always range/time based.
                table[TimescaleDbAnnotationNames.IsHypertable] = true;
                table[TimescaleDbAnnotationNames.PartitionColumn] = column;

                if (Interval() is { } interval)
                {
                    table[TimescaleDbAnnotationNames.ChunkInterval] = interval;
                }

                if (!reader.IsDBNull(7))
                {
                    table[TimescaleDbAnnotationNames.IntegerNowFunction] = reader.GetString(7);
                }
            }
            else if (string.Equals(dimensionType, "Time", StringComparison.OrdinalIgnoreCase))
            {
                // A secondary range (by_range) dimension: carries an interval, not a partition count.
                if (Interval() is { } interval)
                {
                    var item = $"{column}:{interval}";
                    table[TimescaleDbAnnotationNames.RangeDimensions] =
                        table[TimescaleDbAnnotationNames.RangeDimensions] is string existing
                            ? $"{existing}, {item}"
                            : item;
                }
            }
            else if (!reader.IsDBNull(6))
            {
                // A secondary hash (by_hash) dimension: carries a partition count.
                var item = $"{column}:{(int)reader.GetInt16(6)}";
                table[TimescaleDbAnnotationNames.SpaceDimensions] =
                    table[TimescaleDbAnnotationNames.SpaceDimensions] is string existing
                        ? $"{existing}, {item}"
                        : item;
            }
        }
    }

    private static void EnrichColumnstoreSettings(DatabaseModel model, DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT n.nspname, c.relname, s.segmentby, s.orderby, s.index::text
            FROM timescaledb_information.hypertable_columnstore_settings s
            JOIN pg_class c ON c.oid = s.hypertable
            JOIN pg_namespace n ON n.oid = c.relnamespace
            """;

        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var table = FindTable(model, reader.GetString(0), reader.GetString(1));
                if (table is null)
                {
                    continue;
                }

                table[TimescaleDbAnnotationNames.ColumnstoreEnabled] = true;

                if (!reader.IsDBNull(2))
                {
                    table[TimescaleDbAnnotationNames.ColumnstoreSegmentBy] = reader.GetString(2);
                }

                if (!reader.IsDBNull(3))
                {
                    table[TimescaleDbAnnotationNames.ColumnstoreOrderBy] = reader.GetString(3);
                }

                if (!reader.IsDBNull(4) && ReadSparseIndex(reader.GetString(4)) is { } sparseIndex)
                {
                    table[TimescaleDbAnnotationNames.ColumnstoreSparseIndex] = sparseIndex;
                }
            }
        }
        catch (PostgresException e)
            when (e.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        {
            // Older TimescaleDB without the columnstore view/index column; those settings are skipped.
        }
    }

    /// <summary>
    ///     Reconstructs the <c>Columnstore:SparseIndex</c> annotation from the <c>index</c> jsonb of
    ///     <c>hypertable_columnstore_settings</c>. Only explicitly-configured entries
    ///     (<c>"source": "config"</c>) are emitted; the <c>minmax</c>/<c>firstlast</c> indexes that
    ///     TimescaleDB derives from the orderby are excluded. Each entry becomes <c>kind("column")</c>,
    ///     matching the canonical form the model finalizing convention stores.
    /// </summary>
    private static string? ReadSparseIndex(string indexJson)
    {
        using var document = JsonDocument.Parse(indexJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var entries = new List<string>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("source", out var source)
                || source.ValueKind != JsonValueKind.String
                || source.GetString() != "config"
                || !element.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !element.TryGetProperty("column", out var col)
                || col.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            entries.Add($"{type.GetString()}(\"{col.GetString()}\")");
        }

        return entries.Count > 0 ? string.Join(", ", entries) : null;
    }

    private static void EnrichPoliciesAndJobs(DatabaseModel model, DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT application_name, proc_name, schedule_interval::text, config::text,
                   hypertable_schema, hypertable_name,
                   max_runtime::text, max_retries, retry_period::text
            FROM timescaledb_information.jobs
            """;

        var jobs = new List<TimescaleDbJob>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var applicationName = reader.IsDBNull(0) ? null : reader.GetString(0);
            var procName = reader.IsDBNull(1) ? null : reader.GetString(1);
            var scheduleInterval = reader.IsDBNull(2) ? null : reader.GetString(2);
            var configJson = reader.IsDBNull(3) ? null : reader.GetString(3);
            var table = reader.IsDBNull(5)
                ? null
                : FindTable(model, reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5));

            using var config = configJson is null ? null : JsonDocument.Parse(configJson);

            switch (procName)
            {
                case "policy_retention" when table is not null:
                    // Creation-time form (drop_created_before) and value form (drop_after) are mutually exclusive.
                    if (TryGetConfigString(config, "drop_created_before") is { } dropCreatedBefore)
                    {
                        table[TimescaleDbAnnotationNames.RetentionPolicyDropCreatedBefore] = dropCreatedBefore;
                        if (scheduleInterval is not null)
                        {
                            table[TimescaleDbAnnotationNames.RetentionPolicyScheduleInterval] = scheduleInterval;
                        }
                    }
                    else if (TryGetConfigString(config, "drop_after") is { } dropAfter)
                    {
                        table[TimescaleDbAnnotationNames.RetentionPolicyDropAfter] = dropAfter;
                        if (scheduleInterval is not null)
                        {
                            table[TimescaleDbAnnotationNames.RetentionPolicyScheduleInterval] = scheduleInterval;
                        }
                    }

                    break;

                case "policy_compression" or "policy_columnstore" when table is not null:
                    // Creation-time form (compress_created_before) and value form (compress_after) are exclusive.
                    var createdBefore = TryGetConfigString(config, "compress_created_before")
                        ?? TryGetConfigString(config, "created_before");
                    var after = TryGetConfigString(config, "compress_after")
                        ?? TryGetConfigString(config, "after");
                    if (createdBefore is not null)
                    {
                        table[TimescaleDbAnnotationNames.ColumnstorePolicyCreatedBefore] = createdBefore;
                        if (scheduleInterval is not null)
                        {
                            table[TimescaleDbAnnotationNames.ColumnstorePolicyScheduleInterval] = scheduleInterval;
                        }
                    }
                    else if (after is not null)
                    {
                        table[TimescaleDbAnnotationNames.ColumnstorePolicyAfter] = after;
                        if (scheduleInterval is not null)
                        {
                            table[TimescaleDbAnnotationNames.ColumnstorePolicyScheduleInterval] = scheduleInterval;
                        }
                    }

                    break;

                case "policy_reorder" when table is not null:
                    if (TryGetConfigString(config, "index_name") is { } index)
                    {
                        table[TimescaleDbAnnotationNames.ReorderPolicyIndex] = index;
                    }

                    break;

                case "policy_refresh_continuous_aggregate":
                    // Continuous aggregates are not scaffolded (materialized views are outside
                    // the Npgsql scaffolding surface); their refresh policies are skipped too.
                    break;

                default:
                    if (applicationName is not null && procName is not null
                        && !procName.StartsWith("policy_", StringComparison.Ordinal))
                    {
                        // add_job registers jobs as 'name [job_id]'; recover the configured name.
                        var bracket = applicationName.LastIndexOf(" [", StringComparison.Ordinal);
                        var name = bracket > 0 && applicationName.EndsWith(']')
                            ? applicationName[..bracket]
                            : applicationName;

                        jobs.Add(new TimescaleDbJob
                        {
                            Name = name,
                            Procedure = procName,
                            ScheduleInterval = scheduleInterval,
                            Config = configJson,
                            MaxRuntime = reader.IsDBNull(6) ? null : reader.GetString(6),
                            MaxRetries = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                            RetryPeriod = reader.IsDBNull(8) ? null : reader.GetString(8),
                        });
                    }

                    break;
            }
        }

        if (jobs.Count > 0)
        {
            model[TimescaleDbAnnotationNames.Jobs] = TimescaleDbJob.Serialize(jobs);
        }
    }

    private static string? TryGetConfigString(JsonDocument? config, string property)
        => config is not null && config.RootElement.ValueKind == JsonValueKind.Object
            && config.RootElement.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
}
