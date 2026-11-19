using System.Text.Json;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace LocaLoca.Database.Tests;

public sealed class FreshSchemaV20Tests
{
    [Fact]
    public async Task Fresh_V020_Baseline_With_Current_Migrations_Produces_V024_Runtime_Schema()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("localoca_fresh_v24_tests")
            .WithUsername("localoca_test")
            .WithPassword(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)))
            .Build();
        await postgres.StartAsync();

        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();

        await using (var createSupabaseRoles = new NpgsqlCommand(@"
            CREATE ROLE postgres NOLOGIN SUPERUSER;
            CREATE ROLE anon NOLOGIN;
            CREATE ROLE authenticated NOLOGIN;
            SET ROLE postgres;",
            connection))
        {
            await createSupabaseRoles.ExecuteNonQueryAsync();
        }

        // The production permission bootstrap is defined for objects owned by
        // Supabase's postgres role, so create the fresh schema under that role.
        await ExecuteFileAsync(connection, "schema", "02_schema_v20.sql");

        // The immutable baseline is v020; apply every post-baseline migration before
        // granting runtime permissions or validating the deployable schema.
        await ExecuteFileAsync(connection, "migrations", "21_user_login_prefix_search.sql");
        await ExecuteFileAsync(connection, "migrations", "22_profile_birthday_visibility_and_remove_contacts.sql");
        await ExecuteFileAsync(connection, "migrations", "23_attraction_name_unique_per_city.sql");

        // Exercise the real app-role bootstrap instead of duplicating its flags
        // and grants in test-only SQL. The password exists only in this container.
        var appRoleScriptPath = Path.Combine(
            AppContext.BaseDirectory, "Database", "security", "05_create_app_role.sql");
        var appRoleScript = await File.ReadAllTextAsync(appRoleScriptPath);
        var testPassword = Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        appRoleScript = appRoleScript.Replace(
            "REPLACE_WITH_A_LONG_RANDOM_PASSWORD", testPassword, StringComparison.Ordinal);
        await using (var createAppRole = new NpgsqlCommand(appRoleScript, connection))
        {
            await createAppRole.ExecuteNonQueryAsync();
        }

        // Supabase supplies this event-trigger function at the platform level.
        // A no-op stand-in lets plain PostgreSQL exercise the unchanged REVOKE.
        await using (var createSupabaseFunction = new NpgsqlCommand(@"
            CREATE FUNCTION public.rls_auto_enable()
            RETURNS event_trigger
            LANGUAGE plpgsql
            AS $$ BEGIN RETURN; END; $$;",
            connection))
        {
            await createSupabaseFunction.ExecuteNonQueryAsync();
        }

        await ExecuteFileAsync(connection, "security", "06_update_runtime_permissions.sql");
        // Migration 024 grants the external-identity tables to the already-created API role.
        await ExecuteFileAsync(connection, "migrations", "24_google_external_identities_and_oauth_challenges.sql");

        // The manifest lists structural migrations only. Ledger version 1 is the
        // separate initial_schema marker and is validated independently.
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "manifest.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var expectedMigrationVersions = manifest.RootElement
            .GetProperty("schemaMigrations")
            .EnumerateArray()
            .Select(migration => migration.GetProperty("version").GetInt32())
            .ToArray();

        Assert.DoesNotContain(1, expectedMigrationVersions);

        var actualMigrationVersions = new List<int>();
        await using (var ledgerVersions = new NpgsqlCommand(
            "SELECT version FROM public.localoca_schema_migrations WHERE version <> 1 ORDER BY version",
            connection))
        await using (var reader = await ledgerVersions.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                actualMigrationVersions.Add(reader.GetInt32(0));
            }
        }

        Assert.Equal(expectedMigrationVersions, actualMigrationVersions);

        await using var ledgerCountCheck = new NpgsqlCommand(@"
            SELECT count(*) = @expectedLedgerCount
                AND count(*) FILTER (
                    WHERE version = 1
                      AND name = 'initial_schema'
                      AND source = 'baseline') = 1
            FROM public.localoca_schema_migrations",
            connection);
        ledgerCountCheck.Parameters.AddWithValue("expectedLedgerCount", expectedMigrationVersions.Length + 1);
        Assert.True((bool)(await ledgerCountCheck.ExecuteScalarAsync() ?? false),
            "The fresh-install ledger must contain every manifest migration plus one initial_schema marker.");

        await using var schemaCheck = new NpgsqlCommand(@"
            SELECT
                current_user = 'postgres'
                AND (SELECT rolsuper AND NOT rolcanlogin
                     FROM pg_roles WHERE rolname = 'postgres')
                AND (SELECT count(*) = 18 FROM pg_policies
                     WHERE schemaname = 'public'
                       AND policyname IN ('localoca_app_runtime_access', 'localoca_app_catalog_read'))
                AND EXISTS (SELECT 1 FROM information_schema.columns
                            WHERE table_schema = 'public' AND table_name = 'users'
                              AND column_name = 'date_of_birth')
                AND EXISTS (SELECT 1 FROM information_schema.columns
                            WHERE table_schema = 'public' AND table_name = 'user_profile_visibility'
                              AND column_name = 'date_of_birth_visibility'
                              AND column_default LIKE '%private%')
                AND EXISTS (SELECT 1 FROM information_schema.columns
                            WHERE table_schema = 'public' AND table_name = 'attractions'
                              AND column_name = 'min_recommended_age')
                AND to_regclass('public.birthday_contacts') IS NULL
                AND to_regclass('public.user_profile_visibility') IS NOT NULL
                AND to_regclass('public.user_friendships') IS NOT NULL
                AND to_regclass('public.user_blocks') IS NOT NULL
                AND to_regclass('public.user_external_identities') IS NOT NULL
                AND to_regclass('public.google_oauth_challenges') IS NOT NULL
                AND has_table_privilege('localoca_app', 'public.user_external_identities', 'SELECT')
                AND has_table_privilege('localoca_app', 'public.google_oauth_challenges', 'SELECT')
                AND NOT has_table_privilege('anon', 'public.user_external_identities', 'SELECT')
                AND NOT has_table_privilege('authenticated', 'public.google_oauth_challenges', 'SELECT')
                AND to_regclass('public.ix_attractions_name_trgm') IS NOT NULL
                AND EXISTS (SELECT 1 FROM pg_constraint
                            WHERE conrelid = 'public.attractions'::regclass
                              AND conname = 'uq_attractions_city_name'
                              AND contype = 'u')
                AND to_regclass('public.ix_cities_name_trgm') IS NOT NULL
                AND to_regclass('public.ix_countries_name_trgm') IS NOT NULL
                AND to_regclass('public.ix_user_friendships_requested_by_status') IS NOT NULL
                AND EXISTS (SELECT 1 FROM pg_proc
                            WHERE oid = 'public.create_default_user_profile_visibility()'::regprocedure
                              AND proconfig @> ARRAY['search_path=pg_catalog, public'])
                AND has_column_privilege('localoca_app', 'public.users', 'password_hash', 'UPDATE')
                AND NOT has_sequence_privilege('localoca_app', 'public.countries_country_id_seq', 'USAGE')
                AND NOT has_table_privilege('anon', 'public.users', 'SELECT')
                AND NOT has_table_privilege('authenticated', 'public.users', 'SELECT')
                AND NOT has_table_privilege('localoca_app', 'public.localoca_schema_migrations', 'SELECT')
                AND (SELECT rolcanlogin
                            AND NOT (rolsuper OR rolcreatedb OR rolcreaterole
                                     OR rolinherit OR rolreplication OR rolbypassrls)
                     FROM pg_roles WHERE rolname = 'localoca_app')
                AND NOT pg_has_role('localoca_app', 'postgres', 'MEMBER')
                AND EXISTS (SELECT 1 FROM pg_proc
                            WHERE oid = 'public.rls_auto_enable()'::regprocedure)
                AND NOT has_function_privilege('anon', 'public.rls_auto_enable()', 'EXECUTE')
                AND NOT has_function_privilege('authenticated', 'public.rls_auto_enable()', 'EXECUTE')
                AND NOT has_function_privilege('localoca_app', 'public.rls_auto_enable()', 'EXECUTE')",
            connection);

        Assert.True((bool)(await schemaCheck.ExecuteScalarAsync() ?? false));

        // The same attraction name is forbidden twice in one city, but may be
        // reused by a different city (for example, multiple city museums).
        int uniquenessCountryId;
        await using (var insertCountry = new NpgsqlCommand(
            "INSERT INTO public.countries(name) VALUES ('Uniqueness test country') RETURNING country_id",
            connection))
        {
            uniquenessCountryId = Convert.ToInt32(await insertCountry.ExecuteScalarAsync());
        }

        int firstCityId;
        int secondCityId;
        await using (var insertCities = new NpgsqlCommand(@"
            INSERT INTO public.cities(country_id, name)
            VALUES (@countryId, 'First test city') RETURNING city_id", connection))
        {
            insertCities.Parameters.AddWithValue("countryId", uniquenessCountryId);
            firstCityId = Convert.ToInt32(await insertCities.ExecuteScalarAsync());
        }
        await using (var insertSecondCity = new NpgsqlCommand(@"
            INSERT INTO public.cities(country_id, name)
            VALUES (@countryId, 'Second test city') RETURNING city_id", connection))
        {
            insertSecondCity.Parameters.AddWithValue("countryId", uniquenessCountryId);
            secondCityId = Convert.ToInt32(await insertSecondCity.ExecuteScalarAsync());
        }

        int categoryId;
        await using (var insertCategory = new NpgsqlCommand(
            "INSERT INTO public.categories(name) VALUES ('Uniqueness test category') RETURNING category_id",
            connection))
        {
            categoryId = Convert.ToInt32(await insertCategory.ExecuteScalarAsync());
        }

        const string insertAttractionSql = @"
            INSERT INTO public.attractions(name, description, latitude, longitude, category_id, city_id)
            VALUES ('Same attraction name', 'Test description', 0, 0, @categoryId, @cityId)";
        await using (var insertFirstAttraction = new NpgsqlCommand(insertAttractionSql, connection))
        {
            insertFirstAttraction.Parameters.AddWithValue("categoryId", categoryId);
            insertFirstAttraction.Parameters.AddWithValue("cityId", firstCityId);
            await insertFirstAttraction.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var duplicateAttraction = new NpgsqlCommand(insertAttractionSql, connection);
            duplicateAttraction.Parameters.AddWithValue("categoryId", categoryId);
            duplicateAttraction.Parameters.AddWithValue("cityId", firstCityId);
            await duplicateAttraction.ExecuteNonQueryAsync();
        });

        await using (var sameNameInAnotherCity = new NpgsqlCommand(insertAttractionSql, connection))
        {
            sameNameInAnotherCity.Parameters.AddWithValue("categoryId", categoryId);
            sameNameInAnotherCity.Parameters.AddWithValue("cityId", secondCityId);
            await sameNameInAnotherCity.ExecuteNonQueryAsync();
        }

        // Verify the effective default table and sequence ACLs for objects
        // subsequently created by the postgres owner.
        await using (var createDefaultPrivilegeProbes = new NpgsqlCommand(@"
            CREATE TABLE public.runtime_default_privilege_probe (id integer);
            CREATE SEQUENCE public.runtime_default_privilege_probe_seq;",
            connection))
        {
            await createDefaultPrivilegeProbes.ExecuteNonQueryAsync();
        }

        await using var defaultPrivilegesCheck = new NpgsqlCommand(@"
            SELECT
                NOT has_table_privilege('anon', 'public.runtime_default_privilege_probe', 'SELECT')
                AND NOT has_table_privilege('authenticated', 'public.runtime_default_privilege_probe', 'SELECT')
                AND NOT has_table_privilege('localoca_app', 'public.runtime_default_privilege_probe', 'SELECT')
                AND NOT has_sequence_privilege('anon', 'public.runtime_default_privilege_probe_seq', 'USAGE')
                AND NOT has_sequence_privilege('authenticated', 'public.runtime_default_privilege_probe_seq', 'USAGE')
                AND NOT has_sequence_privilege('localoca_app', 'public.runtime_default_privilege_probe_seq', 'USAGE')",
            connection);
        Assert.True((bool)(await defaultPrivilegesCheck.ExecuteScalarAsync() ?? false));

        await using var insertUser = new NpgsqlCommand(@"
            INSERT INTO public.users(login, password_hash, display_name)
            VALUES ('fresh-v20-trigger-test', 'not-a-real-password-hash', 'Fresh baseline test')
            RETURNING user_id", connection);
        var userId = Convert.ToInt32(await insertUser.ExecuteScalarAsync());

        await using var triggerCheck = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM public.user_profile_visibility WHERE user_id = @userId)",
            connection);
        triggerCheck.Parameters.AddWithValue("userId", userId);
        Assert.True((bool)(await triggerCheck.ExecuteScalarAsync() ?? false));
    }

    private static async Task ExecuteFileAsync(NpgsqlConnection connection, params string[] relativeParts)
    {
        var path = Path.Combine(new[] { AppContext.BaseDirectory, "Database" }.Concat(relativeParts).ToArray());
        var sql = await File.ReadAllTextAsync(path);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
