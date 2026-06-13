using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata.Conventions;

/// <summary>
///     Validates the TimescaleDB model configuration and canonicalizes property names to store
///     column names (respecting HasColumnName and naming-convention plugins). The canonical
///     <c>TimescaleDb:*</c> annotations are projected onto the table by
///     <see cref="YC.EntityFrameworkCore.TigerData.TimescaleDB.Migrations.TimescaleDbAnnotationProvider" />
///     and interpreted (forward / reverse / rebuild) by the migrations SQL generator.
/// </summary>
public class TimescaleDbModelFinalizingConvention : IModelFinalizingConvention
{
    private static readonly string[] AllowedOrderByTokens = ["ASC", "DESC", "NULLS", "FIRST", "LAST"];

    public virtual void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            var isHypertable = entityType.FindAnnotation(TimescaleDbAnnotationNames.IsHypertable)?.Value is true;
            var isContinuousAggregate =
                entityType.FindAnnotation(TimescaleDbAnnotationNames.IsContinuousAggregate)?.Value is true;

            if (isHypertable && isContinuousAggregate)
            {
                throw new InvalidOperationException(
                    $"Entity type '{entityType.DisplayName()}' is configured both as a hypertable and as a " +
                    "continuous aggregate; these are mutually exclusive.");
            }

            if (isHypertable)
            {
                ProcessHypertable(entityType);
            }
            else
            {
                EnsureNoHypertableOnlyConfiguration(entityType, isContinuousAggregate);
            }

