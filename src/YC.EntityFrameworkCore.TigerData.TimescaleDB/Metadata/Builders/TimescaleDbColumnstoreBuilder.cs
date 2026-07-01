using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Internal;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata.Builders;

/// <summary>
///     Type-safe configuration of the TimescaleDB columnstore: segment-by columns,
///     ordering and chunk merging. Property references are resolved to store column
///     names when the model is finalized.
/// </summary>
public class TimescaleDbColumnstoreBuilder<TEntity>
    where TEntity : class
{
    private readonly EntityTypeBuilder _entityTypeBuilder;
    private readonly List<string> _segmentBy = [];
    private readonly List<string> _orderBy = [];
    private readonly List<string> _sparseIndex = [];

    public TimescaleDbColumnstoreBuilder(EntityTypeBuilder entityTypeBuilder)
        => _entityTypeBuilder = entityTypeBuilder;

    /// <summary>Adds a column to <c>timescaledb.segmentby</c>.</summary>
    public virtual TimescaleDbColumnstoreBuilder<TEntity> SegmentBy(
        Expression<Func<TEntity, object?>> property)
    {
        _segmentBy.Add(ExpressionHelpers.GetPropertyName(property));
        Apply();
        return this;
    }

    /// <summary>Adds an ascending column to <c>timescaledb.orderby</c>.</summary>
    public virtual TimescaleDbColumnstoreBuilder<TEntity> OrderBy(
        Expression<Func<TEntity, object?>> property,
        Nulls nulls = Nulls.Default)
        => AddOrderBy(property, descending: false, nulls);

    /// <summary>Adds a descending column to <c>timescaledb.orderby</c>.</summary>
    public virtual TimescaleDbColumnstoreBuilder<TEntity> OrderByDescending(
        Expression<Func<TEntity, object?>> property,
        Nulls nulls = Nulls.Default)
        => AddOrderBy(property, descending: true, nulls);

    /// <summary>Adds a further ascending ordering column.</summary>
    public virtual TimescaleDbColumnstoreBuilder<TEntity> ThenBy(
        Expression<Func<TEntity, object?>> property,
        Nulls nulls = Nulls.Default)
        => AddOrderBy(property, descending: false, nulls);

    /// <summary>Adds a further descending ordering column.</summary>
    public virtual TimescaleDbColumnstoreBuilder<TEntity> ThenByDescending(
        Expression<Func<TEntity, object?>> property,
        Nulls nulls = Nulls.Default)
        => AddOrderBy(property, descending: true, nulls);

    /// <summary>
    ///     Merges chunks up to the given span when converting to columnstore
    ///     (<c>timescaledb.compress_chunk_time_interval</c>).
    /// </summary>
    public virtual TimescaleDbColumnstoreBuilder<TEntity> MergeChunksUpTo(TimeSpan interval)
    {
        _entityTypeBuilder.HasAnnotation(
            TimescaleDbAnnotationNames.ColumnstoreChunkMergeInterval, PgInterval.Format(interval));
        return this;
    }

    /// <inheritdoc cref="MergeChunksUpTo(TimeSpan)" />
    public virtual TimescaleDbColumnstoreBuilder<TEntity> MergeChunksUpTo(int interval, Every unit)
    {
        _entityTypeBuilder.HasAnnotation(
            TimescaleDbAnnotationNames.ColumnstoreChunkMergeInterval, PgInterval.Format(interval, unit));
        return this;
    }

    /// <summary>Adds a <c>minmax</c> sparse index on the column to <c>timescaledb.sparse_index</c>.</summary>
    public virtual TimescaleDbColumnstoreBuilder<TEntity> SparseIndexMinMax(
        Expression<Func<TEntity, object?>> property)
        => AddSparseIndex(property, "minmax");

    /// <summary>Adds a <c>bloom</c> sparse index on the column to <c>timescaledb.sparse_index</c>.</summary>
    public virtual TimescaleDbColumnstoreBuilder<TEntity> SparseIndexBloom(
        Expression<Func<TEntity, object?>> property)
        => AddSparseIndex(property, "bloom");

    private TimescaleDbColumnstoreBuilder<TEntity> AddSparseIndex(
        Expression<Func<TEntity, object?>> property,
        string kind)
    {
        _sparseIndex.Add($"{kind}({ExpressionHelpers.GetPropertyName(property)})");
        Apply();
        return this;
    }

    private TimescaleDbColumnstoreBuilder<TEntity> AddOrderBy(
        Expression<Func<TEntity, object?>> property,
        bool descending,
        Nulls nulls)
    {
        var segment = ExpressionHelpers.GetPropertyName(property);

        if (descending)
        {
            segment += " DESC";
        }

        segment += nulls switch
        {
            Nulls.First => " NULLS FIRST",
            Nulls.Last => " NULLS LAST",
            _ => string.Empty,
        };

        _orderBy.Add(segment);
        Apply();
        return this;
    }

    private void Apply()
    {
        if (_segmentBy.Count > 0)
        {
            _entityTypeBuilder.HasAnnotation(
                TimescaleDbAnnotationNames.ColumnstoreSegmentBy, string.Join(", ", _segmentBy));
        }

        if (_orderBy.Count > 0)
        {
            _entityTypeBuilder.HasAnnotation(
                TimescaleDbAnnotationNames.ColumnstoreOrderBy, string.Join(", ", _orderBy));
        }

        if (_sparseIndex.Count > 0)
        {
            _entityTypeBuilder.HasAnnotation(
                TimescaleDbAnnotationNames.ColumnstoreSparseIndex, string.Join(", ", _sparseIndex));
        }
    }
}
