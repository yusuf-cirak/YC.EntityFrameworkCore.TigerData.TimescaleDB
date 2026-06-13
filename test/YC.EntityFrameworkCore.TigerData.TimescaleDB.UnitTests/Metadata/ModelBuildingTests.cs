using Microsoft.EntityFrameworkCore;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Metadata;

public class ModelBuildingTests
{
    private class Reading
    {
        public long Id { get; set; }
        public DateTime Time { get; set; }
        public string DeviceId { get; set; } = null!;
        public double Value { get; set; }
    }

    [Hypertable(ChunkIntervalDays = 1)]
    [Columnstore]
    [ColumnstorePolicy(AfterDays = 7)]
    [RetentionPolicy(DropAfterDays = 90, ScheduleIntervalHours = 12)]
    private class AttributedReading
    {
        [HypertablePartition]
        [ColumnstoreOrderBy(Descending = true)]
        public DateTime Time { get; set; }

        [SpacePartition(4)]
        [ColumnstoreSegmentBy]
        public string DeviceId { get; set; } = null!;

        public double Value { get; set; }
    }

    private class HourlyAvg
    {
        public DateTime Bucket { get; set; }
        public string DeviceId { get; set; } = null!;
        public double Avg { get; set; }
    }

    [Fact]
    public void Fluent_hypertable_sets_canonical_annotations()
    {
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<Reading>(e =>
        {
            e.HasKey(x => new { x.Id, x.Time });
            e.IsHypertable(x => x.Time, chunkInterval: "1 day");
            e.HasColumnstore(segmentBy: "DeviceId", orderBy: "Time DESC");
        }));

        var entity = model.FindEntityType(typeof(Reading))!;

