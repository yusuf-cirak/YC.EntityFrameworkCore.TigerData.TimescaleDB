using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests;

/// <summary>
///     The per-entity toggles for automatic data operations, verified against a real TimescaleDB:
///     <c>WithMigrateData</c> (plain→hypertable) and <c>WithAutoDecompress</c> (columnstore disable).
/// </summary>
[Collection(TimescaleDbCollection.Name)]
public class MigrationOptionsTests(TimescaleDbContainerFixture fixture)
{
    private class Metric
    {
        public DateTimeOffset Time { get; set; }
        public string Source { get; set; } = null!;
        public double Value { get; set; }
    }

    private static Action<ModelBuilder> Plain => mb => mb.Entity<Metric>(e =>
    {
        e.HasNoKey();
        e.ToTable("metrics");
        e.Property(x => x.Time).HasColumnName("time");
        e.Property(x => x.Source).HasColumnName("source");
        e.Property(x => x.Value).HasColumnName("value");
    });

    private static Action<ModelBuilder> Hypertable(Action<EntityTypeBuilder<Metric>>? extra = null)
        => mb => mb.Entity<Metric>(e =>
        {
            e.HasNoKey();
            e.ToTable("metrics");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.Value).HasColumnName("value");
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
            extra?.Invoke(e);
        });

    [Fact]
    public async Task WithMigrateData_false_rejects_a_populated_table_but_allows_an_empty_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        await DiffExecutor.ApplyAsync(cs, null, Plain, ct);
        await ExecuteAsync(cs,
            """
            INSERT INTO metrics (time, source, value)
            SELECT '2026-06-01T00:00:00Z'::timestamptz + (n || ' hours')::interval, 's', n
            FROM generate_series(1, 10) AS n
            """, ct);

        // migrate_data is suppressed → TimescaleDB refuses to convert a non-empty table.
        await Assert.ThrowsAsync<PostgresException>(() =>
            DiffExecutor.ApplyAsync(cs, Plain, Hypertable(e => e.WithMigrateData(false)), ct));

        // The default (migrate) path succeeds and keeps the rows.
        await DiffExecutor.ApplyAsync(cs, Plain, Hypertable(), ct);
        Assert.Equal(10L, await ScalarAsync(cs, "SELECT count(*) FROM metrics", ct));
    }

    [Fact]
    public async Task WithMigrateData_false_converts_an_empty_table()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        await DiffExecutor.ApplyAsync(cs, null, Plain, ct);
        await DiffExecutor.ApplyAsync(cs, Plain, Hypertable(e => e.WithMigrateData(false)), ct);

        Assert.Equal(1L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'metrics'", ct));
    }

    [Fact]
    public async Task WithAutoDecompress_false_disables_columnstore_when_no_chunk_is_compressed()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        var withColumnstore = Hypertable(e =>
            e.HasColumnstore(c => c.SegmentBy(x => x.Source).OrderByDescending(x => x.Time)));

        await DiffExecutor.ApplyAsync(cs, null, withColumnstore, ct);

        // No chunk is compressed, so skipping decompression is safe.
        await DiffExecutor.ApplyAsync(cs, withColumnstore, Hypertable(e => e.WithAutoDecompress(false)), ct);

        Assert.Equal("n", await ScalarAsync(cs,
            "SELECT CASE WHEN compression_enabled THEN 's' ELSE 'n' END "
            + "FROM timescaledb_information.hypertables WHERE hypertable_name = 'metrics'", ct));
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
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
