using System.Net;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace LocaLoca.Api.Tests;

[Collection(PostgreSqlApiCollection.Name)]
public sealed class ReadinessContractTests
{
    private readonly PostgreSqlApiFixture _database;

    public ReadinessContractTests(PostgreSqlApiFixture database) => _database = database;

    [Fact]
    public async Task Ready_Distinguishes_Database_Connectivity_From_Missing_Migration_021()
    {
        await using (var connection = new NpgsqlConnection(_database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand("DROP INDEX public.ix_users_login_prefix", connection);
            await drop.ExecuteNonQueryAsync();
        }

        try
        {
            using var factory = _database.CreateFactory();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/ready");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var health = document.RootElement;

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(health.GetProperty("apiConfigured").GetBoolean());
            Assert.True(health.GetProperty("databaseConnected").GetBoolean());
            Assert.False(health.GetProperty("schemaReady").GetBoolean());
            Assert.True(health.GetProperty("runtimePermissionsReady").GetBoolean());
            Assert.Contains("021", health.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync();
            await using var restore = new NpgsqlCommand(
                "CREATE INDEX ix_users_login_prefix ON public.users (lower(login) text_pattern_ops)",
                connection);
            await restore.ExecuteNonQueryAsync();
        }
    }
}
