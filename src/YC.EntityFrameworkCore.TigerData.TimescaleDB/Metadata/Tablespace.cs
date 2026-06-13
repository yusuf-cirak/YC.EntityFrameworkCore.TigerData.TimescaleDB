namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     A strongly-typed reference to a PostgreSQL tablespace (a cluster-level object created out of
///     band with <c>CREATE TABLESPACE</c>). Declare named instances so call sites stay type-safe and
///     refactor-safe instead of passing raw strings:
///     <code>public static readonly Tablespace FastSsd = new("fast_ssd");</code>
/// </summary>
public readonly record struct Tablespace
{
    public Tablespace(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }

    public override string ToString() => Name;
}
