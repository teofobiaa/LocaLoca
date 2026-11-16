using Npgsql;
using Xunit;

namespace LocaLoca.Database.Tests;

[Collection(PostgreSqlDatabaseCollection.Name)]
public sealed class RuntimeRoleBootstrapTests
{
    private readonly PostgreSqlDatabaseFixture _database;

    public RuntimeRoleBootstrapTests(PostgreSqlDatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Fresh_V013_Baseline_Seed_And_Runtime_Role_Grant_Avatar_Permissions()
    {
        await using var connection = await _database.OpenConnectionAsync();

        await using (var baselineCheck = new NpgsqlCommand(@"
            SELECT to_regclass('public.user_avatars') IS NOT NULL
               AND EXISTS (
                   SELECT 1
                   FROM public.localoca_schema_migrations
                   WHERE version = 13 AND name = 'user_avatars'
               )", connection))
        {
            Assert.True((bool)(await baselineCheck.ExecuteScalarAsync() ?? false));
        }

        // Reproduce the documented new-database order: schema baseline 013, seed, then role 05.
        await _database.ExecuteSqlFileAsync("seed", "02_seed.sql");

        // Keep this test independent of execution order and leave the shared test database unchanged.
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var resetRole = new NpgsqlCommand(@"
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'localoca_app') THEN
                    EXECUTE 'DROP OWNED BY localoca_app';
                    EXECUTE 'DROP ROLE localoca_app';
                END IF;
            END
            $$;", connection, transaction))
        {
            await resetRole.ExecuteNonQueryAsync();
        }

        var scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "security",
            "05_create_app_role.sql");
        var script = await File.ReadAllTextAsync(scriptPath);

        await using (var createRole = new NpgsqlCommand(script, connection, transaction))
        {
            await createRole.ExecuteNonQueryAsync();
        }

        await using (var permissionCheck = new NpgsqlCommand(@"
            SELECT
                has_table_privilege('localoca_app', 'public.user_avatars', 'SELECT')
                AND has_table_privilege('localoca_app', 'public.user_avatars', 'INSERT')
                AND has_table_privilege('localoca_app', 'public.user_avatars', 'UPDATE')
                AND has_table_privilege('localoca_app', 'public.user_avatars', 'DELETE')",
            connection,
            transaction))
        {
            Assert.True((bool)(await permissionCheck.ExecuteScalarAsync() ?? false));
        }

        await transaction.RollbackAsync();
    }
}
