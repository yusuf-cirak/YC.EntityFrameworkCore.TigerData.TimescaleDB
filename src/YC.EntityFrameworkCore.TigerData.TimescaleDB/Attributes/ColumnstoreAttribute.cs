namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     Enables the TimescaleDB columnstore (compression) on the hypertable. Configure the
///     layout with <see cref="ColumnstoreSegmentByAttribute" /> and
///     <see cref="ColumnstoreOrderByAttribute" /> on the properties themselves.
/// </summary>
/// <remarks>Equivalent to the <c>HasColumnstore()</c> Fluent API.</remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ColumnstoreAttribute : Attribute
{
    /// <summary>Merge chunks up to this many days when compressing (<c>timescaledb.compress_chunk_time_interval</c>).</summary>
    public int ChunkMergeIntervalDays { get; set; }

    /// <summary>Raw-interval variant of <see cref="ChunkMergeIntervalDays" />, e.g. <c>"1 month"</c>.</summary>
    public string? ChunkMergeInterval { get; set; }
}

/// <summary>Includes this property's column in <c>timescaledb.segmentby</c>.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ColumnstoreSegmentByAttribute : Attribute
{
    /// <param name="order">Position among the segment-by columns (0-based).</param>
    public ColumnstoreSegmentByAttribute(int order = 0)
        => Order = order;

    public int Order { get; }
}

/// <summary>Includes this property's column in <c>timescaledb.orderby</c>.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ColumnstoreOrderByAttribute : Attribute
{
    /// <param name="order">Position among the order-by columns (0-based).</param>
    public ColumnstoreOrderByAttribute(int order = 0)
        => Order = order;

    public int Order { get; }

    /// <summary>Sort direction; ascending when false (default).</summary>
    public bool Descending { get; set; }

    /// <summary>Place NULLs first. Mutually exclusive with <see cref="NullsLast" />.</summary>
    public bool NullsFirst { get; set; }

    /// <summary>Place NULLs last. Mutually exclusive with <see cref="NullsFirst" />.</summary>
    public bool NullsLast { get; set; }
}
