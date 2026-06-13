using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Migrations;

/// <summary>v5: multiple space dimensions, tablespaces, job reliability, hierarchical continuous aggregates.</summary>
public class V5SchemaFeaturesTests
{
    private class Reading
    {
        public DateTimeOffset Time { get; set; }
        public string DeviceId { get; set; } = null!;
        public string Region { get; set; } = null!;
        public double Value { get; set; }
    }

    private static Action<EntityTypeBuilder<Reading>> Map()
        => e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Region).HasColumnName("region");
            e.Property(x => x.Value).HasColumnName("value");
        };

    private static Action<ModelBuilder> Hypertable(Action<EntityTypeBuilder<Reading>>? extra = null)
        => mb => mb.Entity<Reading>(e =>
        {
            Map()(e);
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
            extra?.Invoke(e);
        });

    // ---------------------------------------------------------------- multiple space dimensions

    [Fact]
    public void Two_space_dimensions_emit_two_add_dimension()
    {
        var sql = MigrationSqlHelper.GenerateSql(Hypertable(e =>
        {
            e.HasSpacePartition(x => x.DeviceId, 4);
            e.HasSpacePartition(x => x.Region, 2);
        }));

        Assert.Contains(sql, s => s.Contains("SELECT add_dimension('\"readings\"', by_hash('device_id', 4));"));
        Assert.Contains(sql, s => s.Contains("SELECT add_dimension('\"readings\"', by_hash('region', 2));"));
    }

    [Fact]
    public void Adding_a_second_dimension_is_in_place()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasSpacePartition(x => x.DeviceId, 4)),
            Hypertable(e =>
            {
                e.HasSpacePartition(x => x.DeviceId, 4);
                e.HasSpacePartition(x => x.Region, 2);
            }));

        Assert.Contains(sql, s => s.Contains("add_dimension('\"readings\"', by_hash('region', 2));"));
        Assert.DoesNotContain(sql, s => s.Contains("__ts_rebuild"));
    }

    [Fact]
    public void Removing_a_dimension_rebuilds()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e =>
            {
                e.HasSpacePartition(x => x.DeviceId, 4);
                e.HasSpacePartition(x => x.Region, 2);
            }),
            Hypertable(e => e.HasSpacePartition(x => x.DeviceId, 4)));

        Assert.Contains(sql, s => s.Contains("__ts_rebuild"));
    }

    // ---------------------------------------------------------------- tablespaces

    [Fact]
    public void Tablespace_attach_and_detach_are_in_place_and_reversible()
    {
        var sql = MigrationSqlHelper.GenerateSql(Hypertable(e => e.HasTablespace(new Tablespace("ts1"))));
        Assert.Contains(sql, s => s.Contains(
            "SELECT attach_tablespace('ts1', '\"readings\"', if_not_attached => true);"));

        var removed = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasTablespace(new Tablespace("ts1"))),
            Hypertable());
        Assert.Contains(removed, s => s.Contains(
            "SELECT detach_tablespace('ts1', '\"readings\"', if_attached => true);"));
        Assert.DoesNotContain(removed, s => s.Contains("__ts_rebuild"));
    }

    // ---------------------------------------------------------------- job reliability

    [Fact]
    public void Job_reliability_args_emitted()
    {
        var sql = MigrationSqlHelper.GenerateSql(mb =>
        {
            Hypertable()(mb);
            mb.HasTimescaleDbJob("j", "public.proc",
                scheduleInterval: TimeSpan.FromHours(1),
                maxRuntime: TimeSpan.FromMinutes(5),
                maxRetries: 3,
                retryPeriod: TimeSpan.FromMinutes(10));
        });

        var add = Assert.Single(sql, s => s.Contains("add_job("));
        Assert.Contains("max_runtime => INTERVAL '00:05:00'", add);
        Assert.Contains("retry_period => INTERVAL '00:10:00'", add);
        Assert.Contains("max_retries => 3", add);
    }

    // ---------------------------------------------------------------- hierarchical caggs

    private class Hourly { public DateTimeOffset Bucket { get; set; } public double Avg { get; set; } }
    private class Daily { public DateTimeOffset Bucket { get; set; } public double Avg { get; set; } }

    private static Action<ModelBuilder> Hierarchy(string dailyQuery) => mb =>
    {
        Hypertable()(mb);
        mb.Entity<Hourly>(e =>
        {
            e.HasNoKey();
            e.IsContinuousAggregate("hourly",
                "SELECT time_bucket('1 hour', time) AS bucket, avg(value) AS avg FROM readings GROUP BY 1");
        });
        mb.Entity<Daily>(e =>
        {
            e.HasNoKey();
            e.IsContinuousAggregate("daily", dailyQuery);
        });
    };

    private const string DailyOverHourly =
        "SELECT time_bucket('1 day', bucket) AS bucket, avg(avg) AS avg FROM hourly GROUP BY 1";

    [Fact]
    public void Hierarchical_cagg_created_after_its_source()
    {
        var sql = MigrationSqlHelper.GenerateSql(Hierarchy(DailyOverHourly)).ToList();

        var hourly = sql.FindIndex(s => s.Contains("CREATE MATERIALIZED VIEW \"hourly\""));
        var daily = sql.FindIndex(s => s.Contains("CREATE MATERIALIZED VIEW \"daily\""));

        Assert.True(hourly >= 0 && daily > hourly, string.Join("\n---\n", sql));
    }

    [Fact]
    public void Hierarchical_cagg_dropped_before_its_source()
    {
        var sql = MigrationSqlHelper.GenerateSql(Hierarchy(DailyOverHourly), Hypertable()).ToList();

        var dropDaily = sql.FindIndex(s => s.Contains("DROP MATERIALIZED VIEW IF EXISTS \"daily\""));
        var dropHourly = sql.FindIndex(s => s.Contains("DROP MATERIALIZED VIEW IF EXISTS \"hourly\""));

        Assert.True(dropDaily >= 0 && dropHourly > dropDaily, string.Join("\n---\n", sql));
    }

    [Fact]
    public void Changing_source_cagg_recreates_dependent()
    {
        const string newHourly =
            "SELECT time_bucket('2 hours', time) AS bucket, avg(value) AS avg FROM readings GROUP BY 1";

        Action<ModelBuilder> Model(string hourlyQuery) => mb =>
        {
            Hypertable()(mb);
            mb.Entity<Hourly>(e => { e.HasNoKey(); e.IsContinuousAggregate("hourly", hourlyQuery); });
            mb.Entity<Daily>(e => { e.HasNoKey(); e.IsContinuousAggregate("daily", DailyOverHourly); });
        };

        var hourly0 = "SELECT time_bucket('1 hour', time) AS bucket, avg(value) AS avg FROM readings GROUP BY 1";
        var sql = MigrationSqlHelper.GenerateSql(Model(hourly0), Model(newHourly));

        // Both recreated even though only hourly's query changed.
        Assert.Contains(sql, s => s.Contains("CREATE MATERIALIZED VIEW \"daily\""));
        Assert.Contains(sql, s => s.Contains("DROP MATERIALIZED VIEW IF EXISTS \"daily\""));
    }

    // ---------------------------------------------------------------- idempotency

    [Fact]
    public void All_features_idempotent()
    {
        Action<ModelBuilder> full = mb =>
        {
            mb.Entity<Reading>(e =>
            {
                Map()(e);
                e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
                e.HasSpacePartition(x => x.DeviceId, 4);
                e.HasSpacePartition(x => x.Region, 2);
                e.HasTablespace(new Tablespace("ts1"));
                e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId).OrderByDescending(x => x.Time));
                e.HasColumnstorePolicy(7, Every.Day);
                e.HasRetentionPolicy(90, Every.Day);
                e.HasIndex(x => x.Time);
                e.HasReorderPolicy(x => x.Time);
            });
            mb.HasTimescaleDbJob("j", "public.proc", maxRuntime: TimeSpan.FromMinutes(5), maxRetries: 3);
            mb.Entity<Hourly>(e =>
            {
                e.HasNoKey();
                e.IsContinuousAggregate("hourly",
                    "SELECT time_bucket('1 hour', time) AS bucket, avg(value) AS avg FROM readings GROUP BY 1");
                e.HasRefreshPolicy(TimeSpan.FromDays(3), TimeSpan.FromHours(1), scheduleInterval: TimeSpan.FromHours(1));
                e.HasRetentionPolicy(365, Every.Day);
            });
            mb.Entity<Daily>(e => { e.HasNoKey(); e.IsContinuousAggregate("daily", DailyOverHourly); });
        };

        Assert.Empty(MigrationSqlHelper.GenerateSql(full, full));
    }
}
