using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using Microsoft.EntityFrameworkCore;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata.Builders;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Metadata;

public class TypeSafeApiTests
{
    private class Order
    {
        public long OrderId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string CustomerId { get; set; } = null!;
        public decimal Total { get; set; }
    }

    [Fact]
    public void Columnstore_builder_resolves_renamed_columns()
    {
        // The "OrderId vs order_id" scenario: every column is renamed; the emitted
        // parameters must follow the EF-configured store names.
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<Order>(e =>
        {
            e.HasNoKey();
            e.ToTable("orders");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.OrderId).HasColumnName("order_id");
            e.IsHypertable(x => x.CreatedAt, chunkInterval: TimeSpan.FromDays(1));
            e.HasColumnstore(cs => cs
                .SegmentBy(x => x.CustomerId)
                .OrderByDescending(x => x.CreatedAt, nulls: Nulls.Last)
                .ThenBy(x => x.OrderId));
        }));

        var entity = model.FindEntityType(typeof(Order))!;

        Assert.Equal("created_at", entity.FindAnnotation(TimescaleDbAnnotationNames.PartitionColumn)?.Value);
        Assert.Equal("1 day", entity.FindAnnotation(TimescaleDbAnnotationNames.ChunkInterval)?.Value);
        Assert.Equal("customer_id", entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreSegmentBy)?.Value);
        Assert.Equal(
            "created_at DESC NULLS LAST, order_id",
            entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreOrderBy)?.Value);
    }

    [Fact]
    public void Property_attributes_resolve_renamed_columns()
    {
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<AttributedOrder>(e =>
        {
            e.HasNoKey();
            e.ToTable("orders");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
        }));

        var entity = model.FindEntityType(typeof(AttributedOrder))!;

        Assert.Equal("created_at", entity.FindAnnotation(TimescaleDbAnnotationNames.PartitionColumn)?.Value);
        Assert.Equal("customer_id", entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreSegmentBy)?.Value);
        Assert.Equal(
            "created_at DESC NULLS FIRST",
            entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreOrderBy)?.Value);
    }

    [Columnstore]
    private class AttributedOrder
    {
        [PartitionColumn]
        [OrderBy(0, Sort.Descending, Nulls.First)]
        public DateTimeOffset CreatedAt { get; set; }

        [SegmentBy]
        public string CustomerId { get; set; } = null!;
    }

    [Fact]
    public void Multiple_partition_attributes_throw()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TimescaleDbModelBuilder.Build(mb => mb.Entity<TwoPartitions>(e => e.HasNoKey())));

        Assert.Contains("more than one property with [PartitionColumn]", exception.Message);
    }

    private class TwoPartitions
    {
        [PartitionColumn] public DateTime A { get; set; }
        [PartitionColumn] public DateTime B { get; set; }
    }

    [Fact]
    public void Reorder_policy_index_expression_resolves_database_name()
    {
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<Order>(e =>
        {
            e.HasNoKey();
            e.ToTable("orders");
            e.HasIndex(x => new { x.CustomerId, x.CreatedAt });
            e.IsHypertable(x => x.CreatedAt);
            e.HasReorderPolicy(x => new { x.CustomerId, x.CreatedAt });
        }));

        var entity = model.FindEntityType(typeof(Order))!;

        Assert.Equal(
            "IX_orders_CustomerId_CreatedAt",
            entity.FindAnnotation(TimescaleDbAnnotationNames.ReorderPolicyIndex)?.Value);
    }

    [Fact]
    public void Reorder_policy_without_matching_index_throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TimescaleDbModelBuilder.Build(mb => mb.Entity<Order>(e =>
            {
                e.HasNoKey();
                e.IsHypertable(x => x.CreatedAt);
                e.HasReorderPolicy(x => new { x.CustomerId });
            })));

        Assert.Contains("No EF index", exception.Message);
    }

    [Fact]
    public void Chunk_skipping_canonicalizes_columns()
    {
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<Order>(e =>
        {
            e.HasNoKey();
            e.Property(x => x.OrderId).HasColumnName("order_id");
            e.IsHypertable(x => x.CreatedAt);
            e.HasChunkSkipping(x => x.OrderId);
        }));

        var entity = model.FindEntityType(typeof(Order))!;

        Assert.Equal("order_id", entity.FindAnnotation(TimescaleDbAnnotationNames.ChunkSkippingColumns)?.Value);
    }

    [Fact]
    public void Integer_time_hypertable_requires_numeric_chunk_interval()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TimescaleDbModelBuilder.Build(mb => mb.Entity<IntegerSeries>(e =>
            {
                e.HasNoKey();
                e.IsHypertable(x => x.UnixMicros, chunkInterval: TimeSpan.FromDays(1));
            })));

        Assert.Contains("not numeric", exception.Message);
    }

    [Fact]
    public void Integer_time_policies_require_integer_now_function()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TimescaleDbModelBuilder.Build(mb => mb.Entity<IntegerSeries>(e =>
            {
                e.HasNoKey();
                e.IsHypertableByInteger(x => x.UnixMicros, 86_400_000_000);
                e.HasRetentionPolicy(7_776_000_000_000L);
            })));

        Assert.Contains("integer-now function", exception.Message);
    }

    [Fact]
    public void Integer_time_with_now_function_is_valid()
    {
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<IntegerSeries>(e =>
        {
            e.HasNoKey();
            e.IsHypertableByInteger(x => x.UnixMicros, 86_400_000_000, integerNowFunction: "unix_micros_now");
            e.HasRetentionPolicy(7_776_000_000_000L);
        }));

        var entity = model.FindEntityType(typeof(IntegerSeries))!;

        Assert.Equal("unix_micros_now", entity.FindAnnotation(TimescaleDbAnnotationNames.IntegerNowFunction)?.Value);
        Assert.Equal("86400000000", entity.FindAnnotation(TimescaleDbAnnotationNames.ChunkInterval)?.Value);
    }

    private class IntegerSeries
    {
        public long UnixMicros { get; set; }
        public double Value { get; set; }
    }
}
