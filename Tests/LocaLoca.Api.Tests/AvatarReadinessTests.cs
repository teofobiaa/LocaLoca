using System.Net;
using Npgsql;
using Xunit;

namespace LocaLoca.Api.Tests;

[Collection(PostgreSqlApiCollection.Name)]
public sealed class AvatarReadinessTests
{
    private readonly PostgreSqlApiFixture _database;

    public AvatarReadinessTests(PostgreSqlApiFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Ready_Returns_503_When_Avatar_Table_Is_Missing()
    {
        await using (var connection = new NpgsqlConnection(_database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var rename = new NpgsqlCommand(
                "ALTER TABLE public.user_avatars RENAME TO user_avatars_readiness_test_backup",
                connection);
            await rename.ExecuteNonQueryAsync();
        }

        try
        {
            using var factory = _database.CreateFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/ready");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("миграция 013", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("public.user_avatars", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync();
            await using var restore = new NpgsqlCommand(
                "ALTER TABLE public.user_avatars_readiness_test_backup RENAME TO user_avatars",
                connection);
            await restore.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Ready_Returns_503_When_Runtime_Role_Lacks_Avatar_Permissions()
    {
        var roleName = "readiness_" + Guid.NewGuid().ToString("N");
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

        await using (var admin = new NpgsqlConnection(_database.ConnectionString))
        {
            await admin.OpenAsync();
            var setupSql = $@"
                CREATE ROLE {roleName} LOGIN PASSWORD '{password}';
                GRANT CONNECT ON DATABASE localoca_api_tests TO {roleName};
                GRANT USAGE ON SCHEMA public TO {roleName};
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {roleName};
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {roleName};
                REVOKE ALL PRIVILEGES ON TABLE public.user_avatars FROM {roleName};";
            await using var setup = new NpgsqlCommand(setupSql, admin);
            await setup.ExecuteNonQueryAsync();
        }

        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(_database.ConnectionString)
            {
                Username = roleName,
                Password = password,
                Pooling = false
            }.ConnectionString;

            using var factory = new ApiTestFactory(connectionString);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/ready");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("runtime-роли не хватает прав", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SELECT, INSERT, UPDATE, DELETE", body, StringComparison.Ordinal);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(_database.ConnectionString);
            await admin.OpenAsync();
            await using var cleanup = new NpgsqlCommand(
                $"DROP OWNED BY {roleName}; DROP ROLE {roleName};",
                admin);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
