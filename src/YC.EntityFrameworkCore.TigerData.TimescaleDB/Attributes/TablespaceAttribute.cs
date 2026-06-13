namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     Attaches the hypertable to a PostgreSQL tablespace (<c>attach_tablespace</c>); new chunks are
///     distributed round-robin across all attached tablespaces. <b>Repeatable</b> — apply once per
///     tablespace. Requires a <see cref="PartitionColumnAttribute" /> on the class.
/// </summary>
/// <remarks>
///     The tablespace must <b>already exist</b> in the cluster (created out of band with
///     <c>CREATE TABLESPACE name LOCATION '…'</c>); this attribute only attaches an existing one — it
///     does not create it. Use it to spread chunks across multiple disks/volumes for I/O or capacity.
///     Equivalent to <c>HasTablespace(...)</c>.
/// </remarks>
/// <example>
///     <code>
///     [Tablespace("fast_ssd")]
///     [Tablespace("archive_hdd")]
///     public class Reading { … }
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class TablespaceAttribute : Attribute
{
    /// <param name="name">Name of an <b>existing</b> PostgreSQL tablespace (case-sensitive).</param>
    public TablespaceAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Name of the attached tablespace.</summary>
    public string Name { get; }
}
