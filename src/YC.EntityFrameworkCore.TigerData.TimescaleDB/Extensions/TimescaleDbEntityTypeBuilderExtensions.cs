using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Internal;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata.Builders;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     TimescaleDB Fluent API for entity types: hypertables, columnstore,
///     policies and continuous aggregates.
/// </summary>
public static class TimescaleDbEntityTypeBuilderExtensions
{
    // ---------------------------------------------------------------- hypertable

    /// <summary>
    ///     Maps the entity's table to a TimescaleDB hypertable partitioned by the given property.
    /// </summary>
    /// <param name="partitionColumn">The range partition (time) property.</param>
    /// <param name="chunkInterval">Chunk interval. TimescaleDB default: 7 days.</param>
    /// <param name="createDefaultIndexes">
    ///     Whether TimescaleDB creates its default index on the partition column. TimescaleDB default: true.
    /// </param>
    public static EntityTypeBuilder<TEntity> IsHypertable<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Expression<Func<TEntity, object?>> partitionColumn,
        TimeSpan? chunkInterval = null,
        bool? createDefaultIndexes = null)
        where TEntity : class
        => (EntityTypeBuilder<TEntity>)((EntityTypeBuilder)entityTypeBuilder).IsHypertable(
            ExpressionHelpers.GetPropertyName(partitionColumn),
            chunkInterval is { } interval ? PgInterval.Format(interval) : null,
            createDefaultIndexes);

    /// <summary>
    ///     Maps to a hypertable with a raw PostgreSQL chunk interval for calendar units a
    ///     <see cref="TimeSpan" /> cannot express, e.g. <c>"1 month"</c>.
    /// </summary>
    public static EntityTypeBuilder<TEntity> IsHypertable<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Expression<Func<TEntity, object?>> partitionColumn,
        string chunkInterval,
        bool? createDefaultIndexes = null)
        where TEntity : class
        => (EntityTypeBuilder<TEntity>)((EntityTypeBuilder)entityTypeBuilder).IsHypertable(
            ExpressionHelpers.GetPropertyName(partitionColumn), chunkInterval, createDefaultIndexes);

    /// <summary>
    ///     Maps to a hypertable partitioned by an integer column, with the chunk interval given
    ///     in the column's own unit (e.g. microseconds for a microsecond-epoch column).
    /// </summary>
    /// <param name="integerNowFunction">
    ///     A STABLE SQL function returning the current time in the column's unit
    ///     (<c>set_integer_now_func</c>); required when policies are configured.
    /// </param>
    public static EntityTypeBuilder<TEntity> IsHypertable<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Expression<Func<TEntity, object?>> partitionColumn,
        long chunkInterval,
        string? integerNowFunction = null,
        bool? createDefaultIndexes = null)
        where TEntity : class
    {
        var builder = ((EntityTypeBuilder)entityTypeBuilder).IsHypertable(
            ExpressionHelpers.GetPropertyName(partitionColumn),
            chunkInterval.ToString(System.Globalization.CultureInfo.InvariantCulture),
            createDefaultIndexes);

        if (integerNowFunction is not null)
        {
            builder.HasAnnotation(TimescaleDbAnnotationNames.IntegerNowFunction, integerNowFunction);
        }

        return (EntityTypeBuilder<TEntity>)builder;
    }

    /// <summary>Non-generic / name-based variant of <c>IsHypertable</c>.</summary>
    public static EntityTypeBuilder IsHypertable(
        this EntityTypeBuilder entityTypeBuilder,
        string partitionColumn,
        string? chunkInterval = null,
        bool? createDefaultIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionColumn);

        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.IsHypertable, true);
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.PartitionColumn, partitionColumn);

        if (chunkInterval is not null)
        {
            entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ChunkInterval, chunkInterval);
        }

        if (createDefaultIndexes is not null)
        {
            entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.CreateDefaultIndexes, createDefaultIndexes);
        }

        return entityTypeBuilder;
    }

    /// <summary>
    ///     Registers the integer-now function for an integer-time hypertable
    ///     (<c>set_integer_now_func</c>).
    /// </summary>
    public static EntityTypeBuilder HasIntegerNowFunction(
        this EntityTypeBuilder entityTypeBuilder,
        string functionName)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);

        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.IntegerNowFunction, functionName);
        return entityTypeBuilder;
    }

    /// <inheritdoc cref="HasIntegerNowFunction(EntityTypeBuilder, string)" />
    public static EntityTypeBuilder<TEntity> HasIntegerNowFunction<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string functionName)
        where TEntity : class
        => (EntityTypeBuilder<TEntity>)((EntityTypeBuilder)entityTypeBuilder)
            .HasIntegerNowFunction(functionName);

    /// <summary>
    ///     Adds a hash (space) partition dimension to the hypertable
    ///     (<c>add_dimension(..., by_hash(...))</c>).
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasSpacePartition<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Expression<Func<TEntity, object?>> column,
        int partitions)
        where TEntity : class
        => (EntityTypeBuilder<TEntity>)((EntityTypeBuilder)entityTypeBuilder)
            .HasSpacePartition(ExpressionHelpers.GetPropertyName(column), partitions);

    /// <summary>Non-generic / name-based variant of <c>HasSpacePartition</c>.</summary>
    public static EntityTypeBuilder HasSpacePartition(
        this EntityTypeBuilder entityTypeBuilder,
        string column,
        int partitions)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(partitions, 0);

        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.SpacePartitionColumn, column);
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.SpacePartitions, partitions);
        return entityTypeBuilder;
    }

    /// <summary>
    ///     Enables chunk skipping (min/max range tracking) on the given column
    ///     (<c>enable_chunk_skipping</c>).
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasChunkSkipping<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Expression<Func<TEntity, object?>> column)
        where TEntity : class
        => (EntityTypeBuilder<TEntity>)((EntityTypeBuilder)entityTypeBuilder)
            .HasChunkSkipping(ExpressionHelpers.GetPropertyName(column));

    /// <summary>Non-generic / name-based variant of <c>HasChunkSkipping</c>.</summary>
    public static EntityTypeBuilder HasChunkSkipping(
        this EntityTypeBuilder entityTypeBuilder,
        string column)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        var existing = entityTypeBuilder.Metadata
            .FindAnnotation(TimescaleDbAnnotationNames.ChunkSkippingColumns)?.Value as string;

        var columns = existing is null
            ? column
            : string.Join(", ", existing
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(c => c != column)
                .Append(column));

        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ChunkSkippingColumns, columns);
        return entityTypeBuilder;
    }

    // ---------------------------------------------------------------- columnstore

    /// <summary>
    ///     Enables the TimescaleDB columnstore (compression) with type-safe configuration:
    ///     <code>
    ///     e.HasColumnstore(cs => cs
    ///         .SegmentBy(x => x.DeviceId)
    ///         .OrderByDescending(x => x.Time));
    ///     </code>
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

    /// <summary>Non-generic / name-based variant of <c>HasColumnstore</c>.</summary>
    public static EntityTypeBuilder HasColumnstore(
        this EntityTypeBuilder entityTypeBuilder,
        string? segmentBy = null,
        string? orderBy = null)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);

        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ColumnstoreEnabled, true);

        if (segmentBy is not null)
        {
            entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ColumnstoreSegmentBy, segmentBy);
        }

        if (orderBy is not null)
        {
            entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ColumnstoreOrderBy, orderBy);
        }

        return entityTypeBuilder;
    }

    /// <summary>
    ///     Adds an automatic columnstore conversion policy (<c>add_columnstore_policy</c>).
    /// </summary>
    /// <param name="after">Chunks older than this are converted.</param>
    /// <param name="scheduleInterval">How often the policy job runs. TimescaleDB default: derived from the chunk interval.</param>
    /// <param name="initialStart">First run of the policy job.</param>
    /// <param name="timezone">Time zone for the schedule. TimescaleDB default: UTC.</param>
    public static EntityTypeBuilder<TEntity> HasColumnstorePolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        TimeSpan after,
        TimeSpan? scheduleInterval = null,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
        where TEntity : class
        => (EntityTypeBuilder<TEntity>)((EntityTypeBuilder)entityTypeBuilder).HasColumnstorePolicy(
            PgInterval.Format(after),
            scheduleInterval is { } schedule ? PgInterval.Format(schedule) : null,
            initialStart,
            timezone);

    /// <summary>Raw-interval variant of <c>HasColumnstorePolicy</c> (e.g. <c>"1 month"</c>).</summary>
    public static EntityTypeBuilder HasColumnstorePolicy(
        this EntityTypeBuilder entityTypeBuilder,
        string after,
        string? scheduleInterval = null,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(after);

        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ColumnstorePolicyAfter, after);
        SetIfNotNull(entityTypeBuilder, TimescaleDbAnnotationNames.ColumnstorePolicyScheduleInterval, scheduleInterval);
        SetIfNotNull(entityTypeBuilder, TimescaleDbAnnotationNames.ColumnstorePolicyInitialStart,
            initialStart?.ToString("O"));
        SetIfNotNull(entityTypeBuilder, TimescaleDbAnnotationNames.ColumnstorePolicyTimezone, timezone);
        return entityTypeBuilder;
    }

    // ---------------------------------------------------------------- retention / reorder

    /// <summary>
    ///     Adds a data retention policy (<c>add_retention_policy</c>); applies to hypertables
    ///     and continuous aggregates.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasRetentionPolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        TimeSpan dropAfter,
        TimeSpan? scheduleInterval = null,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
        where TEntity : class
        => (EntityTypeBuilder<TEntity>)((EntityTypeBuilder)entityTypeBuilder).HasRetentionPolicy(
            PgInterval.Format(dropAfter),
            scheduleInterval is { } schedule ? PgInterval.Format(schedule) : null,
            initialStart,
            timezone);

    /// <summary>Raw-interval variant of <c>HasRetentionPolicy</c> (e.g. <c>"6 months"</c>).</summary>
    public static EntityTypeBuilder HasRetentionPolicy(
        this EntityTypeBuilder entityTypeBuilder,
        string dropAfter,
        string? scheduleInterval = null,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(dropAfter);

        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.RetentionPolicyDropAfter, dropAfter);
        SetIfNotNull(entityTypeBuilder, TimescaleDbAnnotationNames.RetentionPolicyScheduleInterval, scheduleInterval);
        SetIfNotNull(entityTypeBuilder, TimescaleDbAnnotationNames.RetentionPolicyInitialStart,
            initialStart?.ToString("O"));
        SetIfNotNull(entityTypeBuilder, TimescaleDbAnnotationNames.RetentionPolicyTimezone, timezone);
        return entityTypeBuilder;
    }

    /// <summary>
    ///     Adds a reorder policy (<c>add_reorder_policy</c>) using the EF index on the given
    ///     properties; the index's database name is resolved when the model is finalized.
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

    /// <summary>Adds a reorder policy using an explicitly named (possibly external) index.</summary>
    public static EntityTypeBuilder HasReorderPolicy(
        this EntityTypeBuilder entityTypeBuilder,
        string indexName)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);

        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ReorderPolicyIndex, indexName);
        return entityTypeBuilder;
    }

    /// <inheritdoc cref="HasReorderPolicy(EntityTypeBuilder, string)" />
    public static EntityTypeBuilder<TEntity> HasReorderPolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string indexName)
        where TEntity : class
        => (EntityTypeBuilder<TEntity>)((EntityTypeBuilder)entityTypeBuilder).HasReorderPolicy(indexName);

    // ---------------------------------------------------------------- continuous aggregate

    /// <summary>
    ///     Maps the entity to a TimescaleDB continuous aggregate: a materialized view created with
    ///     <c>WITH (timescaledb.continuous)</c> over the given SQL query. The entity is mapped to the
    ///     view for querying and excluded from normal table migrations.
    /// </summary>
    /// <param name="viewName">Name of the materialized view.</param>
    /// <param name="query">
    ///     The aggregate SELECT; must group by <c>time_bucket(...)</c> over the source hypertable.
    /// </param>
    /// <param name="materializedOnly">
    ///     When false, real-time aggregation includes not-yet-materialized data. TimescaleDB default: true.
    /// </param>
    /// <param name="withNoData">Create the view without an initial full refresh. Default here: true.</param>
    /// <param name="chunkInterval">Chunk interval of the materialization hypertable.</param>
    /// <param name="schema">Optional schema of the view.</param>
    public static EntityTypeBuilder IsContinuousAggregate(
        this EntityTypeBuilder entityTypeBuilder,
        string viewName,
        string query,
        bool materializedOnly = true,
        bool withNoData = true,
        string? chunkInterval = null,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        entityTypeBuilder.ToView(viewName, schema);

        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.IsContinuousAggregate, true);
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ContinuousAggregateQuery, query);
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ContinuousAggregateMaterializedOnly, materializedOnly);
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ContinuousAggregateWithNoData, withNoData);

        if (chunkInterval is not null)
        {
            entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.ContinuousAggregateChunkInterval, chunkInterval);
        }

        return entityTypeBuilder;
    }

    /// <inheritdoc cref="IsContinuousAggregate(EntityTypeBuilder, string, string, bool, bool, string?, string?)" />
    public static EntityTypeBuilder<TEntity> IsContinuousAggregate<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string viewName,
        string query,
        bool materializedOnly = true,
        bool withNoData = true,
        string? chunkInterval = null,
        string? schema = null)
        where TEntity : class
        => (EntityTypeBuilder<TEntity>)((EntityTypeBuilder)entityTypeBuilder)
            .IsContinuousAggregate(viewName, query, materializedOnly, withNoData, chunkInterval, schema);

    /// <summary>
    ///     Adds a refresh policy (<c>add_continuous_aggregate_policy</c>) to the continuous aggregate.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasRefreshPolicy<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        TimeSpan startOffset,
        TimeSpan endOffset,
        TimeSpan? scheduleInterval = null,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
        where TEntity : class
        => (EntityTypeBuilder<TEntity>)((EntityTypeBuilder)entityTypeBuilder).HasRefreshPolicy(
            PgInterval.Format(startOffset),
            PgInterval.Format(endOffset),
            scheduleInterval is { } schedule ? PgInterval.Format(schedule) : null,
            initialStart,
            timezone);

    /// <summary>Raw-interval variant of <c>HasRefreshPolicy</c>.</summary>
    public static EntityTypeBuilder HasRefreshPolicy(
        this EntityTypeBuilder entityTypeBuilder,
        string startOffset,
        string endOffset,
        string? scheduleInterval = null,
        DateTimeOffset? initialStart = null,
        string? timezone = null)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(startOffset);
        ArgumentException.ThrowIfNullOrWhiteSpace(endOffset);

        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.RefreshPolicyStartOffset, startOffset);
        entityTypeBuilder.HasAnnotation(TimescaleDbAnnotationNames.RefreshPolicyEndOffset, endOffset);
        SetIfNotNull(entityTypeBuilder, TimescaleDbAnnotationNames.RefreshPolicyScheduleInterval, scheduleInterval);
        SetIfNotNull(entityTypeBuilder, TimescaleDbAnnotationNames.RefreshPolicyInitialStart,
            initialStart?.ToString("O"));
        SetIfNotNull(entityTypeBuilder, TimescaleDbAnnotationNames.RefreshPolicyTimezone, timezone);
        return entityTypeBuilder;
    }

    // ---------------------------------------------------------------- helpers

    private static void SetIfNotNull(EntityTypeBuilder builder, string annotation, string? value)
    {
        if (value is not null)
        {
            builder.HasAnnotation(annotation, value);
        }
    }
}
