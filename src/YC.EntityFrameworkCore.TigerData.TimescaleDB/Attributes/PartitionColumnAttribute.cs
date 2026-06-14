namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     Marks the property as the hypertable's range partition (time) column. Its presence
///     <b>declares the entity as a TimescaleDB hypertable</b> — there is no separate
///     <c>[Hypertable]</c> attribute, and every other feature (columnstore, retention, space
///     dimensions, …) requires this marker on the class.
/// </summary>
/// <remarks>
///     Rules (enforced at model build):
///     <list type="bullet">
///         <item>Exactly one property per class may carry <see cref="PartitionColumnAttribute" />.</item>
///         <item>The column must be <b>non-nullable</b> (use a non-nullable type, e.g.
///             <see cref="System.DateTimeOffset" /> / <see cref="System.DateTime" /> / <see cref="long" />).</item>
///         <item>If the entity has a key, that key <b>must include</b> this column — keyless
///             (<c>HasNoKey()</c>) entities are the natural fit for raw time-series.</item>
///         <item>For an <b>integer</b> partition column (e.g. epoch), configure an integer-now function via
///             the Fluent <c>IsHypertableByInteger(...)</c> before adding policies.</item>
///     </list>
///     Equivalent to the <c>IsHypertable(x =&gt; x.Time, …)</c> Fluent API.
/// </remarks>
/// <example>
///     <code>
///     [PartitionColumn(1, Every.Day)]            // 1-day chunks
///     public DateTimeOffset Time { get; set; }
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PartitionColumnAttribute : Attribute
{
    /// <param name="chunkInterval">
    ///     Chunk size <b>value</b> (must be ≥ 0). For a time column it is combined with
    ///     <paramref name="chunkUnit" /> (e.g. <c>7, Every.Day</c> → 7-day chunks). For an integer
    ///     partition column it is the raw chunk size in the column's own unit (e.g. microseconds) and
    ///     <paramref name="chunkUnit" /> is ignored. <c>0</c> (the default) uses the TimescaleDB default
    ///     of <b>7 days</b>. Tip: size a chunk so the most recent one fits comfortably in memory.
    /// </param>
    /// <param name="chunkUnit">
    ///     Calendar unit of <paramref name="chunkInterval" /> for time columns. Default
    ///     <see cref="Every.Day" />. Use <see cref="Every.Week" /> / <see cref="Every.Month" /> /
    ///     <see cref="Every.Year" /> for calendar intervals a fixed duration cannot express.
    /// </param>
    public PartitionColumnAttribute(long chunkInterval = 0, Every chunkUnit = Every.Day)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkInterval);
        ChunkInterval = chunkInterval;
        ChunkUnit = chunkUnit;
    }

    /// <summary>Chunk size value; <c>0</c> means the TimescaleDB default (7 days). See the constructor.</summary>
    public long ChunkInterval { get; }

    /// <summary>Calendar unit of <see cref="ChunkInterval" /> for time columns (ignored for integer columns).</summary>
    public Every ChunkUnit { get; }

    /// <summary>
    ///     Whether TimescaleDB creates its default index on the partition column when the hypertable is
    ///     created. Default <see langword="true" />. Set <see langword="false" /> only if you define your
    ///     own indexes and want to avoid the automatic one.
    /// </summary>
    public bool CreateDefaultIndexes { get; set; } = true;
}

/// <summary>
///     Adds a hash (space) partition dimension on this property's column — an optional secondary
///     partitioning that spreads each time range across several chunks for parallel I/O. Requires a
///     <see cref="PartitionColumnAttribute" /> on the class.
/// </summary>
/// <remarks>
///     Apply to a <b>different</b> column than the time column (a column cannot be both). Best on a
///     column you frequently filter or group by with moderate-to-high cardinality (device, tenant, …).
///     Adding a dimension is applied in place; removing or changing one rebuilds the table.
///     Equivalent to <c>HasSpacePartition(x =&gt; x.Col, partitions)</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SpacePartitionAttribute : Attribute
{
    /// <param name="partitions">
    ///     Number of hash partitions — must be <b>&gt; 0</b>. Rule of thumb: set it to (a multiple of)
    ///     the number of data nodes / parallel workers you want for a single time range.
    /// </param>
    public SpacePartitionAttribute(int partitions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(partitions, 0);
        Partitions = partitions;
    }

    /// <summary>Number of hash partitions (&gt; 0).</summary>
    public int Partitions { get; }
}

/// <summary>
///     Enables chunk skipping on this property's column (<c>enable_chunk_skipping</c>): TimescaleDB tracks
///     each chunk's min/max for the column and skips chunks that can't match a range filter on it.
/// </summary>
/// <remarks>
///     This is for a <b>non-partition</b> column. Filtering on the time column or a space dimension is
///     <b>already</b> pruned automatically by the hypertable — chunk skipping extends that to a secondary
///     column you also filter on, best when it is <b>correlated with the time column</b> (e.g. a
///     monotonically-growing id).
///     <para>
///     It only speeds up <b>compressed</b> chunks: the min/max is computed when a chunk is converted to
///     the columnstore, so pair it with <see cref="ColumnstoreAttribute" /> / <c>HasColumnstore(...)</c> —
///     without the columnstore it is accepted but has no query effect.
///     </para>
///     Supported column types: <c>smallint</c>, <c>int</c>, <c>bigint</c>, <c>serial</c>, <c>bigserial</c>,
///     <c>date</c>, <c>timestamp</c>, <c>timestamptz</c> (no floating-point or text). Equivalent to
///     <c>HasChunkSkipping(x =&gt; x.Col)</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ChunkSkippingAttribute : Attribute;
