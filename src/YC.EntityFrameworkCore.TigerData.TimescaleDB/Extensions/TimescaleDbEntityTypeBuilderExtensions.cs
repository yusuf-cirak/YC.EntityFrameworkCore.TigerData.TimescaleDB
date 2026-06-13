using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Internal;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata.Builders;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     TimescaleDB Fluent API for entity types. Intervals are expressed with <see cref="TimeSpan" />
///     or a <c>(value, <see cref="Every" />)</c> pair — never a raw string. The only strings are
///     genuine database references (an integer-now function name, a continuous-aggregate query, a
///     time-zone identifier).
/// </summary>
public static class TimescaleDbEntityTypeBuilderExtensions
{
    // ---------------------------------------------------------------- hypertable

    /// <summary>Maps the entity to a hypertable partitioned by the given time property.</summary>
    /// <param name="chunkInterval">Chunk interval; TimescaleDB default (7 days) when null.</param>
    public static EntityTypeBuilder<TEntity> IsHypertable<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Expression<Func<TEntity, object?>> partitionColumn,
        TimeSpan? chunkInterval = null,
        bool? createDefaultIndexes = null)
        where TEntity : class
        => ApplyHypertable(
            entityTypeBuilder,
            ExpressionHelpers.GetPropertyName(partitionColumn),
            chunkInterval is { } interval ? PgInterval.Format(interval) : null,
            createDefaultIndexes);

    /// <summary>Maps the entity to a hypertable with a <c>(value, unit)</c> chunk interval.</summary>
    public static EntityTypeBuilder<TEntity> IsHypertable<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Expression<Func<TEntity, object?>> partitionColumn,
        int chunkInterval,
        Every chunkUnit,
        bool? createDefaultIndexes = null)
        where TEntity : class
        => ApplyHypertable(
            entityTypeBuilder,
            ExpressionHelpers.GetPropertyName(partitionColumn),
            PgInterval.Format(chunkInterval, chunkUnit),
            createDefaultIndexes);

    /// <summary>
    ///     Maps the entity to a hypertable partitioned by an integer column. The chunk interval is
    ///     the raw size in the column's own unit (e.g. microseconds).
    /// </summary>
    /// <param name="integerNowFunction">
    ///     A STABLE SQL function returning the current time in the column's unit
    ///     (<c>set_integer_now_func</c>); required when policies are configured.
    /// </param>
    public static EntityTypeBuilder<TEntity> IsHypertableByInteger<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Expression<Func<TEntity, object?>> partitionColumn,
        long chunkInterval,
        string? integerNowFunction = null,
        bool? createDefaultIndexes = null)
        where TEntity : class
    {
        ApplyHypertable(
            entityTypeBuilder,
            ExpressionHelpers.GetPropertyName(partitionColumn),
            chunkInterval.ToString(System.Globalization.CultureInfo.InvariantCulture),
            createDefaultIndexes);

        if (integerNowFunction is not null)
        {
            entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.IntegerNowFunction, integerNowFunction);
        }

        return entityTypeBuilder;
    }

    /// <summary>Registers the integer-now function of an integer-time hypertable.</summary>
    public static EntityTypeBuilder<TEntity> HasIntegerNowFunction<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string functionName)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.IntegerNowFunction, functionName);
        return entityTypeBuilder;
    }

    /// <summary>Adds a hash (space) partition dimension on the given column.</summary>
    public static EntityTypeBuilder<TEntity> HasSpacePartition<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Expression<Func<TEntity, object?>> column,
        int partitions)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(partitions, 0);

        entityTypeBuilder.HasAnnotation(
            TimescaleDbAnnotationNames.SpacePartitionColumn, ExpressionHelpers.GetPropertyName(column));
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.SpacePartitions, partitions);
        return entityTypeBuilder;
    }

    /// <summary>Enables chunk skipping (min/max range tracking) on the given column.</summary>
    public static EntityTypeBuilder<TEntity> HasChunkSkipping<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Expression<Func<TEntity, object?>> column)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);

        var name = ExpressionHelpers.GetPropertyName(column);
        var existing = entityTypeBuilder.Metadata
            .FindAnnotation(TimescaleDbAnnotationNames.ChunkSkippingColumns)?.Value as string;

        var columns = existing is null
            ? name
            : string.Join(", ", existing
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(c => c != name)
                .Append(name));

        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ChunkSkippingColumns, columns);
        return entityTypeBuilder;
    }

    // ---------------------------------------------------------------- columnstore

    /// <summary>
    ///     Enables the TimescaleDB columnstore with type-safe configuration:
    ///     <code>e.HasColumnstore(cs => cs.SegmentBy(x =&gt; x.DeviceId).OrderByDescending(x =&gt; x.Time));</code>
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasColumnstore<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Action<TimescaleDbColumnstoreBuilder<TEntity>>? configure = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ColumnstoreEnabled, true);
        configure?.Invoke(new TimescaleDbColumnstoreBuilder<TEntity>(entityTypeBuilder));
        return entityTypeBuilder;
    }

    /// <summary>Adds an automatic columnstore conversion policy (<c>add_columnstore_policy</c>).</summary>
    public static EntityTypeBuilder<TEntity> HasColumnstorePolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        TimeSpan after,
        TimeSpan? scheduleInterval = null,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
        where TEntity : class
        => ApplyColumnstorePolicy(
            entityTypeBuilder, PgInterval.Format(after),
            scheduleInterval is { } s ? PgInterval.Format(s) : null, initialStart, timezone);

    /// <inheritdoc cref="HasColumnstorePolicy{TEntity}(EntityTypeBuilder{TEntity}, TimeSpan, TimeSpan?, DateTimeOffset?, string?)" />
    public static EntityTypeBuilder<TEntity> HasColumnstorePolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        int after,
        Every unit,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
        where TEntity : class
        => ApplyColumnstorePolicy(
            entityTypeBuilder, PgInterval.Format(after, unit), null, initialStart, timezone);

    /// <summary>
    ///     Adds a columnstore policy to an integer-partitioned hypertable, with <paramref name="after" />
    ///     expressed as a raw value in the partition column's own unit.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasColumnstorePolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        long after,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
        where TEntity : class
        => ApplyColumnstorePolicy(
            entityTypeBuilder, after.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null, initialStart, timezone);

    // ---------------------------------------------------------------- retention / reorder

    /// <summary>Adds a data retention policy (hypertable or continuous aggregate).</summary>
    public static EntityTypeBuilder<TEntity> HasRetentionPolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        TimeSpan dropAfter,
        TimeSpan? scheduleInterval = null,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
        where TEntity : class
        => ApplyRetentionPolicy(
            entityTypeBuilder, PgInterval.Format(dropAfter),
            scheduleInterval is { } s ? PgInterval.Format(s) : null, initialStart, timezone);

    /// <inheritdoc cref="HasRetentionPolicy{TEntity}(EntityTypeBuilder{TEntity}, TimeSpan, TimeSpan?, DateTimeOffset?, string?)" />
    public static EntityTypeBuilder<TEntity> HasRetentionPolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        int dropAfter,
        Every unit,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
        where TEntity : class
        => ApplyRetentionPolicy(
            entityTypeBuilder, PgInterval.Format(dropAfter, unit), null, initialStart, timezone);

    /// <summary>
    ///     Adds a retention policy to an integer-partitioned hypertable, with <paramref name="dropAfter" />
    ///     expressed as a raw value in the partition column's own unit.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasRetentionPolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        long dropAfter,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
        where TEntity : class
        => ApplyRetentionPolicy(
            entityTypeBuilder, dropAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null, initialStart, timezone);

    /// <summary>
    ///     Adds a reorder policy using the EF index on the given properties; the index's database
    ///     name is resolved when the model is finalized.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasReorderPolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Expression<Func<TEntity, object?>> indexProperties)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        entityTypeBuilder.HasAnnotation(
            TimescaleDbAnnotationNames.ReorderPolicyIndexProperties,
            string.Join(",", ExpressionHelpers.GetPropertyNames(indexProperties)));
        return entityTypeBuilder;
    }

    /// <summary>Adds a reorder policy using an explicitly named index.</summary>
    public static EntityTypeBuilder<TEntity> HasReorderPolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string indexName)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ReorderPolicyIndex, indexName);
        return entityTypeBuilder;
    }

    // ---------------------------------------------------------------- continuous aggregate

    /// <summary>
    ///     Maps the entity to a TimescaleDB continuous aggregate (a materialized view created with
    ///     <c>WITH (timescaledb.continuous)</c> over <paramref name="query" />).
    /// </summary>
    public static EntityTypeBuilder<TEntity> IsContinuousAggregate<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string viewName,
        string query,
        bool materializedOnly = true,
        bool withNoData = true,
        TimeSpan? chunkInterval = null,
        string? schema = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        entityTypeBuilder.ToView(viewName, schema);
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.IsContinuousAggregate, true);
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ContinuousAggregateQuery, query);
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ContinuousAggregateMaterializedOnly, materializedOnly);
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ContinuousAggregateWithNoData, withNoData);

        if (chunkInterval is { } interval)
        {
            entityTypeBuilder.HasAnnotation(
                TimescaleDbAnnotationNames.ContinuousAggregateChunkInterval, PgInterval.Format(interval));
        }

        return entityTypeBuilder;
    }

    /// <summary>Adds a refresh policy (<c>add_continuous_aggregate_policy</c>) to the continuous aggregate.</summary>
    public static EntityTypeBuilder<TEntity> HasRefreshPolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        TimeSpan startOffset,
        TimeSpan endOffset,
        TimeSpan? scheduleInterval = null,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
        where TEntity : class
        => ApplyRefreshPolicy(
            entityTypeBuilder, PgInterval.Format(startOffset), PgInterval.Format(endOffset),
            scheduleInterval is { } s ? PgInterval.Format(s) : null, initialStart, timezone);

    /// <inheritdoc cref="HasRefreshPolicy{TEntity}(EntityTypeBuilder{TEntity}, TimeSpan, TimeSpan, TimeSpan?, DateTimeOffset?, string?)" />
    public static EntityTypeBuilder<TEntity> HasRefreshPolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        int startOffset,
        int endOffset,
        Every unit,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
        where TEntity : class
        => ApplyRefreshPolicy(
            entityTypeBuilder, PgInterval.Format(startOffset, unit), PgInterval.Format(endOffset, unit),
            null, initialStart, timezone);

    // ---------------------------------------------------------------- apply helpers

    private static EntityTypeBuilder<TEntity> ApplyHypertable<TEntity>(
        EntityTypeBuilder<TEntity> builder, string column, string? chunkInterval, bool? createDefaultIndexes)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(TimescaleDbAnnotationNames.IsHypertable, true);
        builder.HasAnnotation(TimescaleDbAnnotationNames.PartitionColumn, column);
        if (chunkInterval is not null)
        {
            builder.HasAnnotation(TimescaleDbAnnotationNames.ChunkInterval, chunkInterval);
        }

        if (createDefaultIndexes is not null)
        {
            builder.HasAnnotation(TimescaleDbAnnotationNames.CreateDefaultIndexes, createDefaultIndexes);
        }

        return builder;
    }

    private static EntityTypeBuilder<TEntity> ApplyColumnstorePolicy<TEntity>(
        EntityTypeBuilder<TEntity> builder, string after, string? schedule,
        DateTimeOffset? initialStart, string? timezone)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(TimescaleDbAnnotationNames.ColumnstorePolicyAfter, after);
        SetIfNotNull(builder, TimescaleDbAnnotationNames.ColumnstorePolicyScheduleInterval, schedule);
        SetIfNotNull(builder, TimescaleDbAnnotationNames.ColumnstorePolicyInitialStart, initialStart?.ToString("O"));
        SetIfNotNull(builder, TimescaleDbAnnotationNames.ColumnstorePolicyTimezone, timezone);
        return builder;
    }

    private static EntityTypeBuilder<TEntity> ApplyRetentionPolicy<TEntity>(
        EntityTypeBuilder<TEntity> builder, string dropAfter, string? schedule,
        DateTimeOffset? initialStart, string? timezone)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(TimescaleDbAnnotationNames.RetentionPolicyDropAfter, dropAfter);
        SetIfNotNull(builder, TimescaleDbAnnotationNames.RetentionPolicyScheduleInterval, schedule);
        SetIfNotNull(builder, TimescaleDbAnnotationNames.RetentionPolicyInitialStart, initialStart?.ToString("O"));
        SetIfNotNull(builder, TimescaleDbAnnotationNames.RetentionPolicyTimezone, timezone);
        return builder;
    }

    private static EntityTypeBuilder<TEntity> ApplyRefreshPolicy<TEntity>(
        EntityTypeBuilder<TEntity> builder, string startOffset, string endOffset, string? schedule,
        DateTimeOffset? initialStart, string? timezone)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(TimescaleDbAnnotationNames.RefreshPolicyStartOffset, startOffset);
        builder.HasAnnotation(TimescaleDbAnnotationNames.RefreshPolicyEndOffset, endOffset);
        SetIfNotNull(builder, TimescaleDbAnnotationNames.RefreshPolicyScheduleInterval, schedule);
        SetIfNotNull(builder, TimescaleDbAnnotationNames.RefreshPolicyInitialStart, initialStart?.ToString("O"));
        SetIfNotNull(builder, TimescaleDbAnnotationNames.RefreshPolicyTimezone, timezone);
        return builder;
    }

    private static void SetIfNotNull(EntityTypeBuilder builder, string annotation, string? value)
    {
        if (value is not null)
        {
            builder.HasAnnotation(annotation, value);
        }
    }
}