        Assert.Equal(true, entity.FindAnnotation(TimescaleDbAnnotationNames.IsHypertable)?.Value);
        Assert.Equal("Time", entity.FindAnnotation(TimescaleDbAnnotationNames.PartitionColumn)?.Value);
        Assert.Equal("1 day", entity.FindAnnotation(TimescaleDbAnnotationNames.ChunkInterval)?.Value);
        Assert.Equal(true, entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreEnabled)?.Value);
        Assert.Equal("DeviceId", entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreSegmentBy)?.Value);
        Assert.Equal("Time DESC", entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreOrderBy)?.Value);
    }

    [Fact]
    public void Attributes_produce_same_annotations_as_fluent()
    {
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<AttributedReading>(e => e.HasNoKey()));

        var entity = model.FindEntityType(typeof(AttributedReading))!;

        Assert.Equal(true, entity.FindAnnotation(TimescaleDbAnnotationNames.IsHypertable)?.Value);
        Assert.Equal("Time", entity.FindAnnotation(TimescaleDbAnnotationNames.PartitionColumn)?.Value);
        Assert.Equal("DeviceId", entity.FindAnnotation(TimescaleDbAnnotationNames.SpacePartitionColumn)?.Value);
        Assert.Equal(4, entity.FindAnnotation(TimescaleDbAnnotationNames.SpacePartitions)?.Value);
        Assert.Equal("7 days", entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstorePolicyAfter)?.Value);
        Assert.Equal("90 days", entity.FindAnnotation(TimescaleDbAnnotationNames.RetentionPolicyDropAfter)?.Value);
        Assert.Equal(
            "12:00:00", entity.FindAnnotation(TimescaleDbAnnotationNames.RetentionPolicyScheduleInterval)?.Value);
        Assert.Equal(true, entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreEnabled)?.Value);
        Assert.Equal("DeviceId", entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreSegmentBy)?.Value);
        Assert.Equal("Time DESC", entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreOrderBy)?.Value);
    }

    [Fact]
    public void Column_names_are_canonicalized_to_store_names()
    {
        var model = TimescaleDbModelBuilder.Build(mb => mb.Entity<Reading>(e =>
        {
            e.HasNoKey();
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.IsHypertable(x => x.Time);
            e.HasColumnstore(segmentBy: "DeviceId", orderBy: "Time DESC");
        }));

        var entity = model.FindEntityType(typeof(Reading))!;

        Assert.Equal("time", entity.FindAnnotation(TimescaleDbAnnotationNames.PartitionColumn)?.Value);
        Assert.Equal("device_id", entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreSegmentBy)?.Value);
        Assert.Equal("time DESC", entity.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreOrderBy)?.Value);
    }

    [Fact]
    public void Primary_key_must_include_partition_column()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TimescaleDbModelBuilder.Build(mb => mb.Entity<Reading>(e =>
            {
                e.HasKey(x => x.Id);
                e.IsHypertable(x => x.Time);
            })));

        Assert.Contains("primary key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Time", exception.Message);
    }

    [Fact]
    public void Columnstore_policy_requires_columnstore()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TimescaleDbModelBuilder.Build(mb => mb.Entity<Reading>(e =>
            {
                e.HasNoKey();
                e.IsHypertable(x => x.Time);
                e.HasColumnstorePolicy(after: "7 days");
            })));

        Assert.Contains("columnstore", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Retention_policy_requires_hypertable_or_continuous_aggregate()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TimescaleDbModelBuilder.Build(mb => mb.Entity<Reading>(e =>
            {
                e.HasNoKey();
                e.HasRetentionPolicy(dropAfter: "90 days");
            })));

        Assert.Contains("retention", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Continuous_aggregate_maps_to_view_and_requires_time_bucket()
    {
        var model = TimescaleDbModelBuilder.Build(mb =>
        {
            mb.Entity<Reading>(e =>
            {
                e.HasNoKey();
                e.IsHypertable(x => x.Time);
            });
            mb.Entity<HourlyAvg>(e =>
            {
                e.HasNoKey();
                e.IsContinuousAggregate(
                    "hourly_avg",
                    "SELECT time_bucket('1 hour', \"Time\") AS \"Bucket\", \"DeviceId\", avg(\"Value\") AS \"Avg\" "
                    + "FROM \"Reading\" GROUP BY 1, 2");
                e.HasRefreshPolicy(startOffset: "3 days", endOffset: "1 hour");
            });
        });

        var entity = model.FindEntityType(typeof(HourlyAvg))!;

        Assert.Equal("hourly_avg", entity.GetViewName());
        Assert.Null(entity.GetTableName());
        Assert.Equal(true, entity.FindAnnotation(TimescaleDbAnnotationNames.IsContinuousAggregate)?.Value);
    }

    [Fact]
    public void Continuous_aggregate_without_time_bucket_throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TimescaleDbModelBuilder.Build(mb => mb.Entity<HourlyAvg>(e =>
            {
                e.HasNoKey();
                e.IsContinuousAggregate("hourly_avg", "SELECT 1");
            })));

        Assert.Contains("time_bucket", exception.Message);
    }

    [Fact]
    public void Nullable_partition_column_throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TimescaleDbModelBuilder.Build(mb => mb.Entity<Reading>(e =>
            {
                e.HasNoKey();
                e.Property(x => x.Time).IsRequired(false).HasConversion<DateTime?>();
                e.IsHypertable(x => x.Time);
            })));

        Assert.Contains("non-nullable", exception.Message);
    }

    [Fact]
    public void Jobs_are_stored_on_the_model()
    {
        var model = TimescaleDbModelBuilder.Build(mb =>
            mb.HasTimescaleDbJob("nightly_cleanup", "public.cleanup", scheduleInterval: "1 day"));

        var jobs = TimescaleDbJob.Deserialize(
            model.FindAnnotation(TimescaleDbAnnotationNames.Jobs)?.Value as string);

        var job = Assert.Single(jobs);
        Assert.Equal("nightly_cleanup", job.Name);
        Assert.Equal("public.cleanup", job.Procedure);
        Assert.Equal("1 day", job.ScheduleInterval);
    }
}
