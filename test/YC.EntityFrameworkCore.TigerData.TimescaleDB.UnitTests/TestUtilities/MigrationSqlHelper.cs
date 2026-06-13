using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Infrastructure;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

/// <summary>
///     Diffs models and generates migration SQL through the full Npgsql + TimescaleDB pipeline,
///     without touching a database.
/// </summary>
public static class MigrationSqlHelper
{
    private sealed class NeverCachingModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => new();
    }

    private sealed class TestContext(
        Action<ModelBuilder> configure,
        Action<TimescaleDbDbContextOptionsBuilder>? configureOptions = null) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseNpgsql("Host=localhost;Database=unit_test_only")
                .UseTimescaleDb(configureOptions)
                .ReplaceService<IModelCacheKeyFactory, NeverCachingModelCacheKeyFactory>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => configure(modelBuilder);
    }

    /// <summary>Operations produced by diffing <paramref name="source" /> → <paramref name="target" />.</summary>
    public static IReadOnlyList<MigrationOperation> Diff(
        Action<ModelBuilder>? source,
        Action<ModelBuilder> target,
        Action<TimescaleDbDbContextOptionsBuilder>? configureOptions = null)
    {
        using var context = new TestContext(target, configureOptions);

        var differ = context.GetService<IMigrationsModelDiffer>();
        var designTimeModel = context.GetService<IDesignTimeModel>().Model;

        IRelationalModel? sourceModel = null;
        if (source is not null)
        {
            using var sourceContext = new TestContext(source, configureOptions);
            sourceModel = sourceContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();

            return differ.GetDifferences(sourceModel, designTimeModel.GetRelationalModel());
        }

        return differ.GetDifferences(null, designTimeModel.GetRelationalModel());
    }

    /// <summary>SQL commands for the given model creation (empty → target).</summary>
    public static IReadOnlyList<string> GenerateSql(Action<ModelBuilder> target)
        => GenerateSql(null, target);

    /// <summary>SQL commands for the migration <paramref name="source" /> → <paramref name="target" />.</summary>
    public static IReadOnlyList<string> GenerateSql(
        Action<ModelBuilder>? source,
        Action<ModelBuilder> target,
        Action<TimescaleDbDbContextOptionsBuilder>? configureOptions = null)
    {
        using var context = new TestContext(target, configureOptions);

        var operations = Diff(source, target, configureOptions);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var designTimeModel = context.GetService<IDesignTimeModel>().Model;

        return generator
            .Generate(operations, designTimeModel)
            .Select(c => c.CommandText)
            .ToList();
    }
}
