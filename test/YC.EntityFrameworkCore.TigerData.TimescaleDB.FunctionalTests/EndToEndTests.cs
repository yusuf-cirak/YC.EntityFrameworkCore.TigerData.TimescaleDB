using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Npgsql;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Design.Scaffolding;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests;

[Collection(TimescaleDbCollection.Name)]
public class EndToEndTests(TimescaleDbContainerFixture fixture)
{
    [Fact]
    public async Task Full_lifecycle_creates_queries_and_scaffolds()
    {
        var connectionString = await fixture.CreateDatabaseAsync(TestContext.Current.CancellationToken);

        // The user-defined job's procedure must exist before add_job references it.
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE PROCEDURE test_job_proc(job_id int, config jsonb)
                LANGUAGE plpgsql AS $$ BEGIN END $$;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var context = new MetricsContext(connectionString);

        // Creates: extension, hypertable (WITH clause), dimension, columnstore settings,
        // policies, continuous aggregate, refresh policy and the custom job.
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        // --- DDL assertions via the informational views -------------------------------

        Assert.Equal(1L, await ScalarAsync(context,
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'readings'"));

        Assert.Equal(2L, await ScalarAsync(context,
            "SELECT count(*) FROM timescaledb_information.dimensions WHERE hypertable_name = 'readings'"));

        Assert.Equal(1L, await ScalarAsync(context,
            "SELECT count(*) FROM timescaledb_information.jobs WHERE proc_name = 'policy_retention' "
            + "AND hypertable_name = 'readings'"));

        Assert.Equal(1L, await ScalarAsync(context,
            "SELECT count(*) FROM timescaledb_information.jobs WHERE proc_name IN "
            + "('policy_compression', 'policy_columnstore') AND hypertable_name = 'readings'"));

        Assert.Equal(1L, await ScalarAsync(context,
            "SELECT count(*) FROM timescaledb_information.continuous_aggregates "
            + "WHERE view_name = 'hourly_averages'"));

        Assert.Equal(1L, await ScalarAsync(context,
            "SELECT count(*) FROM timescaledb_information.jobs "
            + "WHERE application_name LIKE 'functional_test_job [%]'"));

        // --- data + hyperfunction queries ----------------------------------------------

        // Keyless entities are not tracked; time-series ingestion goes through SQL.
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO readings (time, device_id, value) VALUES
                ('2026-06-01T10:00:00Z', 'a', 10),
                ('2026-06-01T10:20:00Z', 'a', 30),
                ('2026-06-01T10:40:00Z', 'b', 50)
            """,
            TestContext.Current.CancellationToken);

        var buckets = await context.Readings
            .GroupBy(r => EF.Functions.TimeBucket(TimeSpan.FromHours(1), r.Time))
            .Select(g => new
            {
                Bucket = g.Key,
                Avg = g.Average(x => x.Value),
                First = EF.Functions.First(g.Select(x => ValueTuple.Create(x.Value, x.Time))),
                Last = EF.Functions.Last(g.Select(x => ValueTuple.Create(x.Value, x.Time))),
            })
            .ToListAsync(TestContext.Current.CancellationToken);

        var bucket = Assert.Single(buckets);
        Assert.Equal(30, bucket.Avg);
        Assert.Equal(10, bucket.First);
        Assert.Equal(50, bucket.Last);

        // --- continuous aggregate materializes and is queryable ------------------------

        await context.Database.ExecuteSqlRawAsync(
            "CALL refresh_continuous_aggregate('hourly_averages', NULL, NULL)",
            TestContext.Current.CancellationToken);

        var averages = await context.HourlyAverages
            .OrderBy(h => h.DeviceId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, averages.Count);
        Assert.Equal(20, averages[0].Average);
        Assert.Equal(50, averages[1].Average);

        // --- scaffolding round-trip -----------------------------------------------------

        var factory = new TimescaleDbDatabaseModelFactory(
            context.GetService<IDiagnosticsLogger<DbLoggerCategory.Scaffolding>>());

        var databaseModel = factory.Create(
            connectionString,
            new DatabaseModelFactoryOptions(tables: [], schemas: []));

        var readings = Assert.Single(databaseModel.Tables, t => t.Name == "readings");

        Assert.Equal(true, readings[TimescaleDbAnnotationNames.IsHypertable]);
        Assert.Equal("time", readings[TimescaleDbAnnotationNames.PartitionColumn]);
        Assert.Equal("1 day", readings[TimescaleDbAnnotationNames.ChunkInterval]);
        Assert.Equal("device_id", readings[TimescaleDbAnnotationNames.SpacePartitionColumn]);
        Assert.Equal(4, readings[TimescaleDbAnnotationNames.SpacePartitions]);
        Assert.Equal(true, readings[TimescaleDbAnnotationNames.ColumnstoreEnabled]);
        Assert.Equal("90 days", readings[TimescaleDbAnnotationNames.RetentionPolicyDropAfter]);
        Assert.Equal("7 days", readings[TimescaleDbAnnotationNames.ColumnstorePolicyAfter]);

        var jobs = TimescaleDbJob.Deserialize(
            databaseModel[TimescaleDbAnnotationNames.Jobs] as string);
        Assert.Contains(jobs, j => j.Name == "functional_test_job" && j.Procedure == "test_job_proc");
    }

    private static async Task<object?> ScalarAsync(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
