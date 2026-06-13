using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Internal;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata.Conventions;

/// <summary>
///     Reads the TimescaleDB attributes — class-level configuration and property-level
///     column markers — and stores the equivalent semantic annotations, as if configured
///     through the Fluent API. Property names are stored; the model finalizing convention
///     resolves them to store column names.
/// </summary>
public class TimescaleDbAttributeConvention : IEntityTypeAddedConvention
{
    public virtual void ProcessEntityTypeAdded(
        IConventionEntityTypeBuilder entityTypeBuilder,
        IConventionContext<IConventionEntityTypeBuilder> context)
    {
        var clrType = entityTypeBuilder.Metadata.ClrType;
        var clrProperties = clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        ProcessHypertable(entityTypeBuilder, clrType, clrProperties);
        ProcessColumnstore(entityTypeBuilder, clrType, clrProperties);
        ProcessPolicies(entityTypeBuilder, clrType);
        ProcessChunkSkipping(entityTypeBuilder, clrProperties);
    }

    private static void ProcessHypertable(
        IConventionEntityTypeBuilder builder,
        Type clrType,
        PropertyInfo[] clrProperties)
    {
        var hypertable = clrType.GetCustomAttribute<HypertableAttribute>();
        var partitionProperties = clrProperties
            .Where(p => p.GetCustomAttribute<HypertablePartitionAttribute>() is not null)
            .ToList();
        var spaceProperties = clrProperties
            .Where(p => p.GetCustomAttribute<SpacePartitionAttribute>() is not null)
            .ToList();

        if (hypertable is null && partitionProperties.Count == 0)
        {
            if (spaceProperties.Count > 0)
            {
                throw new InvalidOperationException(
                    $"'{clrType.Name}' has a [SpacePartition] property but is not a hypertable; "
                    + "add [Hypertable] to the class.");
            }

            return;
        }

        if (partitionProperties.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{clrType.Name}' marks more than one property with [HypertablePartition]; "
                + "a hypertable has exactly one range partition column.");
        }

        var partitionColumn = partitionProperties.SingleOrDefault()?.Name ?? hypertable?.PartitionColumn;
        if (partitionColumn is null)
        {
            throw new InvalidOperationException(
                $"'{clrType.Name}' is a hypertable but no partition column is marked; "
                + "add [HypertablePartition] to the time property.");
        }

        Set(builder, TimescaleDbAnnotationNames.IsHypertable, true);
        Set(builder, TimescaleDbAnnotationNames.PartitionColumn, partitionColumn);

        if (hypertable is not null)
        {
            if (ResolveInterval(
                    clrType, "chunk interval",
                    hypertable.ChunkInterval,
                    hypertable.ChunkIntervalDays, hypertable.ChunkIntervalHours,
                    hypertable.ChunkIntervalInteger) is { } chunkInterval)
            {
                Set(builder, TimescaleDbAnnotationNames.ChunkInterval, chunkInterval);
            }

            if (!hypertable.CreateDefaultIndexes)
            {
                Set(builder, TimescaleDbAnnotationNames.CreateDefaultIndexes, false);
            }

            if (hypertable.IntegerNowFunction is not null)
            {
                Set(builder, TimescaleDbAnnotationNames.IntegerNowFunction, hypertable.IntegerNowFunction);
            }
        }

