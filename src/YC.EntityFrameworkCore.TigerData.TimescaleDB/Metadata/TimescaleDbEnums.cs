namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>The unit of a TimescaleDB interval (chunk size, policy ages, schedules).</summary>
public enum Every
{
    Second,
    Minute,
    Hour,
    Day,
    Week,
    Month,
    Year,
}

/// <summary>Sort direction of a columnstore ordering column.</summary>
public enum Sort
{
    Ascending,
    Descending,
}

/// <summary>NULL ordering of a columnstore ordering column.</summary>
public enum Nulls
{
    /// <summary>PostgreSQL default (NULLS LAST for ascending, NULLS FIRST for descending).</summary>
    Default,
    First,
    Last,
}

/// <summary>Kind of columnstore sparse index maintained per chunk for a column.</summary>
public enum SparseIndexKind
{
    /// <summary>Tracks the per-chunk minimum and maximum (<c>minmax</c>) — best for range filters.</summary>
    MinMax,

    /// <summary>Maintains a bloom filter (<c>bloom</c>) — best for equality filters on high-cardinality columns.</summary>
    Bloom,
}
