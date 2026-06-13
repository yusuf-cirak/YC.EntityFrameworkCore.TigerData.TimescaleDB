using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

/// <summary>Builds finalized models through the full Npgsql + TimescaleDB pipeline.</summary>
public static class TimescaleDbModelBuilder
{
    /// <summary>Each context instance gets its own model; tests configure the same context type differently.</summary>
    private sealed class NeverCachingModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => new();
    }

    private sealed class TestContext(Action<ModelBuilder> configure) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseNpgsql("Host=localhost;Database=unit_test_only")
                .UseTimescaleDb()
                .ReplaceService<IModelCacheKeyFactory, NeverCachingModelCacheKeyFactory>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => configure(modelBuilder);
    }

    /// <summary>
    ///     Returns the design-time model: migrations read this one, and the runtime model strips
    ///     design-time-only annotations such as storage parameters.
    /// </summary>
    public static IModel Build(Action<ModelBuilder> configure)
    {
        using var context = new TestContext(configure);
        return context.GetService<IDesignTimeModel>().Model;
    }
}
