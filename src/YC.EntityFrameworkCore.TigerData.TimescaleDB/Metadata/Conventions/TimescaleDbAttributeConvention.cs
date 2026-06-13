using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Internal;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata.Conventions;

/// <summary>
///     Reads the TimescaleDB attributes — the <see cref="PartitionColumnAttribute" /> property
///     marker (which declares the hypertable), the columnstore / retention class attributes, and
///     the per-column markers — and stores the same semantic <c>TimescaleDb:*</c> annotations the
///     Fluent API produces. Property names are canonicalized to store column names by the model
///     finalizing convention. No reorder / integer-now here: those are Fluent-only.
/// </summary>
public class TimescaleDbAttributeConvention : IEntityTypeAddedConvention
{
    public virtual void ProcessEntityTypeAdded(
        IConventionEntityTypeBuilder entityTypeBuilder,
        IConventionContext<IConventionEntityTypeBuilder> context)
    {
        var clrType = entityTypeBuilder.Metadata.ClrType;
        var properties = clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        ProcessHypertable(entityTypeBuilder, clrType, properties);
        ProcessSpacePartition(entityTypeBuilder, clrType, properties);
        ProcessChunkSkipping(entityTypeBuilder, properties);
        ProcessColumnstore(entityTypeBuilder, clrType, properties);
        ProcessRetention(entityTypeBuilder, clrType);
    }

    private static void ProcessHypertable(
        IConventionEntityTypeBuilder builder,
        Type clrType,
        PropertyInfo[] properties)
    {
        var partitions = properties
            .Where(p => p.GetCustomAttribute<PartitionColumnAttribute>() is not null)
            .ToList();

        if (partitions.Count == 0)
        {
            return;
        }

        if (partitions.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{clrType.Name}' marks more than one property with [PartitionColumn]; a hypertable has "
                + "exactly one range partition column.");
        }

        var property = partitions[0];
        var attribute = property.GetCustomAttribute<PartitionColumnAttribute>()!;

        Set(builder, TimescaleDbAnnotationNames.IsHypertable, true);
        Set(builder, TimescaleDbAnnotationNames.PartitionColumn, property.Name);

        if (attribute.ChunkInterval > 0)
        {
            Set(builder, TimescaleDbAnnotationNames.ChunkInterval,
                FormatChunkInterval(property, attribute.ChunkInterval, attribute.ChunkUnit));
        }

        if (!attribute.CreateDefaultIndexes)
        {
            Set(builder, TimescaleDbAnnotationNames.CreateDefaultIndexes, false);
        }
    }

    private static void ProcessSpacePartition(
        IConventionEntityTypeBuilder builder,
        Type clrType,
        PropertyInfo[] properties)
    {
        var space = properties
            .Where(p => p.GetCustomAttribute<SpacePartitionAttribute>() is not null)
            .ToList();

        if (space.Count == 0)
        {
            return;
        }

        if (space.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{clrType.Name}' marks more than one property with [SpacePartition]; only one hash "
                + "dimension is supported.");
        }

        Set(builder, TimescaleDbAnnotationNames.SpacePartitionColumn, space[0].Name);
        Set(builder, TimescaleDbAnnotationNames.SpacePartitions,
            space[0].GetCustomAttribute<SpacePartitionAttribute>()!.Partitions);
    }

    private static void ProcessChunkSkipping(IConventionEntityTypeBuilder builder, PropertyInfo[] properties)
    {
        var columns = properties
            .Where(p => p.GetCustomAttribute<ChunkSkippingAttribute>() is not null)
            .Select(p => p.Name)
            .ToList();

        if (columns.Count > 0)
        {
            Set(builder, TimescaleDbAnnotationNames.ChunkSkippingColumns, string.Join(", ", columns));
        }
    }

    private static void ProcessColumnstore(
        IConventionEntityTypeBuilder builder,
        Type clrType,
        PropertyInfo[] properties)
    {
        var columnstore = clrType.GetCustomAttribute<ColumnstoreAttribute>();

        var segmentBy = Ordered<SegmentByAttribute>(properties, a => a.Order);
        var orderBy = properties
            .Select(p => (Property: p, Attribute: p.GetCustomAttribute<OrderByAttribute>()))
            .Where(x => x.Attribute is not null)
            .OrderBy(x => x.Attribute!.Order)
            .ToList();

        if (columnstore is null && segmentBy.Count == 0 && orderBy.Count == 0)
        {
            return;
        }

        Set(builder, TimescaleDbAnnotationNames.ColumnstoreEnabled, true);

        if (segmentBy.Count > 0)
        {
            Set(builder, TimescaleDbAnnotationNames.ColumnstoreSegmentBy,
                string.Join(", ", segmentBy.Select(p => p.Name)));
        }

        if (orderBy.Count > 0)
        {
            Set(builder, TimescaleDbAnnotationNames.ColumnstoreOrderBy,
                string.Join(", ", orderBy.Select(x => FormatOrderBy(x.Property.Name, x.Attribute!))));
        }

        if (columnstore is null)
        {
            return;
        }

        if (columnstore.CompressAfter > 0)
        {
            Set(builder, TimescaleDbAnnotationNames.ColumnstorePolicyAfter,
                PgInterval.Format(columnstore.CompressAfter, columnstore.CompressAfterUnit));

            if (columnstore.ScheduleInterval > 0)
            {
                Set(builder, TimescaleDbAnnotationNames.ColumnstorePolicyScheduleInterval,
                    PgInterval.Format(columnstore.ScheduleInterval, columnstore.ScheduleIntervalUnit));
            }
        }

        if (columnstore.MergeChunks > 0)
        {
            Set(builder, TimescaleDbAnnotationNames.ColumnstoreChunkMergeInterval,
                PgInterval.Format(columnstore.MergeChunks, columnstore.MergeChunksUnit));
        }
    }

    private static void ProcessRetention(IConventionEntityTypeBuilder builder, Type clrType)
    {
        if (clrType.GetCustomAttribute<RetentionAttribute>() is not { } retention)
        {
            return;
        }

        Set(builder, TimescaleDbAnnotationNames.RetentionPolicyDropAfter,
            PgInterval.Format(retention.DropAfter, retention.Unit));

        if (retention.ScheduleInterval > 0)
        {
            Set(builder, TimescaleDbAnnotationNames.RetentionPolicyScheduleInterval,
                PgInterval.Format(retention.ScheduleInterval, retention.ScheduleIntervalUnit));
        }
    }

    private static string FormatChunkInterval(PropertyInfo property, long value, Every unit)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var isInteger = type == typeof(long) || type == typeof(int) || type == typeof(short);

        return isInteger
            ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : PgInterval.Format(value, unit);
    }

    private static string FormatOrderBy(string propertyName, OrderByAttribute attribute)
    {
        var segment = propertyName;
        if (attribute.Direction == Sort.Descending)
        {
            segment += " DESC";
        }

        segment += attribute.Nulls switch
        {
            Nulls.First => " NULLS FIRST",
            Nulls.Last => " NULLS LAST",
            _ => string.Empty,
        };

        return segment;
    }

    private static List<PropertyInfo> Ordered<TAttribute>(PropertyInfo[] properties, Func<TAttribute, int> order)
        where TAttribute : Attribute
        => properties
            .Select(p => (Property: p, Attribute: p.GetCustomAttribute<TAttribute>()))
            .Where(x => x.Attribute is not null)
            .OrderBy(x => order(x.Attribute!))
            .Select(x => x.Property)
            .ToList();

    private static void Set(IConventionEntityTypeBuilder builder, string name, object? value)
        => builder.HasAnnotation(name, value, fromDataAnnotation: true);
}
