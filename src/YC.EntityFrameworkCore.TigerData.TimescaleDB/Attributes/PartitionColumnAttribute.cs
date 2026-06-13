namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     Marks the property as the hypertable's range partition (time) column. Its presence
///     declares the entity as a TimescaleDB hypertable — there is no separate hypertable
///     attribute. Everything else (columnstore, retention, …) requires this marker.
/// </summary>
/// <remarks>Equivalent to the <c>IsHypertable(x =&gt; x.Time, …)</c> Fluent API.</remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PartitionColumnAttribute : Attribute
{
    /// <param name="chunkInterval">
    ///     Chunk size value. For a time column it is combined with <paramref name="chunkUnit" />
    ///     (e.g. <c>7, Every.Day</c>). For an integer partition column it is the raw chunk size in
    ///     the column's own unit (e.g. microseconds) and the unit is ignored. <c>0</c> uses the
    ///     TimescaleDB default (7 days).
    /// </param>
    /// <param name="chunkUnit">Unit of <paramref name="chunkInterval" /> for time columns.</param>
    public PartitionColumnAttribute(long chunkInterval = 0, Every chunkUnit = Every.Day)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkInterval);
        ChunkInterval = chunkInterval;
        ChunkUnit = chunkUnit;
    }

    public long ChunkInterval { get; }

    public Every ChunkUnit { get; }

    /// <summary>Whether TimescaleDB creates its default index on the partition column. Default: true.</summary>
    public bool CreateDefaultIndexes { get; set; } = true;
}

/// <summary>Adds a hash (space) partition dimension on this property's column.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SpacePartitionAttribute : Attribute
{
    /// <param name="partitions">Number of hash partitions.</param>
    public SpacePartitionAttribute(int partitions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(partitions, 0);
        Partitions = partitions;
    }

    public int Partitions { get; }
}

/// <summary>
///     Enables chunk skipping (min/max range tracking) on this property's column
///     (<c>enable_chunk_skipping</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ChunkSkippingAttribute : Attribute;
