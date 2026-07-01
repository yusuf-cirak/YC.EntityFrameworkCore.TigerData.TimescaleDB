using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;
using Npgsql;
using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Design.Scaffolding;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests.TestUtilities;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests;

/// <summary>
///     Functional coverage for the four recently-added features, exercised against a real
///     TimescaleDB container: sparse columnstore indexes, creation-time retention and columnstore
///     policies and <c>by_range</c> secondary dimensions. Each test applies a migration (Up) and
///     asserts the resulting object via the <c>timescaledb_information.*</c> catalog, additionally
///     scaffolding the database back and asserting their annotations reappear (model → DB → scaffold
///     fidelity).
/// </summary>
[Collection(TimescaleDbCollection.Name)]
public class NewFeaturesTests(TimescaleDbContainerFixture fixture)
{
    private class Metric
    {
        public DateTimeOffset Time { get; set; }
        public string Device { get; set; } = null!;
        public int Region { get; set; }
        public long Shard { get; set; }
        public double Value { get; set; }
    }

    private static void MapMetric(ModelBuilder mb, Action<EntityTypeBuilder<Metric>>? extra = null)
        => mb.Entity<Metric>(e =>
        {
            e.HasNoKey();
            e.ToTable("metrics");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.Device).HasColumnName("device");
            e.Property(x => x.Region).HasColumnName("region");
            e.Property(x => x.Shard).HasColumnName("shard");
            e.Property(x => x.Value).HasColumnName("value");
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
            extra?.Invoke(e);
        });

    // ------------------------------------------------------------------ 1. Sparse columnstore index

    [Fact]
    public async Task Sparse_index_created_and_round_trips()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        await DiffExecutor.ApplyAsync(cs, null, mb => MapMetric(mb, e =>
            e.HasColumnstore(csb => csb
                .SegmentBy(x => x.Device)
                .OrderByDescending(x => x.Time)
                .SparseIndexMinMax(x => x.Value)
                .SparseIndexBloom(x => x.Region))), ct);

        // The two explicitly-configured sparse indexes are recorded with source='config'.
        Assert.Equal(2L, await ScalarAsync(cs,
            """
            SELECT count(*)
            FROM timescaledb_information.hypertable_columnstore_settings s
            JOIN pg_class c ON c.oid = s.hypertable
            CROSS JOIN LATERAL jsonb_array_elements(s.index) e
            WHERE c.relname = 'metrics' AND e->>'source' = 'config'
            """, ct));
        Assert.Equal(1L, await ScalarAsync(cs,
            """
            SELECT count(*)
            FROM timescaledb_information.hypertable_columnstore_settings s
            JOIN pg_class c ON c.oid = s.hypertable
            CROSS JOIN LATERAL jsonb_array_elements(s.index) e
            WHERE c.relname = 'metrics'
              AND e->>'type' = 'minmax' AND e->>'column' = 'value' AND e->>'source' = 'config'
            """, ct));
        Assert.Equal(1L, await ScalarAsync(cs,
            """
            SELECT count(*)
            FROM timescaledb_information.hypertable_columnstore_settings s
            JOIN pg_class c ON c.oid = s.hypertable
            CROSS JOIN LATERAL jsonb_array_elements(s.index) e
            WHERE c.relname = 'metrics'
              AND e->>'type' = 'bloom' AND e->>'column' = 'region' AND e->>'source' = 'config'
            """, ct));

        var metrics = ScaffoldTable(cs, "metrics");
        Assert.Equal("minmax(\"value\"), bloom(\"region\")",
            metrics[TimescaleDbAnnotationNames.ColumnstoreSparseIndex]);
    }

    // ------------------------------------------------------------------ 2. Creation-time retention

    [Fact]
    public async Task Creation_time_retention_policy_created_and_round_trips()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        await DiffExecutor.ApplyAsync(cs, null, mb =>
            MapMetric(mb, e => e.HasRetentionPolicy(dropCreatedBefore: TimeSpan.FromDays(90))), ct);

        // The retention job is keyed on creation time (drop_created_before), not drop_after.
        Assert.Equal(1L, await ScalarAsync(cs,
            """
            SELECT count(*) FROM timescaledb_information.jobs
            WHERE hypertable_name = 'metrics' AND proc_name = 'policy_retention'
              AND config->>'drop_created_before' = '90 days'
            """, ct));
        Assert.Equal(0L, await ScalarAsync(cs,
            """
            SELECT count(*) FROM timescaledb_information.jobs
            WHERE hypertable_name = 'metrics' AND proc_name = 'policy_retention'
              AND config ? 'drop_after'
            """, ct));

        var metrics = ScaffoldTable(cs, "metrics");
        Assert.Equal("90 days", metrics[TimescaleDbAnnotationNames.RetentionPolicyDropCreatedBefore]);
        Assert.Null(metrics[TimescaleDbAnnotationNames.RetentionPolicyDropAfter]);
    }

    // ------------------------------------------------------------------ 3. Creation-time columnstore policy

    [Fact]
    public async Task Creation_time_columnstore_policy_created_and_round_trips()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        await DiffExecutor.ApplyAsync(cs, null, mb => MapMetric(mb, e =>
        {
            e.HasColumnstore(csb => csb.SegmentBy(x => x.Device).OrderByDescending(x => x.Time));
            e.HasColumnstorePolicy(createdBefore: TimeSpan.FromDays(7));
        }), ct);

        // The columnstore policy job is keyed on creation time (compress_created_before), not compress_after.
        Assert.Equal(1L, await ScalarAsync(cs,
            """
            SELECT count(*) FROM timescaledb_information.jobs
            WHERE hypertable_name = 'metrics' AND proc_name IN ('policy_compression', 'policy_columnstore')
              AND config->>'compress_created_before' = '7 days'
            """, ct));
        Assert.Equal(0L, await ScalarAsync(cs,
            """
            SELECT count(*) FROM timescaledb_information.jobs
            WHERE hypertable_name = 'metrics' AND proc_name IN ('policy_compression', 'policy_columnstore')
              AND config ? 'compress_after'
            """, ct));

        var metrics = ScaffoldTable(cs, "metrics");
        Assert.Equal("7 days", metrics[TimescaleDbAnnotationNames.ColumnstorePolicyCreatedBefore]);
        Assert.Null(metrics[TimescaleDbAnnotationNames.ColumnstorePolicyAfter]);
    }

    // ------------------------------------------------------------------ 4. by_range secondary dimension

    [Fact]
    public async Task Range_dimension_created_and_round_trips()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        await DiffExecutor.ApplyAsync(cs, null, mb =>
            MapMetric(mb, e => e.HasRangePartition(x => x.Shard, 1_000_000L)), ct);

        // 1 primary time dimension + 1 secondary range dimension on 'shard'.
        Assert.Equal(2L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.dimensions WHERE hypertable_name = 'metrics'", ct));
        Assert.Equal(1_000_000L, Convert.ToInt64(await ScalarAsync(cs,
            """
            SELECT integer_interval FROM timescaledb_information.dimensions
            WHERE hypertable_name = 'metrics' AND column_name = 'shard'
            """, ct)));

        var metrics = ScaffoldTable(cs, "metrics");
        Assert.Equal("shard:1000000", metrics[TimescaleDbAnnotationNames.RangeDimensions]);
    }

    // ------------------------------------------------------------------ helpers

    private static DatabaseTable ScaffoldTable(string connectionString, string name)
    {
        var options = new DbContextOptionsBuilder().UseNpgsql(connectionString).UseTimescaleDb().Options;
        using var context = new DbContext(options);

        var factory = new TimescaleDbDatabaseModelFactory(
            context.GetService<IDiagnosticsLogger<DbLoggerCategory.Scaffolding>>());

        var databaseModel = factory.Create(
            connectionString, new DatabaseModelFactoryOptions(tables: [], schemas: []));

        return Assert.Single(databaseModel.Tables, t => t.Name == name);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct);
    }
}