            if (isContinuousAggregate)
            {
                ProcessContinuousAggregate(entityType);
            }
        }
    }

    // ---------------------------------------------------------------- hypertable

    private static void ProcessHypertable(IConventionEntityType entityType)
    {
        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException(
                $"Hypertable entity type '{entityType.DisplayName()}' is not mapped to a table.");

        var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());

        // Partition column: canonicalize and validate.
        var partitionReference = (string?)entityType
                .FindAnnotation(TimescaleDbAnnotationNames.PartitionColumn)?.Value
            ?? throw new InvalidOperationException(
                $"Hypertable entity type '{entityType.DisplayName()}' has no partition column configured.");

        var partitionProperty = ResolveProperty(entityType, partitionReference, storeObject);
        var partitionColumn = GetColumnName(partitionProperty, storeObject, entityType);

        if (partitionProperty.IsNullable)
        {
            throw new InvalidOperationException(
                $"The partition column '{partitionReference}' of hypertable '{entityType.DisplayName()}' must be " +
                "non-nullable.");
        }

        ValidateUniqueConstraintsIncludePartitionColumn(entityType, partitionProperty);
        ValidateIntegerTimeConfiguration(entityType, partitionProperty);

        entityType.SetAnnotation(TimescaleDbAnnotationNames.PartitionColumn, partitionColumn);

        // Space dimensions: canonicalize each column name (emitted as add_dimension by the generator).
        if (entityType.FindAnnotation(TimescaleDbAnnotationNames.SpaceDimensions)?.Value is string dimensions)
        {
            var store = storeObject;
            var canonical = string.Join(", ", dimensions
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(item =>
                {
                    var parts = item.Split(':', 2);
                    if (parts.Length != 2 || !int.TryParse(parts[1], out var partitions) || partitions <= 0)
                    {
                        throw new InvalidOperationException(
                            $"The space dimension '{item}' of hypertable '{entityType.DisplayName()}' must specify " +
                            "a positive number of partitions.");
                    }

                    var column = GetColumnName(ResolveProperty(entityType, parts[0], store), store, entityType);
                    return $"{column}:{partitions}";
                }));

            entityType.SetAnnotation(TimescaleDbAnnotationNames.SpaceDimensions, canonical);
        }

        // Chunk skipping: canonicalize column list.
        if (entityType.FindAnnotation(TimescaleDbAnnotationNames.ChunkSkippingColumns)?.Value is string skipColumns)
        {
            entityType.SetAnnotation(
                TimescaleDbAnnotationNames.ChunkSkippingColumns,
                CanonicalizeColumnList(skipColumns, entityType, storeObject));
        }

        ProcessColumnstore(entityType, storeObject);
        ResolveReorderPolicyIndex(entityType, storeObject);
    }

    private static void ProcessColumnstore(
        IConventionEntityType entityType,
        in StoreObjectIdentifier storeObject)
    {
        if (entityType.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreEnabled)?.Value is not true)
        {
            if (entityType.FindAnnotation(TimescaleDbAnnotationNames.ColumnstorePolicyAfter) is not null)
            {
                throw new InvalidOperationException(
                    $"Entity type '{entityType.DisplayName()}' has a columnstore policy but columnstore is not " +
                    "enabled; call 'HasColumnstore()' (or add [Columnstore]).");
            }

            return;
        }

        if (entityType.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreSegmentBy)?.Value is string segmentBy)
        {
            entityType.SetAnnotation(
                TimescaleDbAnnotationNames.ColumnstoreSegmentBy,
                CanonicalizeColumnList(segmentBy, entityType, storeObject));
        }

        if (entityType.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreOrderBy)?.Value is string orderBy)
        {
            var store = storeObject;
            entityType.SetAnnotation(
                TimescaleDbAnnotationNames.ColumnstoreOrderBy,
                string.Join(", ", orderBy
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(segment => CanonicalizeOrderBySegment(segment, entityType, store))));
        }
    }

    private static void ResolveReorderPolicyIndex(
        IConventionEntityType entityType,
        in StoreObjectIdentifier storeObject)
    {
        if (entityType.FindAnnotation(TimescaleDbAnnotationNames.ReorderPolicyIndexProperties)?.Value
            is not string propertyList)
        {
            return;
        }

        if (entityType.FindAnnotation(TimescaleDbAnnotationNames.ReorderPolicyIndex) is not null)
        {
            throw new InvalidOperationException(
                $"Entity type '{entityType.DisplayName()}' configures a reorder policy both by index name and " +
                "by properties; use one.");
        }

        var store = storeObject;
        var properties = propertyList
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(reference => ResolveProperty(entityType, reference, store))
            .ToList();

        var index = entityType.GetIndexes().FirstOrDefault(i => i.Properties.SequenceEqual(properties))
            ?? throw new InvalidOperationException(
                $"No EF index on '{entityType.DisplayName()}' matches the reorder policy properties " +
                $"({propertyList}); declare it with 'HasIndex(...)' first.");

        var databaseName = index.GetDatabaseName(storeObject)
            ?? throw new InvalidOperationException(
                $"The reorder policy index on '{entityType.DisplayName()}' has no database name.");

        entityType.SetAnnotation(TimescaleDbAnnotationNames.ReorderPolicyIndex, databaseName);
        entityType.RemoveAnnotation(TimescaleDbAnnotationNames.ReorderPolicyIndexProperties);
    }

    private static void ValidateIntegerTimeConfiguration(
        IConventionEntityType entityType,
        IConventionProperty partitionProperty)
    {
        var clrType = Nullable.GetUnderlyingType(partitionProperty.ClrType) ?? partitionProperty.ClrType;
        var isIntegerTime = clrType == typeof(long) || clrType == typeof(int) || clrType == typeof(short);

        var chunkInterval = entityType.FindAnnotation(TimescaleDbAnnotationNames.ChunkInterval)?.Value as string;
        var hasIntegerNow = entityType.FindAnnotation(TimescaleDbAnnotationNames.IntegerNowFunction) is not null;

        if (isIntegerTime)
        {
            if (chunkInterval is not null && !long.TryParse(chunkInterval, out _))
            {
                throw new InvalidOperationException(
                    $"Hypertable '{entityType.DisplayName()}' partitions by the integer column " +
                    $"'{partitionProperty.Name}' but its chunk interval '{chunkInterval}' is not numeric; " +
                    "specify the interval in the column's own unit.");
            }

            var hasPolicy =
                entityType.FindAnnotation(TimescaleDbAnnotationNames.RetentionPolicyDropAfter) is not null
                || entityType.FindAnnotation(TimescaleDbAnnotationNames.ColumnstorePolicyAfter) is not null
                || entityType.FindAnnotation(TimescaleDbAnnotationNames.ReorderPolicyIndex) is not null
                || entityType.FindAnnotation(TimescaleDbAnnotationNames.ReorderPolicyIndexProperties) is not null;

            if (hasPolicy && !hasIntegerNow)
            {
                throw new InvalidOperationException(
                    $"Hypertable '{entityType.DisplayName()}' partitions by an integer column and has policies; " +
                    "TimescaleDB requires an integer-now function. Configure it with " +
                    "'HasIntegerNowFunction(...)' (or [Hypertable(IntegerNowFunction = ...)]).");
            }
        }
        else if (hasIntegerNow)
        {
            throw new InvalidOperationException(
                $"Hypertable '{entityType.DisplayName()}' configures an integer-now function but its partition " +
                $"column '{partitionProperty.Name}' is not an integer type.");
        }
    }

    // ---------------------------------------------------------------- non-hypertables

    private static void EnsureNoHypertableOnlyConfiguration(
        IConventionEntityType entityType,
        bool isContinuousAggregate)
    {
        var hypertableOnly = new List<(string Annotation, string Feature)>
        {
            (TimescaleDbAnnotationNames.SpaceDimensions, "space partition"),
            (TimescaleDbAnnotationNames.Tablespaces, "tablespace"),
            (TimescaleDbAnnotationNames.ReorderPolicyIndex, "reorder policy"),
            (TimescaleDbAnnotationNames.ReorderPolicyIndexProperties, "reorder policy"),
            (TimescaleDbAnnotationNames.ChunkSkippingColumns, "chunk skipping"),
            (TimescaleDbAnnotationNames.IntegerNowFunction, "integer-now function"),
        };

        if (!isContinuousAggregate)
        {
            // Continuous aggregates may use the columnstore; plain tables may not.
            hypertableOnly.Add((TimescaleDbAnnotationNames.ColumnstoreEnabled, "columnstore"));
            hypertableOnly.Add((TimescaleDbAnnotationNames.ColumnstorePolicyAfter, "columnstore policy"));
        }

        foreach (var (annotation, feature) in hypertableOnly)
        {
            if (entityType.FindAnnotation(annotation) is not null)
            {
                throw new InvalidOperationException(
                    $"Entity type '{entityType.DisplayName()}' is configured with a {feature} but is not a " +
                    "hypertable; call 'IsHypertable(...)' (or add [Hypertable]).");
            }
        }

        if (!isContinuousAggregate
            && entityType.FindAnnotation(TimescaleDbAnnotationNames.RetentionPolicyDropAfter) is not null)
        {
            throw new InvalidOperationException(
                $"Entity type '{entityType.DisplayName()}' has a retention policy but is neither a hypertable nor " +
                "a continuous aggregate.");
        }

        if (!isContinuousAggregate
            && entityType.FindAnnotation(TimescaleDbAnnotationNames.RefreshPolicyStartOffset) is not null)
        {
            throw new InvalidOperationException(
                $"Entity type '{entityType.DisplayName()}' has a refresh policy but is not a continuous aggregate; " +
                "call 'IsContinuousAggregate(...)'.");
        }
    }

    // ---------------------------------------------------------------- continuous aggregate

    private static void ProcessContinuousAggregate(IConventionEntityType entityType)
    {
        var viewName = entityType.GetViewName()
            ?? throw new InvalidOperationException(
                $"Continuous aggregate entity type '{entityType.DisplayName()}' is not mapped to a view. " +
                "Configure it with 'IsContinuousAggregate(viewName, query)'.");

        if (entityType.FindAnnotation(TimescaleDbAnnotationNames.ContinuousAggregateQuery)?.Value is not string query
            || string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException(
                $"Continuous aggregate '{entityType.DisplayName()}' has no query configured.");
        }

        if (!query.Contains("time_bucket", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The query of continuous aggregate '{entityType.DisplayName()}' must aggregate by " +
                "'time_bucket(...)'.");
        }

        var hasStart = entityType.FindAnnotation(TimescaleDbAnnotationNames.RefreshPolicyStartOffset) is not null;
        var hasEnd = entityType.FindAnnotation(TimescaleDbAnnotationNames.RefreshPolicyEndOffset) is not null;
        if (hasStart != hasEnd)
        {
            throw new InvalidOperationException(
                $"The refresh policy of continuous aggregate '{entityType.DisplayName()}' must specify both " +
                "a start offset and an end offset.");
        }

        // Columnstore on the aggregate: canonicalize against the VIEW mapping; emitted as
        // ALTER MATERIALIZED VIEW by the differ.
        var storeObject = StoreObjectIdentifier.View(viewName, entityType.GetViewSchema());
        ProcessColumnstore(entityType, storeObject);
    }

    // ---------------------------------------------------------------- helpers

    private static void ValidateUniqueConstraintsIncludePartitionColumn(
        IConventionEntityType entityType,
        IConventionProperty partitionProperty)
    {
        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey is not null && !primaryKey.Properties.Contains(partitionProperty))
        {
            throw new InvalidOperationException(
                $"The primary key of hypertable '{entityType.DisplayName()}' must include the partition column " +
                $"'{partitionProperty.Name}' (TimescaleDB requires every unique constraint to cover all " +
                "partitioning columns). Add it to the key, or make the entity keyless.");
        }

        foreach (var index in entityType.GetIndexes().Where(i => i.IsUnique))
        {
            if (!index.Properties.Contains(partitionProperty))
            {
                throw new InvalidOperationException(
                    $"The unique index {{'{string.Join("', '", index.Properties.Select(p => p.Name))}'}} on " +
                    $"hypertable '{entityType.DisplayName()}' must include the partition column " +
                    $"'{partitionProperty.Name}'.");
            }
        }
    }

    private static IConventionProperty ResolveProperty(
        IConventionEntityType entityType,
        string reference,
        in StoreObjectIdentifier storeObject)
    {
        if (entityType.FindProperty(reference) is { } byName)
        {
            return byName;
        }

        IConventionProperty? byColumn = null;
        foreach (var property in entityType.GetProperties())
        {
            if (property.GetColumnName(storeObject) == reference)
            {
                byColumn = property;
                break;
            }
        }

        return byColumn
            ?? throw new InvalidOperationException(
                $"'{reference}' on entity type '{entityType.DisplayName()}' does not resolve to a property or a " +
                "mapped column.");
    }

    private static string GetColumnName(
        IConventionProperty property,
        in StoreObjectIdentifier storeObject,
        IConventionEntityType entityType)
        => property.GetColumnName(storeObject)
            ?? throw new InvalidOperationException(
                $"Property '{property.Name}' of entity type '{entityType.DisplayName()}' is not mapped to a column.");

    private static string CanonicalizeColumnList(
        string columns,
        IConventionEntityType entityType,
        in StoreObjectIdentifier storeObject)
    {
        var result = new List<string>();
        foreach (var reference in columns.Split(
            ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            result.Add(GetColumnName(ResolveProperty(entityType, reference, storeObject), storeObject, entityType));
        }

        return string.Join(", ", result);
    }

    private static string CanonicalizeOrderBySegment(
        string segment,
        IConventionEntityType entityType,
        in StoreObjectIdentifier storeObject)
    {
        var parts = segment.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in parts[1..])
        {
            if (!AllowedOrderByTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Unknown token '{token}' in columnstore ordering segment '{segment}' on " +
                    $"'{entityType.DisplayName()}'; allowed: ASC, DESC, NULLS FIRST, NULLS LAST.");
            }
        }

        var column = GetColumnName(ResolveProperty(entityType, parts[0], storeObject), storeObject, entityType);
        return parts.Length > 1 ? $"{column} {string.Join(' ', parts[1..])}" : column;
    }
}
