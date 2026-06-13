using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Migrations;

/// <summary>
///     Per-entity toggles for the automatic, data-heavy operations: <c>migrate_data</c> on
///     plain→hypertable, the rebuild row-copy, and chunk decompression on columnstore disable.
///     All default ON (current behavior); each can be turned off via Fluent or <c>[MigrationOptions]</c>.
/// </summary>
public class MigrationOptionsTests
{
    private class Reading
    {
        public DateTimeOffset Time { get; set; }
        public string DeviceId { get; set; } = null!;
        public double Value { get; set; }
    }

    private static Action<EntityTypeBuilder<Reading>> Configure()
        => e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Value).HasColumnName("value");
        };

    private static Action<ModelBuilder> Plain(Action<EntityTypeBuilder<Reading>>? extra = null)
        => mb => mb.Entity<Reading>(e =>
        {
            Configure()(e);
            extra?.Invoke(e);
        });

    private static Action<ModelBuilder> Hypertable(Action<EntityTypeBuilder<Reading>>? extra = null)
        => mb => mb.Entity<Reading>(e =>
        {
            Configure()(e);
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
            extra?.Invoke(e);
        });

    // ---------------------------------------------------------------- migrate data

    [Fact]
    public void Plain_to_hypertable_migrates_data_by_default()
    {
        var sql = MigrationSqlHelper.GenerateSql(Plain(), Hypertable());
        Assert.Contains(sql, s => s.Contains("migrate_data => true"));
    }

    [Fact]
    public void WithMigrateData_false_emits_migrate_data_false()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Plain(),
            Hypertable(e => e.WithMigrateData(false)));

        // create_hypertable defaults to migrate_data => false, so the clause is simply omitted.
        Assert.Contains(sql, s => s.Contains("create_hypertable"));
        Assert.DoesNotContain(sql, s => s.Contains("migrate_data => true"));
    }

    // ---------------------------------------------------------------- rebuild

    [Fact]
    public void Hypertable_to_plain_rebuilds_by_default()
    {
        var sql = MigrationSqlHelper.GenerateSql(Hypertable(), Plain());
        Assert.Contains(sql, s => s.Contains("__ts_rebuild"));
    }

    [Fact]
    public void WithRebuildData_false_throws_on_hypertable_to_plain()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MigrationSqlHelper.GenerateSql(
                Hypertable(),
                Plain(e => e.WithRebuildData(false))));

        Assert.Contains("rebuild", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("readings", ex.Message);
    }

    [Fact]
    public void WithRebuildData_false_throws_on_partition_column_change()
    {
        Action<ModelBuilder> byValue = mb => mb.Entity<Reading>(e =>
        {
            Configure()(e);
            e.Property(x => x.Value).IsRequired();
            e.IsHypertable(x => x.Value);
            e.WithRebuildData(false);
        });

        Assert.Throws<InvalidOperationException>(() => MigrationSqlHelper.GenerateSql(Hypertable(), byValue));
    }

    // ---------------------------------------------------------------- auto decompress

    [Fact]
    public void Columnstore_disable_decompresses_by_default()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId))),
            Hypertable());

        Assert.Contains(sql, s => s.Contains("convert_to_rowstore"));
    }

    [Fact]
    public void WithAutoDecompress_false_skips_decompression()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId))),
            Hypertable(e => e.WithAutoDecompress(false)));

        var disable = Assert.Single(sql, s => s.Contains("timescaledb.enable_columnstore = false"));
        Assert.DoesNotContain("convert_to_rowstore", disable);
        Assert.Contains("remove_columnstore_policy", disable);
    }

    // ---------------------------------------------------------------- attribute parity

    [MigrationOptions(MigrateData = false, AutoDecompress = false)]
    private class AttributedReading
    {
        [PartitionColumn(1, Every.Day)]
        public DateTimeOffset Time { get; set; }

        [SegmentBy]
        public string DeviceId { get; set; } = null!;

        public double Value { get; set; }
    }

    [Fact]
    public void Attribute_toggles_set_the_same_annotations()
    {
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<AttributedReading>(e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
        }));

        var entity = model.FindEntityType(typeof(AttributedReading))!;

        Assert.False((bool)entity.FindAnnotation(TimescaleDbAnnotationNames.MigrateData)!.Value!);
        Assert.False((bool)entity.FindAnnotation(TimescaleDbAnnotationNames.AutoDecompress)!.Value!);
        // RebuildData left at its default (true) → not persisted.
        Assert.Null(entity.FindAnnotation(TimescaleDbAnnotationNames.RebuildData));
    }

    // ---------------------------------------------------------------- model-wide defaults (UseTimescaleDb)

    [Fact]
    public void Global_migrate_data_false_applies_to_entities_without_an_override()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Plain(),
            Hypertable(),
            o => o.MigrateData(false));

        Assert.Contains(sql, s => s.Contains("create_hypertable"));
        Assert.DoesNotContain(sql, s => s.Contains("migrate_data => true"));
    }

    [Fact]
    public void Entity_override_wins_over_global_default()
    {
        // Global default off, but this entity explicitly turns migration back on.
        var sql = MigrationSqlHelper.GenerateSql(
            Plain(),
            Hypertable(e => e.WithMigrateData(true)),
            o => o.MigrateData(false));

        Assert.Contains(sql, s => s.Contains("migrate_data => true"));
    }

    [Fact]
    public void Global_rebuild_data_false_throws_without_an_override()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MigrationSqlHelper.GenerateSql(Hypertable(), Plain(), o => o.RebuildData(false)));
    }

    [Fact]
    public void Global_rebuild_data_false_can_be_overridden_per_entity()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(),
            Plain(e => e.WithRebuildData(true)),
            o => o.RebuildData(false));

        Assert.Contains(sql, s => s.Contains("__ts_rebuild"));
    }

    [Fact]
    public void Global_auto_decompress_false_applies_without_an_override()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId))),
            Hypertable(),
            o => o.AutoDecompress(false));

        Assert.DoesNotContain(sql, s => s.Contains("convert_to_rowstore"));
    }

    // ---------------------------------------------------------------- idempotency

    [Fact]
    public void Toggled_model_is_idempotent()
    {
        Action<ModelBuilder> model = Hypertable(e =>
        {
            e.WithMigrateData(false);
            e.WithRebuildData(false);
            e.WithAutoDecompress(false);
        });

        Assert.Empty(MigrationSqlHelper.GenerateSql(model, model));
    }
}
