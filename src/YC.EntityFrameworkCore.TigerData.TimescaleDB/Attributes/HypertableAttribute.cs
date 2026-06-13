namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     Marks the entity's table as a TimescaleDB hypertable. Mark the partition column
///     with <see cref="HypertablePartitionAttribute" /> on the property itself.
/// </summary>
/// <remarks>Equivalent to the <c>IsHypertable()</c> Fluent API.</remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class HypertableAttribute : Attribute
{
    /// <summary>
    ///     Fallback partition column reference by name. Prefer
    ///     <see cref="HypertablePartitionAttribute" /> on the property for type safety.
    /// </summary>
    public string? PartitionColumn { get; set; }

    /// <summary>Chunk interval in whole days. TimescaleDB default: 7 days.</summary>
    public int ChunkIntervalDays { get; set; }

    /// <summary>Chunk interval in whole hours; combines with <see cref="ChunkIntervalDays" />.</summary>
    public int ChunkIntervalHours { get; set; }

    /// <summary>
    ///     Raw PostgreSQL interval for calendar units a <see cref="TimeSpan" /> cannot express,
    ///     e.g. <c>"1 month"</c>. Mutually exclusive with the numeric properties.
    /// </summary>
    public string? ChunkInterval { get; set; }

    /// <summary>
    ///     Chunk interval for integer partition columns, in the column's own unit
    ///     (e.g. microseconds for a microsecond-epoch column).
    /// </summary>
    public long ChunkIntervalInteger { get; set; }

    /// <summary>Whether TimescaleDB creates its default index on the partition column. TimescaleDB default: true.</summary>
    public bool CreateDefaultIndexes { get; set; } = true;

    /// <summary>
    ///     For integer partition columns: a STABLE SQL function returning the current time in the
    ///     column's unit (<c>set_integer_now_func</c>); required for policies.
    /// </summary>
    public string? IntegerNowFunction { get; set; }
}

/// <summary>Marks the property used as the hypertable's range partition (time) column.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class HypertablePartitionAttribute : Attribute;

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
