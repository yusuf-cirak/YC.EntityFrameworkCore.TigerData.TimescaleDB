using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using Microsoft.EntityFrameworkCore;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Migrations;

/// <summary>
///     Every emitted TimescaleDB statement must address its relation schema-qualified when the table
///     or view lives outside the default schema; an unqualified reference would resolve against
///     <c>search_path</c> and silently hit the wrong (or no) object in production.
/// </summary>
public class SchemaQualificationTests
{
    private class Reading
    {
        public DateTimeOffset Time { get; set; }
        public string DeviceId { get; set; } = null!;
        public double Value { get; set; }
    }

    private class HourlyAvg
    {
        public DateTime Bucket { get; set; }
        public double Avg { get; set; }
    }

    private const string Schema = "ts";

    private static Action<ModelBuilder> FullModel => mb =>
    {
        mb.Entity<Reading>(e =>
        {
            e.HasNoKey();
            e.ToTable("readings", Schema);
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Value).HasColumnName("value");
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
            e.HasSpacePartition(x => x.DeviceId, 4);
            e.HasTablespace(new Tablespace("ts1"));
            e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId));
            e.HasColumnstorePolicy(7, Every.Day);
            e.HasRetentionPolicy(90, Every.Day);
            e.HasChunkSkipping(x => x.Value);
            e.HasReorderPolicy("readings_time_idx");
        });

        mb.Entity<HourlyAvg>(e =>
        {
            e.HasNoKey();
            e.Property(x => x.Bucket).HasColumnName("bucket");
            e.Property(x => x.Avg).HasColumnName("avg");
            e.IsContinuousAggregate(
                "hourly_avg",
                "SELECT time_bucket('1 hour', time) AS bucket, avg(value) AS avg FROM ts.readings GROUP BY 1",
                schema: Schema);
            e.HasRefreshPolicy(TimeSpan.FromDays(3), TimeSpan.FromHours(1));
        });
    };

    [Fact]
    public void Hypertable_statements_are_schema_qualified()
    {
        var sql = MigrationSqlHelper.GenerateSql(FullModel);
        var joined = string.Join("\n---\n", sql);

        Assert.Contains(sql, s => s.Contains("create_hypertable('\"ts\".\"readings\"'"));
        Assert.Contains(sql, s => s.Contains("add_dimension('\"ts\".\"readings\"', by_hash('device_id', 4));"));
        Assert.Contains(sql, s => s.Contains("attach_tablespace('ts1', '\"ts\".\"readings\"'"));
        Assert.Contains(sql, s => s.Contains("ALTER TABLE \"ts\".\"readings\" SET (timescaledb.enable_columnstore"));
        Assert.Contains(sql, s => s.Contains("add_columnstore_policy('\"ts\".\"readings\"'"));
        Assert.Contains(sql, s => s.Contains("add_retention_policy('\"ts\".\"readings\"'"));
        Assert.Contains(sql, s => s.Contains("enable_chunk_skipping('\"ts\".\"readings\"'"));
        Assert.Contains(sql, s => s.Contains("add_reorder_policy('\"ts\".\"readings\"'"));

        // No TimescaleDB statement may reference the table unqualified.
        Assert.DoesNotContain(sql, s =>
            (s.Contains("create_hypertable") || s.Contains("add_dimension") || s.Contains("attach_tablespace")
             || s.Contains("add_retention_policy") || s.Contains("add_columnstore_policy")
             || s.Contains("add_reorder_policy") || s.Contains("enable_chunk_skipping"))
            && s.Contains("'\"readings\"'"));

        Assert.False(string.IsNullOrEmpty(joined));
    }

    [Fact]
    public void Continuous_aggregate_statements_are_schema_qualified()
    {
        var sql = MigrationSqlHelper.GenerateSql(FullModel);

        Assert.Contains(sql, s => s.Contains("CREATE MATERIALIZED VIEW \"ts\".\"hourly_avg\""));
        Assert.Contains(sql, s => s.Contains("add_continuous_aggregate_policy('\"ts\".\"hourly_avg\"'"));
    }

    [Fact]
    public void Schema_qualified_disable_columnstore_targets_the_right_schema()
    {
        // Disabling the columnstore decompresses chunks via a catalog query keyed by schema + name.
        var with = FullModel;
        Action<ModelBuilder> without = mb =>
        {
            mb.Entity<Reading>(e =>
            {
                e.HasNoKey();
                e.ToTable("readings", Schema);
                e.Property(x => x.Time).HasColumnName("time");
                e.Property(x => x.DeviceId).HasColumnName("device_id");
                e.Property(x => x.Value).HasColumnName("value");
                e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
                e.HasSpacePartition(x => x.DeviceId, 4);
                e.HasTablespace(new Tablespace("ts1"));
                e.HasChunkSkipping(x => x.Value);
                e.HasReorderPolicy("readings_time_idx");
                e.HasRetentionPolicy(90, Every.Day);
            });
        };

        var sql = MigrationSqlHelper.GenerateSql(with, without);

        var disable = Assert.Single(sql, s => s.Contains("convert_to_rowstore"));
        Assert.Contains("hypertable_schema = 'ts'", disable);
        Assert.Contains("hypertable_name = 'readings'", disable);
        Assert.Contains("ALTER TABLE \"ts\".\"readings\" SET (timescaledb.enable_columnstore = false)", disable);
    }
}
