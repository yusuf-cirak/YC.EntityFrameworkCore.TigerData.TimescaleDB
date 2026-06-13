using Microsoft.EntityFrameworkCore;
using Npgsql;
using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests;

/// <summary>
///     A full-featured model is applied from scratch, then its exact reverse (<c>Diff(full, plain)</c>)
///     is applied back. The database must return to a clean baseline — no hypertable, continuous
///     aggregate or job left behind — while the table's rows survive the un-hypertable rebuild. This is
///     the end-to-end proof that every "Down" produced by the engine actually composes.
/// </summary>
[Collection(TimescaleDbCollection.Name)]
public class RoundTripTests(TimescaleDbContainerFixture fixture)
{
    private class Reading
    {
        public DateTimeOffset Time { get; set; }
        public string DeviceId { get; set; } = null!;
        public double Value { get; set; }
    }

    private class HourlyAvg
    {
        public DateTimeOffset Bucket { get; set; }
        public string DeviceId { get; set; } = null!;
        public double Avg { get; set; }
    }

    private static void MapReading(ModelBuilder mb, bool plain)
        => mb.Entity<Reading>(e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Value).HasColumnName("value");

            if (plain)
            {
                return;
            }

            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
            e.HasSpacePartition(x => x.DeviceId, 4);
            e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId).OrderByDescending(x => x.Time));
            e.HasColumnstorePolicy(7, Every.Day);
            e.HasRetentionPolicy(90, Every.Day);
            e.HasIndex(x => x.Time);
            e.HasReorderPolicy(x => x.Time);
        });

    private static readonly Action<ModelBuilder> Full = mb =>
    {
        MapReading(mb, plain: false);
        mb.Entity<HourlyAvg>(e =>
        {
            e.HasNoKey();
            e.Property(x => x.Bucket).HasColumnName("bucket");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Avg).HasColumnName("avg");
            e.IsContinuousAggregate("hourly_avg",
                "SELECT time_bucket(INTERVAL '1 hour', time) AS bucket, device_id, avg(value) AS avg "
                + "FROM readings GROUP BY 1, 2");
            e.HasRefreshPolicy(TimeSpan.FromDays(3), TimeSpan.FromHours(1), scheduleInterval: TimeSpan.FromHours(1));
        });
        mb.HasTimescaleDbJob("cleanup", "public.cleanup", scheduleInterval: TimeSpan.FromDays(1));
    };

    private static readonly Action<ModelBuilder> Plain = mb => MapReading(mb, plain: true);

    [Fact]
    public async Task Full_model_then_exact_reverse_returns_to_baseline()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        // The job references a stored procedure that must exist before add_job.
        await ExecuteAsync(cs,
            "CREATE PROCEDURE public.cleanup(job_id int, config jsonb) LANGUAGE plpgsql AS $$ BEGIN END $$;", ct);

        // Forward: build everything.
        await DiffExecutor.ApplyAsync(cs, null, Full, ct);
        await ExecuteAsync(cs,
            """
            INSERT INTO readings (time, device_id, value)
            SELECT '2026-06-01T00:00:00Z'::timestamptz + (n || ' hours')::interval, 'd' || (n % 4), n
            FROM generate_series(1, 48) AS n
            """, ct);

        Assert.Equal(1L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'readings'", ct));
        Assert.Equal(1L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.continuous_aggregates WHERE view_name = 'hourly_avg'", ct));
        Assert.Equal(1L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.jobs WHERE application_name LIKE 'cleanup%'", ct));

        // Applying the same model again is a no-op (snapshot stability).
        Assert.Empty(DiffExecutor.GenerateSql(cs, Full, Full));

        // Reverse: Diff(full, plain) — the engine's automatic Down.
        await DiffExecutor.ApplyAsync(cs, Full, Plain, ct);

        Assert.Equal(0L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'readings'", ct));
        Assert.Equal(0L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.continuous_aggregates WHERE view_name = 'hourly_avg'", ct));
        Assert.Equal(0L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.jobs WHERE application_name LIKE 'cleanup%'", ct));

        // The plain table and every row survived the un-hypertable rebuild.
        Assert.Equal(48L, await ScalarAsync(cs, "SELECT count(*) FROM readings", ct));
        Assert.Equal(1176.0d, Convert.ToDouble(await ScalarAsync(cs, "SELECT sum(value) FROM readings", ct)));

        // Baseline is stable too.
        Assert.Empty(DiffExecutor.GenerateSql(cs, Plain, Plain));
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
