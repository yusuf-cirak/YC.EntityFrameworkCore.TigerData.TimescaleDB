using System.Globalization;
using System.Text;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Migrations.Internal;

/// <summary>
///     Builds the TimescaleDB SQL fragments shared by the migrations SQL generator (table-attached
///     features) and the model differ (continuous aggregates, jobs, policies on aggregates).
///     This is the single source of truth for the emitted DDL.
/// </summary>
public static class TimescaleDbSql
{
    // ---------------------------------------------------------------- primitives

    public static string Quote(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public static string Qualified(string name, string? schema)
        => schema is null ? Quote(name) : Quote(schema) + "." + Quote(name);

    public static string Literal(string value)
        => "'" + value.Replace("'", "''") + "'";

    /// <summary>A schema-qualified relation as a <c>regclass</c> literal, e.g. <c>'"public"."t"'</c>.</summary>
    public static string Regclass(string name, string? schema)
        => Literal(Qualified(name, schema));

    public static string Interval(string value)
        => "INTERVAL " + Literal(value);

    /// <summary>Integer-time hypertables take plain numbers; time-based ones take intervals.</summary>
    public static string IntervalOrNumber(string value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? value
            : Interval(value);

    // ---------------------------------------------------------------- extension

    public static string CreateExtension()
        => "CREATE EXTENSION IF NOT EXISTS timescaledb;";

    // ---------------------------------------------------------------- hypertable core

    public static string CreateHypertable(
        string table,
        string? schema,
        string partitionColumn,
        string? chunkInterval,
        bool createDefaultIndexes,
        bool migrateData)
    {
        var sb = new StringBuilder()
            .Append("SELECT create_hypertable(")
            .Append(Regclass(table, schema))
            .Append(", by_range(")
            .Append(Literal(partitionColumn));

        if (chunkInterval is not null)
        {
            sb.Append(", ").Append(IntervalOrNumber(chunkInterval));
        }

        sb.Append(')');

        if (!createDefaultIndexes)
        {
            sb.Append(", create_default_indexes => false");
        }

        if (migrateData)
        {
            sb.Append(", migrate_data => true");
        }

        return sb.Append(");").ToString();
    }

    public static string AddDimension(string table, string? schema, string column, int partitions)
        => $"SELECT add_dimension({Regclass(table, schema)}, by_hash({Literal(column)}, "
            + $"{partitions.ToString(CultureInfo.InvariantCulture)}));";

    public static string AttachTablespace(string table, string? schema, string tablespace)
        => $"SELECT attach_tablespace({Literal(tablespace)}, {Regclass(table, schema)}, if_not_attached => true);";

    public static string DetachTablespace(string table, string? schema, string tablespace)
        => $"SELECT detach_tablespace({Literal(tablespace)}, {Regclass(table, schema)}, if_attached => true);";

    public static string SetChunkInterval(string table, string? schema, string interval)
        => $"SELECT set_chunk_time_interval({Regclass(table, schema)}, {IntervalOrNumber(interval)});";

    public static string SetIntegerNowFunction(string table, string? schema, string function)
        => $"SELECT set_integer_now_func({Regclass(table, schema)}, {Literal(function)});";

    // Chunk skipping is gated behind a GUC that defaults to off; enable it for the session first so
    // enable_chunk_skipping / disable_chunk_skipping do not raise "chunk skipping functionality disabled".
    public static string EnableChunkSkipping(string table, string? schema, string column)
        => "SET timescaledb.enable_chunk_skipping = on;\n"
            + $"SELECT enable_chunk_skipping({Regclass(table, schema)}, {Literal(column)});";

    public static string DisableChunkSkipping(string table, string? schema, string column)
        => "SET timescaledb.enable_chunk_skipping = on;\n"
            + $"SELECT disable_chunk_skipping({Regclass(table, schema)}, {Literal(column)});";

    // ---------------------------------------------------------------- columnstore

    public static string SetColumnstore(
        string table,
        string? schema,
        string? segmentBy,
        string? orderBy,
        string? mergeInterval)
    {
        var sb = new StringBuilder()
            .Append("ALTER TABLE ")
            .Append(Qualified(table, schema))
            .Append(" SET (timescaledb.enable_columnstore = true");

        if (segmentBy is not null)
        {
            sb.Append(", timescaledb.segmentby = ").Append(Literal(segmentBy));
        }

        if (orderBy is not null)
        {
            sb.Append(", timescaledb.orderby = ").Append(Literal(orderBy));
        }

        if (mergeInterval is not null)
        {
            sb.Append(", timescaledb.compress_chunk_time_interval = ").Append(Literal(mergeInterval));
        }

        return sb.Append(");").ToString();
    }

    /// <summary>
    ///     Reverts the columnstore: removes the policy and disables the columnstore. When
    ///     <paramref name="decompress" /> is true (the default behavior) every compressed chunk is first
    ///     converted back to rowstore — multi-statement, run with a suppressed transaction. When false,
    ///     the chunks are left as-is (the caller asserts none are compressed); single statement.
    /// </summary>
    public static string DisableColumnstore(string table, string? schema, bool decompress = true)
    {
        if (!decompress)
        {
            return new StringBuilder()
                .Append("CALL remove_columnstore_policy(").Append(Regclass(table, schema)).AppendLine(", if_exists => true);")
                .Append("ALTER TABLE ").Append(Qualified(table, schema))
                .Append(" SET (timescaledb.enable_columnstore = false);")
                .ToString();
        }

        var schemaLiteral = Literal(schema ?? "public");
        var tableLiteral = Literal(table);

        return new StringBuilder()
            .Append("CALL remove_columnstore_policy(").Append(Regclass(table, schema)).AppendLine(", if_exists => true);")
            .AppendLine("DO $$")
            .AppendLine("DECLARE chunk regclass;")
            .AppendLine("BEGIN")
            .AppendLine("    FOR chunk IN")
            .AppendLine("        SELECT format('%I.%I', chunk_schema, chunk_name)::regclass")
            .AppendLine("        FROM timescaledb_information.chunks")
            .Append("        WHERE hypertable_schema = ").Append(schemaLiteral)
            .Append(" AND hypertable_name = ").Append(tableLiteral).AppendLine(" AND is_compressed")
            .AppendLine("    LOOP")
            .AppendLine("        CALL convert_to_rowstore(chunk);")
            .AppendLine("    END LOOP;")
            .AppendLine("END $$;")
            .Append("ALTER TABLE ").Append(Qualified(table, schema))
            .Append(" SET (timescaledb.enable_columnstore = false);")
            .ToString();
    }

    // ---------------------------------------------------------------- policies (table or cagg)

    public static string AddRetentionPolicy(
        string relation, string? schema, string dropAfter,
        string? scheduleInterval, string? initialStart, string? timezone)
    {
        var sb = new StringBuilder()
            .Append("SELECT add_retention_policy(").Append(Regclass(relation, schema))
            .Append(", drop_after => ").Append(IntervalOrNumber(dropAfter));
        AppendInterval(sb, "schedule_interval", scheduleInterval);
        AppendSchedule(sb, initialStart, timezone);
        return sb.Append(");").ToString();
    }

    public static string RemoveRetentionPolicy(string relation, string? schema)
        => $"SELECT remove_retention_policy({Regclass(relation, schema)}, if_exists => true);";

    public static string AddColumnstorePolicy(
        string table, string? schema, string after,
        string? scheduleInterval, string? initialStart, string? timezone)
    {
        var sb = new StringBuilder()
            .Append("CALL add_columnstore_policy(").Append(Regclass(table, schema))
            .Append(", after => ").Append(IntervalOrNumber(after));
        AppendInterval(sb, "schedule_interval", scheduleInterval);
        AppendSchedule(sb, initialStart, timezone);
        return sb.Append(");").ToString();
    }

    public static string RemoveColumnstorePolicy(string table, string? schema)
        => $"CALL remove_columnstore_policy({Regclass(table, schema)}, if_exists => true);";

    public static string AddReorderPolicy(string table, string? schema, string index)
        => $"SELECT add_reorder_policy({Regclass(table, schema)}, {Literal(index)}, if_not_exists => true);";

    public static string RemoveReorderPolicy(string table, string? schema)
        => $"SELECT remove_reorder_policy({Regclass(table, schema)}, if_exists => true);";

    // ---------------------------------------------------------------- continuous aggregates

    public static string CreateContinuousAggregate(
        string name, string? schema, string query,
        bool materializedOnly, bool withNoData, string? chunkInterval)
    {
        var sb = new StringBuilder()
            .Append("CREATE MATERIALIZED VIEW ").Append(Qualified(name, schema)).AppendLine()
            .Append("WITH (timescaledb.continuous");

        if (!materializedOnly)
        {
            sb.Append(", timescaledb.materialized_only = false");
        }

        if (chunkInterval is not null)
        {
            sb.Append(", timescaledb.chunk_interval = ").Append(Literal(chunkInterval));
        }

        return sb.AppendLine(") AS").AppendLine(query)
            .Append("WITH ").Append(withNoData ? "NO DATA" : "DATA").Append(';')
            .ToString();
    }

    public static string DropContinuousAggregate(string name, string? schema)
        => $"DROP MATERIALIZED VIEW IF EXISTS {Qualified(name, schema)};";

    public static string AlterContinuousAggregateMaterializedOnly(string name, string? schema, bool materializedOnly)
        => $"ALTER MATERIALIZED VIEW {Qualified(name, schema)} "
            + $"SET (timescaledb.materialized_only = {(materializedOnly ? "true" : "false")});";

    public static string AlterContinuousAggregateColumnstore(
        string name, string? schema, bool enabled, string? segmentBy, string? orderBy)
    {
        var sb = new StringBuilder()
            .Append("ALTER MATERIALIZED VIEW ").Append(Qualified(name, schema))
            .Append(" SET (timescaledb.enable_columnstore = ").Append(enabled ? "true" : "false");

        if (enabled && segmentBy is not null)
        {
            sb.Append(", timescaledb.segmentby = ").Append(Literal(segmentBy));
        }

        if (enabled && orderBy is not null)
        {
            sb.Append(", timescaledb.orderby = ").Append(Literal(orderBy));
        }

        return sb.Append(");").ToString();
    }

    public static string AddRefreshPolicy(
        string view, string? schema, string startOffset, string endOffset,
        string? scheduleInterval, string? initialStart, string? timezone)
    {
        var sb = new StringBuilder()
            .Append("SELECT add_continuous_aggregate_policy(").Append(Regclass(view, schema))
            .Append(", start_offset => ").Append(IntervalOrNumber(startOffset))
            .Append(", end_offset => ").Append(IntervalOrNumber(endOffset))
            .Append(", schedule_interval => ").Append(Interval(scheduleInterval ?? "24 hours"));
        AppendSchedule(sb, initialStart, timezone);
        return sb.Append(");").ToString();
    }

    public static string RemoveRefreshPolicy(string view, string? schema)
        => $"SELECT remove_continuous_aggregate_policy({Regclass(view, schema)}, if_exists => true);";

    // ---------------------------------------------------------------- jobs

    public static string AddJob(
        string name, string procedure, string? scheduleInterval, string? config,
        bool? fixedSchedule, string? initialStart, string? timezone,
        string? maxRuntime, int? maxRetries, string? retryPeriod)
    {
        var sb = new StringBuilder()
            .Append("SELECT add_job(").Append(Literal(procedure)).Append("::regproc");

        AppendInterval(sb, "schedule_interval", scheduleInterval);
        AppendInterval(sb, "max_runtime", maxRuntime);
        AppendInterval(sb, "retry_period", retryPeriod);

        if (maxRetries is not null)
        {
            sb.Append(", max_retries => ")
                .Append(maxRetries.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (config is not null)
        {
            sb.Append(", config => ").Append(Literal(config)).Append("::jsonb");
        }

        if (initialStart is not null)
        {
            sb.Append(", initial_start => ").Append(Literal(initialStart)).Append("::timestamptz");
        }

        if (fixedSchedule is not null)
        {
            sb.Append(", fixed_schedule => ").Append(fixedSchedule.Value ? "true" : "false");
        }

        if (timezone is not null)
        {
            sb.Append(", timezone => ").Append(Literal(timezone));
        }

        return sb.Append(", job_name => ").Append(Literal(name)).Append(");").ToString();
    }

    public static string DeleteJob(string name)
        => "SELECT delete_job(job_id) FROM timescaledb_information.jobs "
            + $"WHERE application_name = {Literal(name)} OR application_name LIKE {Literal(name + " [%]")};";

    // ---------------------------------------------------------------- helpers

    private static void AppendInterval(StringBuilder sb, string parameter, string? value)
    {
        if (value is not null)
        {
            sb.Append(", ").Append(parameter).Append(" => ").Append(Interval(value));
        }
    }

    private static void AppendSchedule(StringBuilder sb, string? initialStart, string? timezone)
    {
        if (initialStart is not null)
        {
            sb.Append(", initial_start => ").Append(Literal(initialStart)).Append("::timestamptz");
        }

        if (timezone is not null)
        {
            sb.Append(", timezone => ").Append(Literal(timezone));
        }
    }
}
