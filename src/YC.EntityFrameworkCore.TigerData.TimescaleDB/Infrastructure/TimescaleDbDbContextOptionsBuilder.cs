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
    {
        var extension = (_optionsBuilder.Options.FindExtension<TimescaleDbOptionsExtension>()
                ?? new TimescaleDbOptionsExtension())
            .WithCreateExtension(create);

        ((IDbContextOptionsBuilderInfrastructure)_optionsBuilder).AddOrUpdateExtension(extension);
        return this;
    }
}
