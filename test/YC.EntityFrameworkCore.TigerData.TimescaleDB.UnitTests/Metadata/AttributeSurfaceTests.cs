using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using Microsoft.EntityFrameworkCore;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Metadata;

/// <summary>
///     The v4 attribute surface: no <c>[Hypertable]</c> class attribute, the
///     <c>[PartitionColumn]</c> property marker declares the hypertable, intervals are
///     <c>(value, Every)</c>, and dependent features require a partition column.
/// </summary>
public class AttributeSurfaceTests
{
    private class BareHypertable
    {
        [PartitionColumn] public DateTime Time { get; set; }
        public double Value { get; set; }
    }

    [Fact]
    public void Partition_column_marker_alone_declares_hypertable()
    {
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<BareHypertable>(e => e.HasNoKey()));
        var entity = model.FindEntityType(typeof(BareHypertable))!;

        Assert.Equal(true, entity.FindAnnotation(TimescaleDbAnnotationNames.IsHypertable)?.Value);
        Assert.Equal("Time", entity.FindAnnotation(TimescaleDbAnnotationNames.PartitionColumn)?.Value);
        Assert.Null(entity.FindAnnotation(TimescaleDbAnnotationNames.ChunkInterval)); // TimescaleDB default
    }

    private class MonthlyChunks
    {
        [PartitionColumn(1, Every.Month)] public DateTime Time { get; set; }
    }

    [Fact]
    public void Calendar_chunk_interval_via_every()
    {
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<MonthlyChunks>(e => e.HasNoKey()));
        var entity = model.FindEntityType(typeof(MonthlyChunks))!;

        Assert.Equal("1 month", entity.FindAnnotation(TimescaleDbAnnotationNames.ChunkInterval)?.Value);
    }

    private class IntegerChunks
    {
        [PartitionColumn(86_400_000_000)] public long UnixMicros { get; set; }
    }

    [Fact]
    public void Integer_partition_chunk_is_raw_number()
    {
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<IntegerChunks>(e => e.HasNoKey()));
        var entity = model.FindEntityType(typeof(IntegerChunks))!;

        Assert.Equal("86400000000", entity.FindAnnotation(TimescaleDbAnnotationNames.ChunkInterval)?.Value);
    }

    [Columnstore(CompressAfter = 7, CompressAfterUnit = Every.Day)]
    [Retention(90, Every.Day)]
    private class FullyAttributed
    {
        [PartitionColumn(1, Every.Day)]
        [OrderBy(0, Sort.Descending, Nulls.Last)]
        public DateTime Time { get; set; }

        [SegmentBy]
        public string Source { get; set; } = null!;
    }

    [Fact]
    public void Columnstore_and_retention_attributes_canonicalize()
    {
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<FullyAttributed>(e =>
        {
            e.HasNoKey();
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.Source).HasColumnName("source");
        }));
        var entity = model.FindEntityType(typeof(FullyAttributed))!;

        Assert.Equal(true, entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreEnabled)?.Value);
        Assert.Equal("source", entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreSegmentBy)?.Value);
        Assert.Equal("time DESC NULLS LAST", entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreOrderBy)?.Value);
        Assert.Equal("7 days", entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstorePolicyAfter)?.Value);
        Assert.Equal("90 days", entity.FindAnnotation(TimescaleDbAnnotationNames.RetentionPolicyDropAfter)?.Value);
    }

    [Fact]
    public void Segment_marker_enables_columnstore_without_columnstore_attribute()
    {
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<ImplicitColumnstore>(e => e.HasNoKey()));
        var entity = model.FindEntityType(typeof(ImplicitColumnstore))!;

        Assert.Equal(true, entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreEnabled)?.Value);
        Assert.Equal("Source", entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreSegmentBy)?.Value);
    }

    private class ImplicitColumnstore
    {
        [PartitionColumn] public DateTime Time { get; set; }
        [SegmentBy] public string Source { get; set; } = null!;
    }

    [Columnstore]
    private class ColumnstoreWithoutPartition
    {
        public DateTime Time { get; set; }
    }

    [Fact]
    public void Columnstore_without_partition_column_throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TimescaleDbModelBuilder.Build(mb => mb.Entity<ColumnstoreWithoutPartition>(e => e.HasNoKey())));

        Assert.Contains("not a hypertable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Retention(30, Every.Day)]
    private class RetentionWithoutPartition
    {
        public DateTime Time { get; set; }
    }

    [Fact]
    public void Retention_without_partition_column_throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TimescaleDbModelBuilder.Build(mb => mb.Entity<RetentionWithoutPartition>(e => e.HasNoKey())));

        Assert.Contains("hypertable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
