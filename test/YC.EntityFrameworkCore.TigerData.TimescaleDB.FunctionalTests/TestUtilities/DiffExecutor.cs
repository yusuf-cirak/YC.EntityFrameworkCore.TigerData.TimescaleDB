using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests.TestUtilities;

/// <summary>
///     Runs differ+generator output for model transitions directly against a database,
///     so incremental migration scenarios don't need a migrations assembly.
/// </summary>
public static class DiffExecutor
{
    private sealed class NeverCachingModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => new();
    }

    private sealed class TestContext(string connectionString, Action<ModelBuilder> configure) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseNpgsql(connectionString)
                .UseTimescaleDb()
                .ReplaceService<IModelCacheKeyFactory, NeverCachingModelCacheKeyFactory>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => configure(modelBuilder);
    }

    /// <summary>SQL commands for the transition <paramref name="source" /> → <paramref name="target" />.</summary>
    public static IReadOnlyList<string> GenerateSql(
        string connectionString,
        Action<ModelBuilder>? source,
        Action<ModelBuilder> target)
    {
        using var targetContext = new TestContext(connectionString, target);

        var differ = targetContext.GetService<IMigrationsModelDiffer>();
        var generator = targetContext.GetService<IMigrationsSqlGenerator>();
        var targetModel = targetContext.GetService<IDesignTimeModel>().Model;

        IReadOnlyList<Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation> operations;
        if (source is null)
        {
            operations = differ.GetDifferences(null, targetModel.GetRelationalModel());
        }
        else
        {
            using var sourceContext = new TestContext(connectionString, source);
            var sourceModel = sourceContext.GetService<IDesignTimeModel>().Model;
            operations = differ.GetDifferences(
                sourceModel.GetRelationalModel(), targetModel.GetRelationalModel());
        }

        return generator.Generate(operations, targetModel).Select(c => c.CommandText).ToList();
    }

    /// <summary>Applies the transition <paramref name="source" /> → <paramref name="target" /> to the database.</summary>
    public static async Task ApplyAsync(
        string connectionString,
        Action<ModelBuilder>? source,
        Action<ModelBuilder> target,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var sql in GenerateSql(connectionString, source, target))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
