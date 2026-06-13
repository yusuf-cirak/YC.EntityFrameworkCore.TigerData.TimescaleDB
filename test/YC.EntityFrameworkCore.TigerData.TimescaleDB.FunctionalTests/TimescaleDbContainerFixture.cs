using Npgsql;
using Testcontainers.PostgreSql;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests;

/// <summary>
///     Shared TimescaleDB container; each test gets its own database for isolation.
/// </summary>
public sealed class TimescaleDbContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("timescale/timescaledb:latest-pg17").Build();

    private int _databaseCounter;

    public string ConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    /// <summary>Creates a fresh database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"test_{Interlocked.Increment(ref _databaseCounter)}";

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {name}";
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
        => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class TimescaleDbCollection : ICollectionFixture<TimescaleDbContainerFixture>
{
    public const string Name = "TimescaleDb";
}
