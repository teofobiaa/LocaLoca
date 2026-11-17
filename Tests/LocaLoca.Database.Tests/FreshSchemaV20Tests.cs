using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace LocaLoca.Database.Tests;

public sealed class FreshSchemaV20Tests
{
    [Fact]
    public async Task Fresh_V020_Baseline_And_Runtime_Bootstrap_Produce_Expected_Schema()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("localoca_fresh_v20_tests")
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

        await using var schemaCheck = new NpgsqlCommand(@"
            SELECT
                current_user = 'postgres'
                AND (SELECT rolsuper AND NOT rolcanlogin
                     FROM pg_roles WHERE rolname = 'postgres')
                AND (SELECT count(*) = 16 FROM public.localoca_schema_migrations)
                AND (SELECT count(*) = 17 FROM pg_policies
                     WHERE schemaname = 'public'
                       AND policyname IN ('localoca_app_runtime_access', 'localoca_app_catalog_read'))
                AND EXISTS (SELECT 1 FROM information_schema.columns
                            WHERE table_schema = 'public' AND table_name = 'users'
                              AND column_name = 'date_of_birth')
                AND EXISTS (SELECT 1 FROM information_schema.columns
                            WHERE table_schema = 'public' AND table_name = 'attractions'
                              AND column_name = 'min_recommended_age')
                AND to_regclass('public.birthday_contacts') IS NOT NULL
                AND to_regclass('public.user_profile_visibility') IS NOT NULL
                AND to_regclass('public.user_friendships') IS NOT NULL
                AND to_regclass('public.user_blocks') IS NOT NULL
                AND to_regclass('public.ix_attractions_name_trgm') IS NOT NULL
                AND to_regclass('public.ix_cities_name_trgm') IS NOT NULL
                AND to_regclass('public.ix_countries_name_trgm') IS NOT NULL
                AND to_regclass('public.ix_user_friendships_requested_by_status') IS NOT NULL
                AND EXISTS (SELECT 1 FROM pg_proc
                            WHERE oid = 'public.create_default_user_profile_visibility()'::regprocedure
                              AND proconfig @> ARRAY['search_path=pg_catalog, public'])
                AND has_table_privilege('localoca_app', 'public.birthday_contacts', 'SELECT')
                AND has_column_privilege('localoca_app', 'public.users', 'password_hash', 'UPDATE')
                AND has_sequence_privilege('localoca_app', 'public.birthday_contacts_birthday_contact_id_seq', 'USAGE')
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
                            WHERE oid = 'public.rls_auto_enable()'::regprocedure)",
            connection);

        Assert.True((bool)(await schemaCheck.ExecuteScalarAsync() ?? false));

        // Prove that the ALTER DEFAULT PRIVILEGES statements in the bootstrap
        // script apply to objects subsequently created by the postgres owner.
        await using (var createDefaultPrivilegeProbes = new NpgsqlCommand(@"
            CREATE TABLE public.runtime_default_privilege_probe (id integer);
            CREATE SEQUENCE public.runtime_default_privilege_probe_seq;
            CREATE FUNCTION public.runtime_default_privilege_probe_fn()
                RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;",
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
                AND NOT has_sequence_privilege('localoca_app', 'public.runtime_default_privilege_probe_seq', 'USAGE')
                AND NOT has_function_privilege('anon', 'public.runtime_default_privilege_probe_fn()', 'EXECUTE')
                AND NOT has_function_privilege('authenticated', 'public.runtime_default_privilege_probe_fn()', 'EXECUTE')
                AND NOT has_function_privilege('localoca_app', 'public.runtime_default_privilege_probe_fn()', 'EXECUTE')",
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
