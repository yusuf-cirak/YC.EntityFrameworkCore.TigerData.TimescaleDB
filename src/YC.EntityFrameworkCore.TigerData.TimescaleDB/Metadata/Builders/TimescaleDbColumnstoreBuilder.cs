using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Internal;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata.Builders;

/// <summary>Position of NULLs in a columnstore ordering.</summary>
public enum NullsPosition
{
    Unspecified = 0,
    First = 1,
    Last = 2,
}

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
        NullsPosition nulls = NullsPosition.Unspecified)
        => AddOrderBy(property, descending: false, nulls);

    /// <summary>Adds a descending column to <c>timescaledb.orderby</c>.</summary>
    public virtual TimescaleDbColumnstoreBuilder<TEntity> OrderByDescending(
        Expression<Func<TEntity, object?>> property,
        NullsPosition nulls = NullsPosition.Unspecified)
        => AddOrderBy(property, descending: true, nulls);

    /// <summary>Adds a further ascending ordering column.</summary>
    public virtual TimescaleDbColumnstoreBuilder<TEntity> ThenBy(
        Expression<Func<TEntity, object?>> property,
        NullsPosition nulls = NullsPosition.Unspecified)
        => AddOrderBy(property, descending: false, nulls);

    /// <summary>Adds a further descending ordering column.</summary>
    public virtual TimescaleDbColumnstoreBuilder<TEntity> ThenByDescending(
        Expression<Func<TEntity, object?>> property,
        NullsPosition nulls = NullsPosition.Unspecified)
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
    public virtual TimescaleDbColumnstoreBuilder<TEntity> MergeChunksUpTo(string interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interval);
        _entityTypeBuilder.HasAnnotation(
            TimescaleDbAnnotationNames.ColumnstoreChunkMergeInterval, interval);
        return this;
    }

    private TimescaleDbColumnstoreBuilder<TEntity> AddOrderBy(
        Expression<Func<TEntity, object?>> property,
        bool descending,
        NullsPosition nulls)
    {
        var segment = ExpressionHelpers.GetPropertyName(property);

        if (descending)
        {
            segment += " DESC";
        }

        segment += nulls switch
        {
            NullsPosition.First => " NULLS FIRST",
            NullsPosition.Last => " NULLS LAST",
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
    }
}
