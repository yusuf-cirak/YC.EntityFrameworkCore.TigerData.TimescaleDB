namespace YC.EntityFrameworkCore.TigerData.TimescaleDB;

/// <summary>
///     Per-entity toggles for the automatic, data-heavy operations the migration engine injects during
///     table transitions. All default to <c>true</c> (current behavior). Equivalent to the
///     <c>WithMigrateData</c> / <c>WithRebuildData</c> / <c>WithAutoDecompress</c> Fluent API.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class MigrationOptionsAttribute : Attribute
{
    /// <summary>
    ///     Migrate existing rows into chunks when converting a populated plain table to a hypertable
    ///     (<c>migrate_data => true</c>). Default: <c>true</c>.
    /// </summary>
    public bool MigrateData { get; set; } = true;

    /// <summary>
    ///     Allow a data-copying table rebuild for changes TimescaleDB cannot apply in place. Default:
    ///     <c>true</c>. When <c>false</c>, such a change throws at migration generation.
    /// </summary>
    public bool RebuildData { get; set; } = true;

    /// <summary>
    ///     Convert every compressed chunk back to rowstore before disabling the columnstore. Default:
    ///     <c>true</c>.
    /// </summary>
    public bool AutoDecompress { get; set; } = true;
}
