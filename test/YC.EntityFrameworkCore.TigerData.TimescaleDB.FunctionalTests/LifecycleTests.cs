using Microsoft.EntityFrameworkCore;
using Npgsql;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests;

/// <summary>
///     The full life of an existing table against a real TimescaleDB: plain table with data
///     → hypertable (auto-convert) → columnstore on → chunk compressed → columnstore off
///     (auto-decompress) → chunk interval widened.
/// </summary>
[Collection(TimescaleDbCollection.Name)]
public class LifecycleTests(TimescaleDbContainerFixture fixture)
{
    private class Metric
    {
        public DateTimeOffset Time { get; set; }
        public string Source { get; set; } = null!;
        public double Value { get; set; }
    }

    private static Action<ModelBuilder> Plain => mb => mb.Entity<Metric>(e =>
    {
        e.HasNoKey();
        e.ToTable("metrics");
        e.Property(x => x.Time).HasColumnName("time");
        e.Property(x => x.Source).HasColumnName("source");
        e.Property(x => x.Value).HasColumnName("value");
    });

    private static Action<ModelBuilder> Hypertable(
        Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Metric>>? extra = null,
        TimeSpan? chunkInterval = null)
        => mb => mb.Entity<Metric>(e =>
        {
            e.HasNoKey();
            e.ToTable("metrics");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.Value).HasColumnName("value");
            e.IsHypertable(x => x.Time, chunkInterval: chunkInterval ?? TimeSpan.FromDays(1));
            extra?.Invoke(e);
        });

    [Fact]
    public async Task Existing_table_full_lifecycle()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.CreateDatabaseAsync(ct);

        // 1. Plain table with pre-existing data.
        await DiffExecutor.ApplyAsync(connectionString, null, Plain, ct);
        await ExecuteAsync(connectionString,
            """
            INSERT INTO metrics (time, source, value)
            SELECT '2026-06-01T00:00:00Z'::timestamptz + (n || ' hours')::interval, 's' || (n % 3), n
            FROM generate_series(0, 99) AS n
            """, ct);

        // 2. → hypertable: data must be migrated into chunks.
        await DiffExecutor.ApplyAsync(connectionString, Plain, Hypertable(), ct);

        Assert.Equal(1L, await ScalarAsync(connectionString,
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'metrics'", ct));
        Assert.Equal(100L, await ScalarAsync(connectionString, "SELECT count(*) FROM metrics", ct));
        var chunkCount = (long)(await ScalarAsync(connectionString,
            "SELECT count(*) FROM timescaledb_information.chunks WHERE hypertable_name = 'metrics'", ct))!;
        Assert.True(chunkCount >= 2, $"expected migrated data spread over chunks, got {chunkCount}");

        // 3. → columnstore enabled + policy.
        await DiffExecutor.ApplyAsync(
            connectionString,
            Hypertable(),
            Hypertable(e =>
            {
                e.HasColumnstore(cs => cs.SegmentBy(x => x.Source).OrderByDescending(x => x.Time));
                e.HasColumnstorePolicy(after: TimeSpan.FromDays(7));
            }),
            ct);

        Assert.Equal("s", await ScalarAsync(connectionString,
            "SELECT CASE WHEN compression_enabled THEN 's' ELSE 'n' END "
            + "FROM timescaledb_information.hypertables WHERE hypertable_name = 'metrics'", ct));

        // 4. Compress one chunk so the disable path has real work to do.
        await ExecuteAsync(connectionString,
            """
            DO $$
            DECLARE chunk regclass;
            BEGIN
                SELECT format('%I.%I', chunk_schema, chunk_name)::regclass INTO chunk
                FROM timescaledb_information.chunks
                WHERE hypertable_name = 'metrics'
                LIMIT 1;
                CALL convert_to_columnstore(chunk);
            END $$;
            """, ct);

        Assert.Equal(1L, await ScalarAsync(connectionString,
            "SELECT count(*) FROM timescaledb_information.chunks "
            + "WHERE hypertable_name = 'metrics' AND is_compressed", ct));

        // 5. → columnstore disabled: policy removed, chunks decompressed, parameter reset.
        await DiffExecutor.ApplyAsync(
            connectionString,
            Hypertable(e =>
            {
                e.HasColumnstore(cs => cs.SegmentBy(x => x.Source).OrderByDescending(x => x.Time));
                e.HasColumnstorePolicy(after: TimeSpan.FromDays(7));
            }),
            Hypertable(),
            ct);

        Assert.Equal(0L, await ScalarAsync(connectionString,
            "SELECT count(*) FROM timescaledb_information.chunks "
            + "WHERE hypertable_name = 'metrics' AND is_compressed", ct));
        Assert.Equal("n", await ScalarAsync(connectionString,
            "SELECT CASE WHEN compression_enabled THEN 's' ELSE 'n' END "
            + "FROM timescaledb_information.hypertables WHERE hypertable_name = 'metrics'", ct));
        Assert.Equal(0L, await ScalarAsync(connectionString,
            "SELECT count(*) FROM timescaledb_information.jobs "
            + "WHERE proc_name IN ('policy_compression', 'policy_columnstore') "
            + "AND hypertable_name = 'metrics'", ct));
        Assert.Equal(100L, await ScalarAsync(connectionString, "SELECT count(*) FROM metrics", ct));

        // 6. Chunk interval widened on the existing hypertable.
        await DiffExecutor.ApplyAsync(
            connectionString,
            Hypertable(),
            Hypertable(chunkInterval: TimeSpan.FromDays(7)),
            ct);

        Assert.Equal("7 days", await ScalarAsync(connectionString,
            "SELECT time_interval::text FROM timescaledb_information.dimensions "
            + "WHERE hypertable_name = 'metrics' AND dimension_number = 1", ct));
    }

    [Fact]
    public async Task Integer_time_hypertable_with_retention_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.CreateDatabaseAsync(ct);

        await ExecuteAsync(connectionString,
            """
            CREATE FUNCTION unix_seconds_now() RETURNS bigint
            LANGUAGE sql STABLE AS $$ SELECT extract(epoch FROM now())::bigint $$;
            """, ct);

        Action<ModelBuilder> model = mb => mb.Entity<IntegerSeries>(e =>
        {
            e.HasNoKey();
            e.ToTable("integer_series");
            e.Property(x => x.UnixSeconds).HasColumnName("unix_seconds");
            e.Property(x => x.Value).HasColumnName("value");
            e.IsHypertableByInteger(x => x.UnixSeconds, 86_400, integerNowFunction: "unix_seconds_now");
            e.HasRetentionPolicy(7_776_000L); // 90 days in seconds
        });

        await DiffExecutor.ApplyAsync(connectionString, null, model, ct);

        Assert.Equal(1L, await ScalarAsync(connectionString,
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'integer_series'", ct));
        Assert.Equal("unix_seconds_now", await ScalarAsync(connectionString,
            "SELECT integer_now_func FROM timescaledb_information.dimensions "
            + "WHERE hypertable_name = 'integer_series'", ct));
        Assert.Equal(1L, await ScalarAsync(connectionString,
            "SELECT count(*) FROM timescaledb_information.jobs "
            + "WHERE proc_name = 'policy_retention' AND hypertable_name = 'integer_series'", ct));
    }

    [Fact]
    public async Task Hypertable_to_plain_rebuilds_preserving_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.CreateDatabaseAsync(ct);

        await DiffExecutor.ApplyAsync(connectionString, null, Hypertable(), ct);
        await ExecuteAsync(connectionString,
            """
            INSERT INTO metrics (time, source, value)
            SELECT '2026-06-01T00:00:00Z'::timestamptz + (n || ' hours')::interval, 's', n
            FROM generate_series(1, 50) AS n
            """, ct);

        // Remove the hypertable configuration → rebuild back to a plain table, data preserved.
        await DiffExecutor.ApplyAsync(connectionString, Hypertable(), Plain, ct);

        Assert.Equal(0L, await ScalarAsync(connectionString,
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'metrics'", ct));
        Assert.Equal(50L, await ScalarAsync(connectionString, "SELECT count(*) FROM metrics", ct));
        Assert.Equal(1275.0d, Convert.ToDouble(
            await ScalarAsync(connectionString, "SELECT sum(value) FROM metrics", ct)));

        // Reverse again: plain → hypertable migrates the data into chunks.
        await DiffExecutor.ApplyAsync(connectionString, Plain, Hypertable(), ct);
        Assert.Equal(1L, await ScalarAsync(connectionString,
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'metrics'", ct));
        Assert.Equal(50L, await ScalarAsync(connectionString, "SELECT count(*) FROM metrics", ct));
    }

    private class Event
    {
        public DateTimeOffset OccurredAt { get; set; }
        public DateTimeOffset RecordedAt { get; set; }
        public double Value { get; set; }
    }

    private static Action<ModelBuilder> EventBy(string partition) => mb => mb.Entity<Event>(e =>
    {
        e.HasNoKey();
        e.ToTable("events");
        e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        e.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        e.Property(x => x.Value).HasColumnName("value");
        if (partition == "occurred")
        {
            e.IsHypertable(x => x.OccurredAt, chunkInterval: TimeSpan.FromDays(1));
        }
        else
        {
            e.IsHypertable(x => x.RecordedAt, chunkInterval: TimeSpan.FromDays(1));
        }
    });

    [Fact]
    public async Task Partition_column_change_rebuilds_preserving_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.CreateDatabaseAsync(ct);

        await DiffExecutor.ApplyAsync(connectionString, null, EventBy("occurred"), ct);
        await ExecuteAsync(connectionString,
            """
            INSERT INTO events (occurred_at, recorded_at, value)
            SELECT t, t + INTERVAL '1 minute', n
            FROM generate_series(1, 40) AS n,
                 LATERAL (SELECT '2026-06-01T00:00:00Z'::timestamptz + (n || ' hours')::interval AS t) s
            """, ct);

        // Repartition from occurred_at to recorded_at → rebuild, data preserved.
        await DiffExecutor.ApplyAsync(connectionString, EventBy("occurred"), EventBy("recorded"), ct);

        Assert.Equal(40L, await ScalarAsync(connectionString, "SELECT count(*) FROM events", ct));
        Assert.Equal("recorded_at", await ScalarAsync(connectionString,
            "SELECT column_name FROM timescaledb_information.dimensions "
            + "WHERE hypertable_name = 'events' AND dimension_number = 1", ct));
    }

    [Fact]
    public async Task Applying_unchanged_model_is_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.CreateDatabaseAsync(ct);

        var model = Hypertable(e =>
        {
            e.HasColumnstore(cs => cs.SegmentBy(x => x.Source).OrderByDescending(x => x.Time));
            e.HasRetentionPolicy(dropAfter: TimeSpan.FromDays(90));
        });

        await DiffExecutor.ApplyAsync(connectionString, null, model, ct);

        // Re-diffing the same model must produce no SQL (snapshot stability).
        var sql = DiffExecutor.GenerateSql(connectionString, model, model);
        Assert.Empty(sql);
    }

    [Fact]
    public async Task Reorder_policy_added_then_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        var withReorder = Hypertable(e =>
        {
            e.HasIndex(x => x.Time);
            e.HasReorderPolicy(x => x.Time);
        });
        var indexOnly = Hypertable(e => e.HasIndex(x => x.Time));

        await DiffExecutor.ApplyAsync(cs, null, withReorder, ct);
        Assert.Equal(1L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.jobs "
            + "WHERE proc_name = 'policy_reorder' AND hypertable_name = 'metrics'", ct));

        await DiffExecutor.ApplyAsync(cs, withReorder, indexOnly, ct);
        Assert.Equal(0L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.jobs "
            + "WHERE proc_name = 'policy_reorder' AND hypertable_name = 'metrics'", ct));
    }

    [Fact]
    public async Task Retention_policy_replaced_keeps_single_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        var ninety = Hypertable(e => e.HasRetentionPolicy(dropAfter: TimeSpan.FromDays(90)));
        var thirty = Hypertable(e => e.HasRetentionPolicy(dropAfter: TimeSpan.FromDays(30)));

        await DiffExecutor.ApplyAsync(cs, null, ninety, ct);
        await DiffExecutor.ApplyAsync(cs, ninety, thirty, ct);

        // Replace = remove + add → still exactly one retention job, now dropping after 30 days.
        Assert.Equal(1L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.jobs "
            + "WHERE proc_name = 'policy_retention' AND hypertable_name = 'metrics'", ct));
        Assert.Equal("30 days", await ScalarAsync(cs,
            "SELECT (config->>'drop_after') FROM timescaledb_information.jobs "
            + "WHERE proc_name = 'policy_retention' AND hypertable_name = 'metrics'", ct));
    }

    [Fact]
    public async Task Columnstore_policy_added_then_removed_in_isolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        var enabled = Hypertable(e => e.HasColumnstore(c => c.SegmentBy(x => x.Source).OrderByDescending(x => x.Time)));
        var withPolicy = Hypertable(e =>
        {
            e.HasColumnstore(c => c.SegmentBy(x => x.Source).OrderByDescending(x => x.Time));
            e.HasColumnstorePolicy(after: TimeSpan.FromDays(7));
        });

        await DiffExecutor.ApplyAsync(cs, null, enabled, ct);
        await DiffExecutor.ApplyAsync(cs, enabled, withPolicy, ct);
        Assert.Equal(1L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.jobs "
            + "WHERE proc_name IN ('policy_compression', 'policy_columnstore') AND hypertable_name = 'metrics'", ct));

        // Removing only the policy must not disable the columnstore.
        await DiffExecutor.ApplyAsync(cs, withPolicy, enabled, ct);
        Assert.Equal(0L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.jobs "
            + "WHERE proc_name IN ('policy_compression', 'policy_columnstore') AND hypertable_name = 'metrics'", ct));
        Assert.Equal("s", await ScalarAsync(cs,
            "SELECT CASE WHEN compression_enabled THEN 's' ELSE 'n' END "
            + "FROM timescaledb_information.hypertables WHERE hypertable_name = 'metrics'", ct));
    }

    [Fact]
    public async Task Job_config_change_is_applied()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        await ExecuteAsync(cs,
            "CREATE PROCEDURE public.cleanup(job_id int, config jsonb) LANGUAGE plpgsql AS $$ BEGIN END $$;", ct);

        Action<ModelBuilder> Model(string config) => mb =>
        {
            Hypertable()(mb);
            mb.HasTimescaleDbJob("cleanup", "public.cleanup", scheduleInterval: TimeSpan.FromDays(1), config: config);
        };

        await DiffExecutor.ApplyAsync(cs, null, Model("""{"drop_after":"30 days"}"""), ct);
        await DiffExecutor.ApplyAsync(cs,
            Model("""{"drop_after":"30 days"}"""), Model("""{"drop_after":"60 days"}"""), ct);

        Assert.Equal(1L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.jobs WHERE application_name LIKE 'cleanup%'", ct));
        Assert.Equal("60 days", await ScalarAsync(cs,
            "SELECT (config->>'drop_after') FROM timescaledb_information.jobs "
            + "WHERE application_name LIKE 'cleanup%'", ct));
    }

    private class Series
    {
        public DateTimeOffset Time { get; set; }
        public long Reading { get; set; }
    }

    [Fact]
    public async Task Chunk_skipping_enabled_then_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        Action<ModelBuilder> Model(bool skip) => mb => mb.Entity<Series>(e =>
        {
            e.HasNoKey();
            e.ToTable("series");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.Reading).HasColumnName("reading");
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
            if (skip)
            {
                e.HasChunkSkipping(x => x.Reading);
            }
        });

        await DiffExecutor.ApplyAsync(cs, null, Model(false), ct);

        // Enable then disable must both succeed — the engine first turns on the
        // timescaledb.enable_chunk_skipping GUC, which the feature requires.
        await DiffExecutor.ApplyAsync(cs, Model(false), Model(true), ct);
        Assert.Equal(1L, await ScalarAsync(cs,
            "SELECT count(*) FROM _timescaledb_catalog.chunk_column_stats s "
            + "JOIN _timescaledb_catalog.hypertable h ON h.id = s.hypertable_id "
            + "WHERE h.table_name = 'series' AND s.column_name = 'reading'", ct));

        await DiffExecutor.ApplyAsync(cs, Model(true), Model(false), ct);
        Assert.Equal(0L, await ScalarAsync(cs,
            "SELECT count(*) FROM _timescaledb_catalog.chunk_column_stats s "
            + "JOIN _timescaledb_catalog.hypertable h ON h.id = s.hypertable_id "
            + "WHERE h.table_name = 'series' AND s.column_name = 'reading'", ct));
    }

    private class IntegerSeries
    {
        public long UnixSeconds { get; set; }
        public double Value { get; set; }
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct);
    }
}
