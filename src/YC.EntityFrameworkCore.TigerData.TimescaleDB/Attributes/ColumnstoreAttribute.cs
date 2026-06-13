namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     Enables the TimescaleDB columnstore (compression) on the hypertable and, optionally, its
///     automatic conversion policy and chunk merging. The columnstore is also enabled implicitly
///     by any <see cref="SegmentByAttribute" /> / <see cref="OrderByAttribute" /> on a property.
///     Requires a <see cref="PartitionColumnAttribute" /> on the class.
/// </summary>
/// <remarks>Equivalent to the <c>HasColumnstore(...)</c> / <c>HasColumnstorePolicy(...)</c> Fluent API.</remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ColumnstoreAttribute : Attribute
{
    /// <summary>
    ///     Age after which chunks are converted to columnstore (with <see cref="CompressAfterUnit" />).
    ///     Set to add an <c>add_columnstore_policy</c>. <c>0</c> adds no policy.
    /// </summary>
    public long CompressAfter { get; set; }

    /// <summary>Unit of <see cref="CompressAfter" />.</summary>
    public Every CompressAfterUnit { get; set; } = Every.Day;

    /// <summary>How often the policy job runs (with <see cref="ScheduleIntervalUnit" />). <c>0</c> uses the default.</summary>
    public long ScheduleInterval { get; set; }

    /// <summary>Unit of <see cref="ScheduleInterval" />.</summary>
    public Every ScheduleIntervalUnit { get; set; } = Every.Hour;

    /// <summary>Merge chunks up to this span when compressing (with <see cref="MergeChunksUnit" />). <c>0</c> = none.</summary>
    public long MergeChunks { get; set; }

    /// <summary>Unit of <see cref="MergeChunks" />.</summary>
    public Every MergeChunksUnit { get; set; } = Every.Day;
}

/// <summary>Includes this property's column in the columnstore <c>segmentby</c> list.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SegmentByAttribute : Attribute
{
    /// <param name="order">Position among the segment-by columns (0-based).</param>
    public SegmentByAttribute(int order = 0)
        => Order = order;

    public int Order { get; }
}

/// <summary>Includes this property's column in the columnstore <c>orderby</c> list.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OrderByAttribute : Attribute
{
    /// <param name="order">Position among the order-by columns (0-based).</param>
    /// <param name="direction">Sort direction.</param>
    /// <param name="nulls">NULL ordering.</param>
    public OrderByAttribute(int order = 0, Sort direction = Sort.Ascending, Nulls nulls = Nulls.Default)
    {
        Order = order;
        Direction = direction;
        Nulls = nulls;
    }

    public int Order { get; }

    public Sort Direction { get; }

    public Nulls Nulls { get; }
}
