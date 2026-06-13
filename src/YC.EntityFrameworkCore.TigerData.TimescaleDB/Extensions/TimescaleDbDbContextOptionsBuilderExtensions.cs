using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Infrastructure;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Infrastructure.Internal;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     TimescaleDB extension methods for <see cref="DbContextOptionsBuilder" />.
/// </summary>
public static class TimescaleDbDbContextOptionsBuilderExtensions
{
    /// <summary>
    ///     Enables TimescaleDB support (hypertables, columnstore, continuous aggregates, policies,
    ///     jobs and hyperfunction translation) on top of the Npgsql provider.
    ///     Must be called after <c>UseNpgsql(...)</c>.
    /// </summary>
    public static DbContextOptionsBuilder UseTimescaleDb(
        this DbContextOptionsBuilder optionsBuilder,
        Action<TimescaleDbDbContextOptionsBuilder>? timescaleDbOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var extension = optionsBuilder.Options.FindExtension<TimescaleDbOptionsExtension>()
            ?? new TimescaleDbOptionsExtension();

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        timescaleDbOptionsAction?.Invoke(new TimescaleDbDbContextOptionsBuilder(optionsBuilder));

        return optionsBuilder;
    }

    /// <inheritdoc cref="UseTimescaleDb(DbContextOptionsBuilder, Action{TimescaleDbDbContextOptionsBuilder}?)" />
    public static DbContextOptionsBuilder<TContext> UseTimescaleDb<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<TimescaleDbDbContextOptionsBuilder>? timescaleDbOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseTimescaleDb(
            (DbContextOptionsBuilder)optionsBuilder, timescaleDbOptionsAction);
}
