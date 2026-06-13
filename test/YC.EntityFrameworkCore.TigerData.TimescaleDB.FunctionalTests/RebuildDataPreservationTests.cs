using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests;

/// <summary>
///     The universal rebuild path (<c>CREATE TABLE (LIKE … INCLUDING ALL)</c> + <c>INSERT … SELECT *</c>)
///     must never lose data. Verified against a real TimescaleDB for the dimension-drop case, which is
///     only covered by SQL-shape assertions in the unit tests.
/// </summary>
[Collection(TimescaleDbCollection.Name)]
public class RebuildDataPreservationTests(TimescaleDbContainerFixture fixture)
{
    private class Sensor
    {
        public DateTimeOffset Time { get; set; }
        public string Device { get; set; } = null!;
        public double Value { get; set; }
    }

    private static Action<ModelBuilder> Model(Action<EntityTypeBuilder<Sensor>>? extra = null)
        => mb => mb.Entity<Sensor>(e =>
        {
            e.HasNoKey();
            e.ToTable("sensors");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.Device).HasColumnName("device");
            e.Property(x => x.Value).HasColumnName("value");
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
            extra?.Invoke(e);
        });

    [Fact]
    public async Task Dropping_a_space_dimension_rebuilds_and_preserves_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        // Hypertable with a space dimension, populated.
        await DiffExecutor.ApplyAsync(cs, null, Model(e => e.HasSpacePartition(x => x.Device, 4)), ct);
        await ExecuteAsync(cs,
            """
            INSERT INTO sensors (time, device, value)
            SELECT '2026-06-01T00:00:00Z'::timestamptz + (n || ' hours')::interval, 'd' || (n % 4), n
            FROM generate_series(1, 60) AS n
            """, ct);

        // Drop the space dimension → rebuild back to a single-dimension hypertable, data preserved.
        await DiffExecutor.ApplyAsync(cs,
            Model(e => e.HasSpacePartition(x => x.Device, 4)),
            Model(),
            ct);

        Assert.Equal(60L, await ScalarAsync(cs, "SELECT count(*) FROM sensors", ct));
        Assert.Equal(1830.0d, Convert.ToDouble(await ScalarAsync(cs, "SELECT sum(value) FROM sensors", ct)));

        // Still a hypertable, now with exactly one (time) dimension.
        Assert.Equal(1L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'sensors'", ct));
        Assert.Equal(1L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.dimensions WHERE hypertable_name = 'sensors'", ct));
    }

    [Fact]
    public async Task Adding_a_space_dimension_to_a_populated_hypertable_keeps_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await fixture.CreateDatabaseAsync(ct);

        await DiffExecutor.ApplyAsync(cs, null, Model(), ct);
        await ExecuteAsync(cs,
            """
            INSERT INTO sensors (time, device, value)
            SELECT '2026-06-01T00:00:00Z'::timestamptz + (n || ' hours')::interval, 'd' || (n % 4), n
            FROM generate_series(1, 60) AS n
            """, ct);

        // Adding a dimension is in place (no rebuild); rows untouched, dimension count grows.
        await DiffExecutor.ApplyAsync(cs, Model(), Model(e => e.HasSpacePartition(x => x.Device, 4)), ct);

        Assert.Equal(60L, await ScalarAsync(cs, "SELECT count(*) FROM sensors", ct));
        Assert.Equal(2L, await ScalarAsync(cs,
            "SELECT count(*) FROM timescaledb_information.dimensions WHERE hypertable_name = 'sensors'", ct));
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
