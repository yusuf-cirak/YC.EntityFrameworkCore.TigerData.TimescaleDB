using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using Microsoft.EntityFrameworkCore;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Migrations;

/// <summary>Initial <c>CREATE TABLE</c> of a hypertable emits the post-create TimescaleDB DDL.</summary>
public class HypertableSqlGenerationTests
{
    private class Reading
    {
        public DateTime Time { get; set; }
        public string DeviceId { get; set; } = null!;
        public double Value { get; set; }
    }

    [Fact]
    public void Create_hypertable_emits_create_hypertable_after_create_table()
    {
        var sql = MigrationSqlHelper.GenerateSql(mb => mb.Entity<Reading>(e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
            e.Property(x => x.Time).HasColumnName("time");
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
        }));

        var list = sql.ToList();
        var createTable = list.FindIndex(s => s.Contains("CREATE TABLE", StringComparison.Ordinal));
        var hypertable = list.FindIndex(s => s.Contains("create_hypertable", StringComparison.Ordinal));

        Assert.True(createTable >= 0 && hypertable > createTable, string.Join("\n---\n", sql));
        Assert.Contains(sql, s => s.Contains(
            "SELECT create_hypertable('\"readings\"', by_range('time', INTERVAL '1 day'));"));

        // No Npgsql storage-parameter WITH clause for timescale settings.
        Assert.DoesNotContain(sql, s => s.Contains("CREATE TABLE") && s.Contains("timescaledb"));
    }

    [Fact]
    public void Create_hypertable_emits_columnstore_dimension_and_policies()
    {
        var sql = MigrationSqlHelper.GenerateSql(mb => mb.Entity<Reading>(e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.IsHypertable(x => x.Time);
            e.HasSpacePartition(x => x.DeviceId, partitions: 4);
            e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId).OrderByDescending(x => x.Time));
            e.HasColumnstorePolicy(7, Every.Day);
            e.HasRetentionPolicy(90, Every.Day);
        }));

        Assert.Contains(sql, s => s.Contains("SELECT add_dimension('\"readings\"', by_hash('device_id', 4));"));
        Assert.Contains(sql, s => s.Contains(
            "ALTER TABLE \"readings\" SET (timescaledb.enable_columnstore = true, "
            + "timescaledb.segmentby = 'device_id', timescaledb.orderby = 'time DESC');"));
        Assert.Contains(sql, s => s.Contains("CALL add_columnstore_policy('\"readings\"', after => INTERVAL '7 days');"));
        Assert.Contains(sql, s => s.Contains(
            "SELECT add_retention_policy('\"readings\"', drop_after => INTERVAL '90 days');"));
    }

    [Fact]
    public void Integer_hypertable_emits_numeric_interval_and_integer_now()
    {
        var sql = MigrationSqlHelper.GenerateSql(mb => mb.Entity<IntegerSeries>(e =>
        {
            e.HasNoKey();
            e.ToTable("series");
            e.Property(x => x.UnixMicros).HasColumnName("unix_micros");
            e.IsHypertableByInteger(x => x.UnixMicros, 86_400_000_000, integerNowFunction: "micros_now");
        }));

        Assert.Contains(sql, s => s.Contains(
            "SELECT create_hypertable('\"series\"', by_range('unix_micros', 86400000000));"));
        Assert.Contains(sql, s => s.Contains("SELECT set_integer_now_func('\"series\"', 'micros_now');"));
    }

    [Fact]
    public void Plain_table_emits_no_timescaledb_sql()
    {
        var sql = MigrationSqlHelper.GenerateSql(mb => mb.Entity<Reading>(e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
        }));

        Assert.DoesNotContain(sql, s => s.Contains("timescaledb") || s.Contains("create_hypertable"));
    }

    private class IntegerSeries
    {
        public long UnixMicros { get; set; }
        public double Value { get; set; }
    }
}
