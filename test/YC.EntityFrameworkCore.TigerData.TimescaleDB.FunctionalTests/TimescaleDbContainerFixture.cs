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

    /// <summary>
    ///     Creates a cluster-wide tablespace backed by a fresh directory inside the container, or
    ///     returns false when the directory can't be prepared (then the caller skips the assertion).
    /// </summary>
    public async Task<bool> TryCreateTablespaceAsync(string name, CancellationToken cancellationToken)
    {
        var path = $"/var/lib/postgresql/{name}";
        var mkdir = await _container.ExecAsync(["mkdir", "-p", path], cancellationToken);
        await _container.ExecAsync(["chown", "postgres:postgres", path], cancellationToken);
        if (mkdir.ExitCode != 0)
        {
            return false;
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLESPACE {name} LOCATION '{path}'";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async ValueTask DisposeAsync()
        => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class TimescaleDbCollection : ICollectionFixture<TimescaleDbContainerFixture>
{
    public const string Name = "TimescaleDb";
}
