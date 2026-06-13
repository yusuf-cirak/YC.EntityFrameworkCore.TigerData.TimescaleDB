using System.Data.Common;
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
        EnrichColumnstoreSettings(model, connection);
        EnrichPoliciesAndJobs(model, connection);
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
                   d.time_interval::text, d.integer_interval, d.num_partitions, d.integer_now_func
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

            if (string.Equals(dimensionType, "Time", StringComparison.OrdinalIgnoreCase))
            {
                table[TimescaleDbAnnotationNames.IsHypertable] = true;
                table[TimescaleDbAnnotationNames.PartitionColumn] = column;

                var interval = reader.IsDBNull(4)
                    ? reader.IsDBNull(5) ? null : reader.GetInt64(5).ToString()
                    : reader.GetString(4);
                if (interval is not null)
                {
                    table[TimescaleDbAnnotationNames.ChunkInterval] = interval;
                }

                if (!reader.IsDBNull(7))
                {
                    table[TimescaleDbAnnotationNames.IntegerNowFunction] = reader.GetString(7);
                }
            }
            else
            {
                table[TimescaleDbAnnotationNames.SpacePartitionColumn] = column;
                if (!reader.IsDBNull(6))
                {
                    table[TimescaleDbAnnotationNames.SpacePartitions] = (int)reader.GetInt16(6);
                }
            }
        }
    }

    private static void EnrichColumnstoreSettings(DatabaseModel model, DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT n.nspname, c.relname, s.segmentby, s.orderby
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
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Older TimescaleDB without the columnstore view; compression settings are skipped.
        }
    }

    private static void EnrichPoliciesAndJobs(DatabaseModel model, DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT application_name, proc_name, schedule_interval::text, config::text,
                   hypertable_schema, hypertable_name
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
                    if (TryGetConfigString(config, "drop_after") is { } dropAfter)
                    {
                        table[TimescaleDbAnnotationNames.RetentionPolicyDropAfter] = dropAfter;
                        if (scheduleInterval is not null)
                        {
                            table[TimescaleDbAnnotationNames.RetentionPolicyScheduleInterval] = scheduleInterval;
                        }
                    }

                    break;

                case "policy_compression" or "policy_columnstore" when table is not null:
                    var after = TryGetConfigString(config, "compress_after")
                        ?? TryGetConfigString(config, "after");
                    if (after is not null)
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
