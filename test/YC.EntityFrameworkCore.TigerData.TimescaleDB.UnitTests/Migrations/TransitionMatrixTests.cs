using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Migrations;

/// <summary>
///     The snapshot↔model transition matrix: forward in-place changes, reversals, and the
///     rebuild path for changes TimescaleDB cannot apply in place. Every reversal is derived
///     automatically from the annotation delta — there are no imperative migration commands.
/// </summary>
public class TransitionMatrixTests
{
    private class Reading
    {
        public DateTimeOffset Time { get; set; }
        public string DeviceId { get; set; } = null!;
        public double Value { get; set; }
    }

    private static Action<ModelBuilder> Plain => mb => mb.Entity<Reading>(Configure());

    private static Action<ModelBuilder> Hypertable(Action<EntityTypeBuilder<Reading>>? extra = null)
        => mb => mb.Entity<Reading>(e =>
        {
            Configure()(e);
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
            extra?.Invoke(e);
        });

    private static Action<EntityTypeBuilder<Reading>> Configure()
        => e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Value).HasColumnName("value");
        };

    // ---------------------------------------------------------------- forward in-place

    [Fact]
    public void Plain_to_hypertable_converts_with_migrate_data()
    {
        var sql = MigrationSqlHelper.GenerateSql(Plain, Hypertable());

        Assert.Contains(sql, s => s.Contains(
            "SELECT create_hypertable('\"readings\"', by_range('time', INTERVAL '1 day'), migrate_data => true);"));
    }

    [Fact]
    public void Chunk_interval_change_sets_chunk_time_interval()
    {
        Action<ModelBuilder> wider = mb => mb.Entity<Reading>(e =>
        {
            Configure()(e);
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(7));
        });

        var sql = MigrationSqlHelper.GenerateSql(Hypertable(), wider);

        Assert.Contains(sql, s => s.Contains("SELECT set_chunk_time_interval('\"readings\"', INTERVAL '7 days');"));
        Assert.DoesNotContain(sql, s => s.Contains("create_hypertable") || s.Contains("__ts_rebuild"));
    }

    [Fact]
    public void Space_partition_added_emits_add_dimension()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(),
            Hypertable(e => e.HasSpacePartition(x => x.DeviceId, partitions: 4)));

        Assert.Contains(sql, s => s.Contains("SELECT add_dimension('\"readings\"', by_hash('device_id', 4));"));
        Assert.DoesNotContain(sql, s => s.Contains("__ts_rebuild"));
    }

    [Fact]
    public void Columnstore_enable_emits_alter_set()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(),
            Hypertable(e => e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId))));

        Assert.Contains(sql, s => s.Contains(
            "ALTER TABLE \"readings\" SET (timescaledb.enable_columnstore = true, timescaledb.segmentby = 'device_id');"));
        Assert.DoesNotContain(sql, s => s.Contains("convert_to_rowstore"));
    }

    [Fact]
    public void Segmentby_change_emits_alter_set_without_decompress()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId))),
            Hypertable(e => e.HasColumnstore(cs => cs.SegmentBy(x => x.Value))));

        Assert.Contains(sql, s => s.Contains("timescaledb.segmentby = 'value'"));
        Assert.DoesNotContain(sql, s => s.Contains("convert_to_rowstore"));
    }

    [Fact]
    public void Chunk_skipping_added_and_removed()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasChunkSkipping(x => x.Value)),
            Hypertable(e => e.HasChunkSkipping(x => x.DeviceId)));

        Assert.Contains(sql, s => s.Contains("SELECT enable_chunk_skipping('\"readings\"', 'device_id');"));
        Assert.Contains(sql, s => s.Contains("SELECT disable_chunk_skipping('\"readings\"', 'value');"));
    }

    // ---------------------------------------------------------------- reversals (no rebuild)

    [Fact]
    public void Removing_retention_policy_emits_remove()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasRetentionPolicy(90, Every.Day)),
            Hypertable());

        Assert.Contains(sql, s => s.Contains("SELECT remove_retention_policy('\"readings\"', if_exists => true);"));
        Assert.DoesNotContain(sql, s => s.Contains("add_retention_policy"));
    }

    [Fact]
    public void Columnstore_disable_decompresses_then_disables_in_one_statement()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId))),
            Hypertable());

        var disable = Assert.Single(sql, s => s.Contains("convert_to_rowstore"));
        Assert.Contains("remove_columnstore_policy", disable);
        Assert.Contains("is_compressed", disable);

        var decompress = disable.IndexOf("convert_to_rowstore", StringComparison.Ordinal);
        var setFalse = disable.IndexOf("timescaledb.enable_columnstore = false", StringComparison.Ordinal);
        Assert.True(setFalse > decompress, disable);
    }

    // ---------------------------------------------------------------- rebuild path

    [Fact]
    public void Hypertable_to_plain_rebuilds_table_preserving_data()
    {
        var sql = MigrationSqlHelper.GenerateSql(Hypertable(), Plain);
        var list = sql.ToList();

        var create = list.FindIndex(s => s.Contains("CREATE TABLE \"readings__ts_rebuild\" (LIKE \"readings\" INCLUDING ALL)"));
        var insert = list.FindIndex(s => s.Contains("INSERT INTO \"readings__ts_rebuild\" SELECT * FROM \"readings\""));
        var drop = list.FindIndex(s => s.Contains("DROP TABLE \"readings\" CASCADE"));
        var rename = list.FindIndex(s => s.Contains("ALTER TABLE \"readings__ts_rebuild\" RENAME TO \"readings\""));

        Assert.True(create >= 0 && insert > create && drop > insert && rename > drop, string.Join("\n---\n", sql));
        // Target is plain → no create_hypertable on the shadow table.
        Assert.DoesNotContain(sql, s => s.Contains("create_hypertable"));
    }

    [Fact]
    public void Partition_column_change_rebuilds_as_hypertable_with_new_column()
    {
        Action<ModelBuilder> byValue = mb => mb.Entity<Reading>(e =>
        {
            Configure()(e);
            e.Property(x => x.Value).IsRequired();
            e.IsHypertable(x => x.Value);
        });

        var sql = MigrationSqlHelper.GenerateSql(Hypertable(), byValue);

        Assert.Contains(sql, s => s.Contains("CREATE TABLE \"readings__ts_rebuild\" (LIKE \"readings\" INCLUDING ALL)"));
        Assert.Contains(sql, s => s.Contains("SELECT create_hypertable('\"readings__ts_rebuild\"', by_range('value'"));
        Assert.Contains(sql, s => s.Contains("ALTER TABLE \"readings__ts_rebuild\" RENAME TO \"readings\""));
    }

    [Fact]
    public void Space_partition_removed_rebuilds()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasSpacePartition(x => x.DeviceId, partitions: 4)),
            Hypertable());

        Assert.Contains(sql, s => s.Contains("__ts_rebuild"));
        Assert.Contains(sql, s => s.Contains("create_hypertable")); // target still a hypertable
    }

    // ---------------------------------------------------------------- idempotency

    [Fact]
    public void Same_model_twice_produces_no_operations()
    {
        var full = (Action<ModelBuilder>)(mb => mb.Entity<Reading>(e =>
        {
            Configure()(e);
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
            e.HasSpacePartition(x => x.DeviceId, partitions: 4);
            e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId).OrderByDescending(x => x.Time));
            e.HasColumnstorePolicy(7, Every.Day);
            e.HasRetentionPolicy(90, Every.Day);
        }));

        var sql = MigrationSqlHelper.GenerateSql(full, full);

        Assert.Empty(sql);
    }
}
