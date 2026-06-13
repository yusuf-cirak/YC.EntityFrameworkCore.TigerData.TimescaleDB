namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     Adds a reorder policy (<c>add_reorder_policy</c>) that reorders chunks by
///     the given index for better scan locality.
/// </summary>
/// <remarks>Equivalent to the <c>HasReorderPolicy()</c> Fluent API.</remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ReorderPolicyAttribute : Attribute
{
    /// <param name="indexName">Name of an existing index on the hypertable.</param>
    public ReorderPolicyAttribute(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        IndexName = indexName;
    }

    /// <summary>Index used to reorder chunk data.</summary>
    public string IndexName { get; }
}
