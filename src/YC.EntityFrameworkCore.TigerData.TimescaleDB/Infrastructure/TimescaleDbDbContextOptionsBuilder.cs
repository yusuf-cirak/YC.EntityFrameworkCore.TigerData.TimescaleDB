using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Infrastructure.Internal;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Infrastructure;

/// <summary>
///     Allows TimescaleDB-specific configuration inside <c>UseTimescaleDb(o => ...)</c>.
/// </summary>
public class TimescaleDbDbContextOptionsBuilder
{
    private readonly DbContextOptionsBuilder _optionsBuilder;

    public TimescaleDbDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
        => _optionsBuilder = optionsBuilder;

    /// <summary>
    ///     Controls whether the first migration emits <c>CREATE EXTENSION IF NOT EXISTS timescaledb;</c>.
    ///     Enabled by default; disable when the extension is provisioned out of band (e.g. managed cloud).
    /// </summary>
    public virtual TimescaleDbDbContextOptionsBuilder CreateExtension(bool create = true)
        => Update(e => e.WithCreateExtension(create));

    /// <summary>
    ///     Model-wide default for migrating existing rows when converting a populated plain table into a
    ///     hypertable. Default <c>true</c>; per-entity <c>WithMigrateData(...)</c> overrides it.
    /// </summary>
    public virtual TimescaleDbDbContextOptionsBuilder MigrateData(bool migrateData = true)
        => Update(e => e.WithMigrateData(migrateData));

    /// <summary>
    ///     Model-wide default for allowing data-copying table rebuilds. Default <c>true</c>; when
    ///     <c>false</c>, changes that need a rebuild throw at migration generation. Per-entity
    ///     <c>WithRebuildData(...)</c> overrides it.
    /// </summary>
    public virtual TimescaleDbDbContextOptionsBuilder RebuildData(bool rebuildData = true)
        => Update(e => e.WithRebuildData(rebuildData));

    /// <summary>
    ///     Model-wide default for decompressing chunks when disabling the columnstore. Default
    ///     <c>true</c>; per-entity <c>WithAutoDecompress(...)</c> overrides it.
    /// </summary>
    public virtual TimescaleDbDbContextOptionsBuilder AutoDecompress(bool autoDecompress = true)
        => Update(e => e.WithAutoDecompress(autoDecompress));

    private TimescaleDbDbContextOptionsBuilder Update(
        Func<TimescaleDbOptionsExtension, TimescaleDbOptionsExtension> update)
    {
        var extension = update(
            _optionsBuilder.Options.FindExtension<TimescaleDbOptionsExtension>()
            ?? new TimescaleDbOptionsExtension());

        ((IDbContextOptionsBuilderInfrastructure)_optionsBuilder).AddOrUpdateExtension(extension);
        return this;
    }
}
