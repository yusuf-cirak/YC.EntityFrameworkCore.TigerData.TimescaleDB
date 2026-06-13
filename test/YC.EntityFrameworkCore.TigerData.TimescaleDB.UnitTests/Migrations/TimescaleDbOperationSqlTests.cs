using Microsoft.EntityFrameworkCore;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Migrations;

/// <summary>
///     Non-table objects (extension, continuous aggregates, jobs) emitted by the differ as
///     <c>migrationBuilder.Sql(...)</c>, and their reversals.
/// </summary>
public class TimescaleDbOperationSqlTests
{
    private class Reading
    {
        public DateTime Time { get; set; }
        public string DeviceId { get; set; } = null!;
        public double Value { get; set; }
    }

    private class HourlyAvg
    {
        public DateTime Bucket { get; set; }
        public double Avg { get; set; }
    }

    private const string CaggQuery =
        "SELECT time_bucket('1 hour', time) AS bucket, avg(value) AS avg FROM readings GROUP BY 1";

    private static void Hypertable(ModelBuilder mb) => mb.Entity<Reading>(e =>
    {
        e.HasNoKey();
        e.ToTable("readings");
        e.Property(x => x.Time).HasColumnName("time");
        e.Property(x => x.DeviceId).HasColumnName("device_id");
        e.Property(x => x.Value).HasColumnName("value");
        e.IsHypertable(x => x.Time, chunkInterval: "1 day");
    });

    [Fact]
    public void Initial_migration_emits_extension_before_create_table()
    {
        var sql = MigrationSqlHelper.GenerateSql(Hypertable);
        var list = sql.ToList();

        var extension = list.FindIndex(s => s.Contains("CREATE EXTENSION IF NOT EXISTS timescaledb"));
        var createTable = list.FindIndex(s => s.Contains("CREATE TABLE"));

        Assert.True(extension >= 0 && extension < createTable, string.Join("\n---\n", sql));
    }

    [Fact]
    public void Continuous_aggregate_and_job_emitted_as_sql()
    {
        var sql = MigrationSqlHelper.GenerateSql(mb =>
        {
            Hypertable(mb);
            mb.Entity<HourlyAvg>(e =>
            {
                e.HasNoKey();
                e.Property(x => x.Bucket).HasColumnName("bucket");
                e.Property(x => x.Avg).HasColumnName("avg");
                e.IsContinuousAggregate("hourly_avg", CaggQuery);
                e.HasRefreshPolicy(startOffset: "3 days", endOffset: "1 hour", scheduleInterval: "1 hour");
            });
            mb.HasTimescaleDbJob("nightly_cleanup", "public.cleanup", scheduleInterval: "1 day",
                config: """{"drop_after":"30 days"}""");
        });

        Assert.Contains(sql, s => s.Contains("CREATE MATERIALIZED VIEW \"hourly_avg\"")
            && s.Contains("WITH (timescaledb.continuous)")
            && s.Contains(CaggQuery)
            && s.Contains("WITH NO DATA"));
        Assert.Contains(sql, s => s.Contains(
            "SELECT add_continuous_aggregate_policy('\"hourly_avg\"', start_offset => INTERVAL '3 days', "
            + "end_offset => INTERVAL '1 hour', schedule_interval => INTERVAL '1 hour');"));
        Assert.Contains(sql, s => s.Contains("SELECT add_job('public.cleanup'::regproc")
            && s.Contains("schedule_interval => INTERVAL '1 day'")
            && s.Contains("""config => '{"drop_after":"30 days"}'::jsonb""")
            && s.Contains("job_name => 'nightly_cleanup'"));
    }

    [Fact]
    public void Dropping_continuous_aggregate_and_job_emits_drop_and_delete()
    {
        Action<ModelBuilder> withCagg = mb =>
        {
            Hypertable(mb);
            mb.Entity<HourlyAvg>(e =>
            {
                e.HasNoKey();
                e.Property(x => x.Bucket).HasColumnName("bucket");
                e.Property(x => x.Avg).HasColumnName("avg");
                e.IsContinuousAggregate("hourly_avg", CaggQuery);
            });
            mb.HasTimescaleDbJob("nightly_cleanup", "public.cleanup");
        };

        var sql = MigrationSqlHelper.GenerateSql(withCagg, Hypertable);

        Assert.Contains(sql, s => s.Contains("DROP MATERIALIZED VIEW IF EXISTS \"hourly_avg\";"));
        Assert.Contains(sql, s => s.Contains("delete_job") && s.Contains("'nightly_cleanup'"));
    }

    [Fact]
    public void Toggling_materialized_only_emits_alter_materialized_view()
    {
        Action<ModelBuilder> Cagg(bool materializedOnly) => mb => mb.Entity<HourlyAvg>(e =>
        {
            e.HasNoKey();
            e.IsContinuousAggregate("hourly_avg", CaggQuery, materializedOnly: materializedOnly);
        });

        var sql = MigrationSqlHelper.GenerateSql(Cagg(true), Cagg(false));

        Assert.Contains(sql, s => s.Contains(
            "ALTER MATERIALIZED VIEW \"hourly_avg\" SET (timescaledb.materialized_only = false);"));
        Assert.DoesNotContain(sql, s => s.Contains("DROP MATERIALIZED VIEW"));
    }

    [Fact]
    public void Changing_cagg_query_recreates_view_and_refresh_policy()
    {
        Action<ModelBuilder> Cagg(string query) => mb => mb.Entity<HourlyAvg>(e =>
        {
            e.HasNoKey();
            e.IsContinuousAggregate("hourly_avg", query);
            e.HasRefreshPolicy(startOffset: "3 days", endOffset: "1 hour");
        });

        const string newQuery =
            "SELECT time_bucket('2 hours', time) AS bucket, avg(value) AS avg FROM readings GROUP BY 1";

        var sql = MigrationSqlHelper.GenerateSql(Cagg(CaggQuery), Cagg(newQuery));

        Assert.Contains(sql, s => s.Contains("DROP MATERIALIZED VIEW IF EXISTS \"hourly_avg\";"));
        Assert.Contains(sql, s => s.Contains("CREATE MATERIALIZED VIEW \"hourly_avg\"") && s.Contains(newQuery));
        Assert.Contains(sql, s => s.Contains("add_continuous_aggregate_policy"));
    }

    [Fact]
    public void Cagg_columnstore_emits_alter_materialized_view()
    {
        Action<ModelBuilder> Cagg(bool columnstore) => mb => mb.Entity<HourlyAvg>(e =>
        {
            e.HasNoKey();
            e.Property(x => x.Bucket).HasColumnName("bucket");
            e.IsContinuousAggregate("hourly_avg", CaggQuery);
            if (columnstore)
            {
                e.HasColumnstore(cs => cs.OrderByDescending(x => x.Bucket));
            }
        });

        var sql = MigrationSqlHelper.GenerateSql(Cagg(false), Cagg(true));

        Assert.Contains(sql, s => s.Contains(
            "ALTER MATERIALIZED VIEW \"hourly_avg\" SET (timescaledb.enable_columnstore = true, "
            + "timescaledb.orderby = 'bucket DESC');"));
    }

    [Fact]
    public void No_extension_for_plain_model()
    {
        var sql = MigrationSqlHelper.GenerateSql(mb => mb.Entity<Reading>(e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
        }));

        Assert.DoesNotContain(sql, s => s.Contains("CREATE EXTENSION"));
    }
}
