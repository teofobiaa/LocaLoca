using System.Security.Cryptography;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace LocaLoca.Database.Tests;

[CollectionDefinition("PostgreSQL database tests", DisableParallelization = true)]
public sealed class PostgreSqlDatabaseCollection : ICollectionFixture<PostgreSqlDatabaseFixture>
{
    public const string Name = "PostgreSQL database tests";
}

public sealed class PostgreSqlDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("localoca_database_tests")
        .WithUsername("localoca_test")
        .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)))
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await ExecuteSqlFileAsync("schema", "01_schema.sql");
    }

    public async Task ExecuteSqlFileAsync(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { AppContext.BaseDirectory, "Database" }.Concat(relativeParts).ToArray());
        var sql = await File.ReadAllTextAsync(path);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }
}
