using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Migrations;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Infrastructure;

public class UseTimescaleDbTests
{
    private sealed class TestContext(DbContextOptions<TestContext> options) : DbContext(options);

    private static TestContext CreateContext()
        => new(new DbContextOptionsBuilder<TestContext>()
            .UseNpgsql("Host=localhost;Database=unit_test_only")
            .UseTimescaleDb()
            .Options);

    [Fact]
    public void Replaces_migrations_sql_generator()
    {
        using var context = CreateContext();

        var generator = context.GetService<IMigrationsSqlGenerator>();

        Assert.IsType<TimescaleDbMigrationsSqlGenerator>(generator);
    }

    [Fact]
    public void Replaces_relational_annotation_provider()
    {
        using var context = CreateContext();

        var provider = context.GetService<IRelationalAnnotationProvider>();

        Assert.IsType<TimescaleDbAnnotationProvider>(provider);
    }

    [Fact]
    public void Throws_without_npgsql()
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseTimescaleDb()
            .Options;

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var context = new TestContext(options);
            try
            {
                _ = context.Model;
            }
            finally
            {
                try
                {
                    context.Dispose();
                }
                catch (InvalidOperationException)
                {
                    // Dispose touches the (invalid) internal service provider as well.
                }
            }
        });

        Assert.Contains("UseNpgsql", exception.Message);
    }
}
