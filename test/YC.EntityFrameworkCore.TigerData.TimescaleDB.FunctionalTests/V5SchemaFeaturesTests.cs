using Microsoft.EntityFrameworkCore;
using Npgsql;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests;

[Collection(TimescaleDbCollection.Name)]
public class V5SchemaFeaturesTests(TimescaleDbContainerFixture fixture)
{
    private class Sensor
    {
        public DateTimeOffset Time { get; set; }
        public string Device { get; set; } = null!;
        public string Region { get; set; } = null!;
        public double Value { get; set; }
    }

    private class Hourly
    {
        public DateTimeOffset Bucket { get; set; }
        public string Device { get; set; } = null!;
        public double Avg { get; set; }
    }

    private class Daily
    {
        public DateTimeOffset Bucket { get; set; }
        public double Avg { get; set; }
    }

    private static void MapSensor(ModelBuilder mb, Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Sensor>>? extra = null)
        => mb.Entity<Sensor>(e =>
        {
            e.HasNoKey();
            e.ToTable("sensors");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.Device).HasColumnName("device");
            e.Property(x => x.Region).HasColumnName("region");
            e.Property(x => x.Value).HasColumnName("value");
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
            extra?.Invoke(e);
        });

    [Fact]
    public async Task Multiple_space_dimensions_created()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        await DiffExecutor.ApplyAsync(cs, null, mb => MapSensor(mb, e =>
        {
            e.HasSpacePartition(x => x.Device, 4);
            e.HasSpacePartition(x => x.Region, 2);
        }), ct);

        // 1 time + 2 space dimensions.
        Assert.Equal(3L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.dimensions WHERE hypertable_name = 'sensors'", ct));
    }

    [Fact]
    public async Task Tablespace_attached_then_detached()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!await fixture.TryCreateTablespaceAsync("ts_v5", ct))
        {
            return; // environment can't host a tablespace; SQL generation is covered by unit tests
        }

        var cs = await fixture.CreateDatabaseAsync(ct);

        await DiffExecutor.ApplyAsync(cs, null, mb => MapSensor(mb, e => e.HasTablespace(new Tablespace("ts_v5"))), ct);
        Assert.Equal(1L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.hypertables "
            + "WHERE hypertable_name = 'sensors' AND 'ts_v5' = ANY(tablespaces)", ct));

        await DiffExecutor.ApplyAsync(cs,
            mb => MapSensor(mb, e => e.HasTablespace(new Tablespace("ts_v5"))), mb => MapSensor(mb), ct);
        Assert.Equal(0L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.hypertables "
            + "WHERE hypertable_name = 'sensors' AND 'ts_v5' = ANY(tablespaces)", ct));
    }

    [Fact]
    public async Task Hierarchical_continuous_aggregate_applies_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        Action<ModelBuilder> model = mb =>
        {
            MapSensor(mb);
            mb.Entity<Hourly>(e =>
            {
                e.HasNoKey();
                e.Property(x => x.Bucket).HasColumnName("bucket");
                e.Property(x => x.Device).HasColumnName("device");
                e.Property(x => x.Avg).HasColumnName("avg");
                e.IsContinuousAggregate("hourly",
                    "SELECT time_bucket(INTERVAL '1 hour', time) AS bucket, device, avg(value) AS avg "
                    + "FROM sensors GROUP BY 1, 2");
            });
            mb.Entity<Daily>(e =>
            {
                e.HasNoKey();
                e.Property(x => x.Bucket).HasColumnName("bucket");
                e.Property(x => x.Avg).HasColumnName("avg");
                e.IsContinuousAggregate("daily",
                    "SELECT time_bucket(INTERVAL '1 day', bucket) AS bucket, avg(avg) AS avg "
                    + "FROM hourly GROUP BY 1");
            });
        };

        // Creates the source hypertable, then hourly, then daily-over-hourly — must not error.
        await DiffExecutor.ApplyAsync(cs, null, model, ct);

        Assert.Equal(2L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.continuous_aggregates "
            + "WHERE view_name IN ('hourly', 'daily')", ct));

        // Tear down: daily must drop before hourly.
        await DiffExecutor.ApplyAsync(cs, model, mb => MapSensor(mb), ct);
        Assert.Equal(0L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.continuous_aggregates "
            + "WHERE view_name IN ('hourly', 'daily')", ct));
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