        if (spaceProperties.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{clrType.Name}' marks more than one property with [SpacePartition]; "
                + "only one hash dimension is supported.");
        }

        if (spaceProperties.SingleOrDefault() is { } spaceProperty)
        {
            Set(builder, TimescaleDbAnnotationNames.SpacePartitionColumn, spaceProperty.Name);
            Set(builder, TimescaleDbAnnotationNames.SpacePartitions,
                spaceProperty.GetCustomAttribute<SpacePartitionAttribute>()!.Partitions);
        }
    }

    private static void ProcessColumnstore(
        IConventionEntityTypeBuilder builder,
        Type clrType,
        PropertyInfo[] clrProperties)
    {
        var columnstore = clrType.GetCustomAttribute<ColumnstoreAttribute>();
        var segmentBy = clrProperties
            .Select(p => (Property: p, Attribute: p.GetCustomAttribute<ColumnstoreSegmentByAttribute>()))
            .Where(x => x.Attribute is not null)
            .OrderBy(x => x.Attribute!.Order)
            .ToList();
        var orderBy = clrProperties
            .Select(p => (Property: p, Attribute: p.GetCustomAttribute<ColumnstoreOrderByAttribute>()))
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
                string.Join(", ", segmentBy.Select(x => x.Property.Name)));
        }

        if (orderBy.Count > 0)
        {
            Set(builder, TimescaleDbAnnotationNames.ColumnstoreOrderBy,
                string.Join(", ", orderBy.Select(x => FormatOrderBySegment(x.Property.Name, x.Attribute!))));
        }

        if (columnstore is not null
            && ResolveInterval(
                clrType, "columnstore chunk merge interval",
                columnstore.ChunkMergeInterval,
                columnstore.ChunkMergeIntervalDays, hours: 0, integer: 0) is { } merge)
        {
            Set(builder, TimescaleDbAnnotationNames.ColumnstoreChunkMergeInterval, merge);
        }
    }

    private static void ProcessPolicies(IConventionEntityTypeBuilder builder, Type clrType)
    {
        if (clrType.GetCustomAttribute<ColumnstorePolicyAttribute>() is { } columnstorePolicy)
        {
            var after = ResolveInterval(
                    clrType, "[ColumnstorePolicy] 'After'",
                    columnstorePolicy.After, columnstorePolicy.AfterDays, hours: 0, integer: 0)
                ?? throw new InvalidOperationException(
                    $"[ColumnstorePolicy] on '{clrType.Name}' must set AfterDays or After.");

            Set(builder, TimescaleDbAnnotationNames.ColumnstorePolicyAfter, after);

            if (ResolveInterval(
                    clrType, "[ColumnstorePolicy] 'ScheduleInterval'",
                    columnstorePolicy.ScheduleInterval, days: 0,
                    columnstorePolicy.ScheduleIntervalHours, integer: 0) is { } schedule)
            {
                Set(builder, TimescaleDbAnnotationNames.ColumnstorePolicyScheduleInterval, schedule);
            }
        }

        if (clrType.GetCustomAttribute<RetentionPolicyAttribute>() is { } retention)
        {
            var dropAfter = ResolveInterval(
                    clrType, "[RetentionPolicy] 'DropAfter'",
                    retention.DropAfter, retention.DropAfterDays, hours: 0, integer: 0)
                ?? throw new InvalidOperationException(
                    $"[RetentionPolicy] on '{clrType.Name}' must set DropAfterDays or DropAfter.");

            Set(builder, TimescaleDbAnnotationNames.RetentionPolicyDropAfter, dropAfter);

            if (ResolveInterval(
                    clrType, "[RetentionPolicy] 'ScheduleInterval'",
                    retention.ScheduleInterval, days: 0,
                    retention.ScheduleIntervalHours, integer: 0) is { } schedule)
            {
                Set(builder, TimescaleDbAnnotationNames.RetentionPolicyScheduleInterval, schedule);
            }
        }

        if (clrType.GetCustomAttribute<ReorderPolicyAttribute>() is { } reorder)
        {
            Set(builder, TimescaleDbAnnotationNames.ReorderPolicyIndex, reorder.IndexName);
        }
    }

    private static void ProcessChunkSkipping(IConventionEntityTypeBuilder builder, PropertyInfo[] clrProperties)
    {
        var columns = clrProperties
            .Where(p => p.GetCustomAttribute<ChunkSkippingAttribute>() is not null)
            .Select(p => p.Name)
            .ToList();

        if (columns.Count > 0)
        {
            Set(builder, TimescaleDbAnnotationNames.ChunkSkippingColumns, string.Join(", ", columns));
        }
    }

    private static string FormatOrderBySegment(string propertyName, ColumnstoreOrderByAttribute attribute)
    {
        if (attribute is { NullsFirst: true, NullsLast: true })
        {
            throw new InvalidOperationException(
                $"[ColumnstoreOrderBy] on '{propertyName}' sets both NullsFirst and NullsLast.");
        }

        var segment = propertyName;
        if (attribute.Descending)
        {
            segment += " DESC";
        }

        if (attribute.NullsFirst)
        {
            segment += " NULLS FIRST";
        }
        else if (attribute.NullsLast)
        {
            segment += " NULLS LAST";
        }

        return segment;
    }

    /// <summary>
    ///     Combines the raw-string and numeric interval attribute properties into a canonical
    ///     interval literal, validating that only one style is used.
    /// </summary>
    private static string? ResolveInterval(
        Type clrType,
        string what,
        string? raw,
        int days,
        int hours,
        long integer)
    {
        var setCount = (raw is not null ? 1 : 0)
            + (days > 0 || hours > 0 ? 1 : 0)
            + (integer > 0 ? 1 : 0);

        return setCount switch
        {
            0 => null,
            1 => raw
                ?? (integer > 0
                    ? integer.ToString(CultureInfo.InvariantCulture)
                    : PgInterval.Format(new TimeSpan(days, hours, 0, 0))),
            _ => throw new InvalidOperationException(
                $"The {what} of '{clrType.Name}' is configured with more than one style; "
                + "use either the numeric properties or the raw interval string."),
        };
    }

    private static void Set(IConventionEntityTypeBuilder builder, string name, object? value)
        => builder.HasAnnotation(name, value, fromDataAnnotation: true);
}
