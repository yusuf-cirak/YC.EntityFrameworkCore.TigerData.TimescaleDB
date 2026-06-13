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
