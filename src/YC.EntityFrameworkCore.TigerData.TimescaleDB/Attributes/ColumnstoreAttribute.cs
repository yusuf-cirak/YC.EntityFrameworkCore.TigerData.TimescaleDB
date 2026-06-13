namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     Enables the TimescaleDB columnstore (compression) on the hypertable and, optionally, its
///     automatic conversion policy and chunk merging. The columnstore is <b>also enabled implicitly</b>
///     by any <see cref="SegmentByAttribute" /> / <see cref="OrderByAttribute" /> on a property, so add
///     this class attribute only when you need the policy / merge options or want to be explicit.
///     Requires a <see cref="PartitionColumnAttribute" /> on the class.
/// </summary>
/// <remarks>
///     Tuning: pick <see cref="SegmentByAttribute" /> columns you filter by equality (low-cardinality
///     grouping keys); pick <see cref="OrderByAttribute" /> columns you range-scan or order by (often the
///     time column descending). A column must not appear in both. Equivalent to the
///     <c>HasColumnstore(...)</c> / <c>HasColumnstorePolicy(...)</c> Fluent API.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ColumnstoreAttribute : Attribute
{
    /// <summary>
    ///     Age after which chunks are automatically converted to the columnstore, combined with
    ///     <see cref="CompressAfterUnit" /> (e.g. <c>7</c> + <see cref="Every.Day" /> → compress chunks
    ///     older than 7 days). Adds an <c>add_columnstore_policy</c>. <c>0</c> (default) adds <b>no</b>
    ///     policy — the columnstore is enabled but nothing is compressed automatically.
    /// </summary>
    public long CompressAfter { get; set; }

    /// <summary>Calendar unit of <see cref="CompressAfter" />. Default <see cref="Every.Day" />.</summary>
    public Every CompressAfterUnit { get; set; } = Every.Day;

    /// <summary>
    ///     How often the conversion policy job runs, combined with <see cref="ScheduleIntervalUnit" />.
    ///     <c>0</c> (default) uses the TimescaleDB default cadence. Only meaningful when
    ///     <see cref="CompressAfter" /> is set.
    /// </summary>
    public long ScheduleInterval { get; set; }

    /// <summary>Calendar unit of <see cref="ScheduleInterval" />. Default <see cref="Every.Hour" />.</summary>
    public Every ScheduleIntervalUnit { get; set; } = Every.Hour;

    /// <summary>
    ///     Merge chunks up to this span when compressing (<c>compress_chunk_time_interval</c>), combined
    ///     with <see cref="MergeChunksUnit" />. <c>0</c> (default) = no merging. Use to consolidate many
    ///     small chunks into fewer, larger compressed chunks.
    /// </summary>
    public long MergeChunks { get; set; }

    /// <summary>Calendar unit of <see cref="MergeChunks" />. Default <see cref="Every.Day" />.</summary>
    public Every MergeChunksUnit { get; set; } = Every.Day;
}

/// <summary>
///     Includes this property's column in the columnstore <c>segmentby</c> list. Adding it (or
///     <see cref="OrderByAttribute" />) to any property enables the columnstore.
/// </summary>
/// <remarks>
///     Choose columns you filter by <b>equality</b> (e.g. device_id, tenant). Avoid high-cardinality or
///     continuously-varying columns. A <c>segmentby</c> column must not also be an
///     <see cref="OrderByAttribute" /> column.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SegmentByAttribute : Attribute
{
    /// <param name="order">
    ///     Position among the segment-by columns (0-based). Use distinct values to make the order
    ///     deterministic when several properties are marked. Default <c>0</c>.
    /// </param>
    public SegmentByAttribute(int order = 0)
        => Order = order;

    /// <summary>Position among the segment-by columns (0-based).</summary>
    public int Order { get; }
}

/// <summary>
///     Includes this property's column in the columnstore <c>orderby</c> list (the order rows are sorted
///     within a compressed chunk). Adding it (or <see cref="SegmentByAttribute" />) to any property
///     enables the columnstore.
/// </summary>
/// <remarks>
///     Choose columns you <b>range-scan or order by</b> — commonly the time column
///     <see cref="Sort.Descending" /> so the newest rows decompress first. A column must not be both
///     <c>orderby</c> and <see cref="SegmentByAttribute" />.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OrderByAttribute : Attribute
{
    /// <param name="order">Position among the order-by columns (0-based). Default <c>0</c>.</param>
    /// <param name="direction">Sort direction. Default <see cref="Sort.Ascending" />.</param>
    /// <param name="nulls">
    ///     NULL ordering. Default <see cref="Nulls.Default" /> (PostgreSQL default: NULLS LAST for
    ///     ascending, NULLS FIRST for descending).
    /// </param>
    public OrderByAttribute(int order = 0, Sort direction = Sort.Ascending, Nulls nulls = Nulls.Default)
    {
        Order = order;
        Direction = direction;
        Nulls = nulls;
    }

    /// <summary>Position among the order-by columns (0-based).</summary>
    public int Order { get; }

    /// <summary>Sort direction.</summary>
    public Sort Direction { get; }

    /// <summary>NULL ordering.</summary>
    public Nulls Nulls { get; }
}
