using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Migrations;

/// <summary>
///     Executable proof of "Down for free": a reverse migration is just <c>Diff(target, source)</c>.
///     For every feature, the forward diff emits the additive op and the reverse diff emits the exact
///     inverse — no imperative Down code anywhere in the engine.
/// </summary>
public class ReversibilityTests
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

    private const string CaggQuery =
        "SELECT time_bucket('1 hour', time) AS bucket, avg(value) AS avg FROM readings GROUP BY 1";

    private static Action<EntityTypeBuilder<Reading>> Configure()
        => e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Value).HasColumnName("value");
        };

    private static Action<ModelBuilder> Plain => mb => mb.Entity<Reading>(Configure());

    private static Action<ModelBuilder> Hypertable(Action<EntityTypeBuilder<Reading>>? extra = null)
        => mb => mb.Entity<Reading>(e =>
        {
            Configure()(e);
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
            extra?.Invoke(e);
        });

    /// <summary>Asserts the forward diff contains <paramref name="forward" /> and the reverse contains <paramref name="reverse" />.</summary>
    private static void AssertInverse(
        Action<ModelBuilder> a,
        Action<ModelBuilder> b,
        string forward,
        string reverse)
    {
        var fwd = MigrationSqlHelper.GenerateSql(a, b);
        var rev = MigrationSqlHelper.GenerateSql(b, a);

        Assert.Contains(fwd, s => s.Contains(forward));
        Assert.Contains(rev, s => s.Contains(reverse));
    }

    [Fact]
    public void Hypertable_conversion_inverts()
    {
        // Forward: plain → hypertable (migrate_data). Reverse: hypertable → plain (rebuild, no hypertable).
        var fwd = MigrationSqlHelper.GenerateSql(Plain, Hypertable());
        var rev = MigrationSqlHelper.GenerateSql(Hypertable(), Plain);

        Assert.Contains(fwd, s => s.Contains("create_hypertable") && s.Contains("migrate_data => true"));
        Assert.Contains(rev, s => s.Contains("__ts_rebuild"));
        Assert.DoesNotContain(rev, s => s.Contains("create_hypertable"));
    }

    [Fact]
    public void Retention_inverts()
        => AssertInverse(
            Hypertable(),
            Hypertable(e => e.HasRetentionPolicy(90, Every.Day)),
            forward: "add_retention_policy",
            reverse: "remove_retention_policy");

    [Fact]
    public void Columnstore_policy_inverts()
        => AssertInverse(
            Hypertable(e => e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId))),
            Hypertable(e =>
            {
                e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId));
                e.HasColumnstorePolicy(7, Every.Day);
            }),
            forward: "add_columnstore_policy",
            reverse: "remove_columnstore_policy");

    [Fact]
    public void Reorder_inverts()
        => AssertInverse(
            Hypertable(),
            Hypertable(e => e.HasReorderPolicy("readings_time_idx")),
            forward: "add_reorder_policy",
            reverse: "remove_reorder_policy");

    [Fact]
    public void Tablespace_inverts()
        => AssertInverse(
            Hypertable(),
            Hypertable(e => e.HasTablespace(new Tablespace("ts1"))),
            forward: "attach_tablespace",
            reverse: "detach_tablespace");

    [Fact]
    public void Columnstore_inverts()
    {
        var fwd = MigrationSqlHelper.GenerateSql(
            Hypertable(),
            Hypertable(e => e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId))));
        var rev = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId))),
            Hypertable());

        Assert.Contains(fwd, s => s.Contains("timescaledb.enable_columnstore = true"));
        Assert.Contains(rev, s => s.Contains("convert_to_rowstore")
            && s.Contains("timescaledb.enable_columnstore = false"));
    }

    [Fact]
    public void Chunk_skipping_inverts()
        => AssertInverse(
            Hypertable(),
            Hypertable(e => e.HasChunkSkipping(x => x.Value)),
            forward: "enable_chunk_skipping",
            reverse: "disable_chunk_skipping");

    [Fact]
    public void Space_dimension_inverts()
    {
        // Forward adds in place; reverse can't drop a dimension in place, so it rebuilds.
        var fwd = MigrationSqlHelper.GenerateSql(
            Hypertable(),
            Hypertable(e => e.HasSpacePartition(x => x.DeviceId, 4)));
        var rev = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasSpacePartition(x => x.DeviceId, 4)),
            Hypertable());

        Assert.Contains(fwd, s => s.Contains("add_dimension('\"readings\"', by_hash('device_id', 4));"));
        Assert.DoesNotContain(fwd, s => s.Contains("__ts_rebuild"));
        Assert.Contains(rev, s => s.Contains("__ts_rebuild"));
    }

    [Fact]
    public void Continuous_aggregate_inverts()
    {
        Action<ModelBuilder> withCagg = mb =>
        {
            Hypertable()(mb);
            mb.Entity<HourlyAvg>(e =>
            {
                e.HasNoKey();
                e.Property(x => x.Bucket).HasColumnName("bucket");
                e.Property(x => x.Avg).HasColumnName("avg");
                e.IsContinuousAggregate("hourly_avg", CaggQuery);
            });
        };

        AssertInverse(
            Hypertable(),
            withCagg,
            forward: "CREATE MATERIALIZED VIEW \"hourly_avg\"",
            reverse: "DROP MATERIALIZED VIEW IF EXISTS \"hourly_avg\"");
    }

    [Fact]
    public void Job_inverts()
    {
        Action<ModelBuilder> withJob = mb =>
        {
            Hypertable()(mb);
            mb.HasTimescaleDbJob("nightly", "public.cleanup", scheduleInterval: TimeSpan.FromDays(1));
        };

        AssertInverse(
            Hypertable(),
            withJob,
            forward: "add_job",
            reverse: "delete_job");
    }
}
