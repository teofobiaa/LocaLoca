using System.Security.Cryptography;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace LocaLoca.Database.Tests;

[CollectionDefinition("PostgreSQL permission drift tests", DisableParallelization = true)]
public sealed class PostgreSqlPermissionDriftCollection
    : ICollectionFixture<PostgreSqlPermissionDriftFixture>
{
    public const string Name = "PostgreSQL permission drift tests";
}

/// <summary>
/// Uses a dedicated PostgreSQL instance so permission-mutation tests never alter
/// the shared database used by the rest of the database test suite.
/// </summary>
public sealed class PostgreSqlPermissionDriftFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("postgres")
        .WithUsername("localoca_test")
        .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)))
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // The current schema baseline already contains migrations 003, 004,
        // 007-013. Apply the same reviewed runtime-role scripts used in production.
        await ExecuteSqlFileAsync("schema", "01_schema.sql");
        await ExecuteSqlFileAsync("security", "05_create_app_role.sql");
        await ExecuteSqlFileAsync("security", "06_update_runtime_permissions.sql");
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task ExecuteSqlFileAsync(params string[] relativeParts)
    {
        var path = Path.Combine(
            new[] { AppContext.BaseDirectory, "Database" }
                .Concat(relativeParts)
                .ToArray());
        var sql = await File.ReadAllTextAsync(path);

        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<string> ReadSqlFileAsync(params string[] relativeParts)
    {
        var path = Path.Combine(
            new[] { AppContext.BaseDirectory, "Database" }
                .Concat(relativeParts)
                .ToArray());
        return await File.ReadAllTextAsync(path);
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }
}

[Collection(PostgreSqlPermissionDriftCollection.Name)]
public sealed class PostgreSqlPermissionDriftTests
{
    private readonly PostgreSqlPermissionDriftFixture _database;

    public PostgreSqlPermissionDriftTests(PostgreSqlPermissionDriftFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Reviewed_Runtime_Role_Baseline_Has_No_Permission_Drift()
    {
        await using var connection = await _database.OpenConnectionAsync();

        var findings = await ReadFindingsAsync(connection, transaction: null);

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("ALTER ROLE localoca_app SUPERUSER;", "ROLE_ATTRIBUTE_DRIFT")]
    [InlineData("ALTER ROLE localoca_app CREATEDB;", "ROLE_ATTRIBUTE_DRIFT")]
    [InlineData("ALTER ROLE localoca_app CREATEROLE;", "ROLE_ATTRIBUTE_DRIFT")]
    [InlineData("ALTER ROLE localoca_app LOGIN INHERIT;", "ROLE_ATTRIBUTE_DRIFT")]
    [InlineData("ALTER ROLE localoca_app REPLICATION;", "ROLE_ATTRIBUTE_DRIFT")]
    [InlineData("ALTER ROLE localoca_app BYPASSRLS;", "ROLE_ATTRIBUTE_DRIFT")]
    [InlineData("ALTER ROLE localoca_app NOLOGIN;", "ROLE_ATTRIBUTE_DRIFT")]
    [InlineData("ALTER ROLE localoca_app CONNECTION LIMIT 3;", "ROLE_ATTRIBUTE_DRIFT")]
    [InlineData("ALTER ROLE localoca_app VALID UNTIL '2030-01-01 00:00:00+00';", "ROLE_ATTRIBUTE_DRIFT")]
    [InlineData("ALTER ROLE localoca_app SET search_path = public;", "ROLE_CONFIGURATION_DRIFT")]
    [InlineData("ALTER ROLE localoca_app SET statement_timeout = '0';", "ROLE_CONFIGURATION_DRIFT")]
    [InlineData("GRANT pg_read_all_data TO localoca_app;", "ROLE_MEMBERSHIP")]
    [InlineData("GRANT pg_read_all_data TO localoca_app WITH ADMIN OPTION;", "ROLE_MEMBERSHIP")]
    [InlineData("CREATE ROLE drift_reader NOLOGIN; GRANT localoca_app TO drift_reader;", "ROLE_MEMBERSHIP")]
    [InlineData("GRANT CREATE ON SCHEMA public TO localoca_app;", "SCHEMA_PRIVILEGE_EXTRA")]
    [InlineData("CREATE SCHEMA drift_probe; GRANT USAGE ON SCHEMA drift_probe TO localoca_app;", "SCHEMA_PRIVILEGE_EXTRA")]
    [InlineData("REVOKE USAGE ON SCHEMA public FROM localoca_app; REVOKE USAGE ON SCHEMA public FROM PUBLIC;", "SCHEMA_PRIVILEGE_MISSING")]
    [InlineData("GRANT CREATE ON SCHEMA public TO PUBLIC;", "PUBLIC_SCHEMA_PRIVILEGE")]
    [InlineData("CREATE ROLE drift_reader NOLOGIN; GRANT USAGE ON SCHEMA public TO drift_reader;", "UNAPPROVED_ROLE_PRIVILEGE")]
    [InlineData("CREATE SCHEMA drift_probe; GRANT CREATE ON SCHEMA drift_probe TO PUBLIC;", "PUBLIC_SCHEMA_PRIVILEGE")]
    [InlineData("GRANT USAGE ON SCHEMA public TO localoca_app WITH GRANT OPTION;", "SCHEMA_GRANT_OPTION")]
    [InlineData("ALTER TABLE public.users ENABLE ROW LEVEL SECURITY;", "ROW_LEVEL_SECURITY_DRIFT")]
    [InlineData("CREATE POLICY permission_drift_policy ON public.users USING (true);", "RLS_POLICY_DRIFT")]
    [InlineData("ALTER TABLE public.users RENAME TO users_drift;", "EXPECTED_OBJECT_MISSING")]
    [InlineData("ALTER SEQUENCE public.users_user_id_seq RENAME TO users_user_id_seq_drift;", "EXPECTED_OBJECT_MISSING")]
    [InlineData("GRANT DELETE ON TABLE public.categories TO localoca_app;", "TABLE_PRIVILEGE_EXTRA")]
    [InlineData("REVOKE SELECT ON TABLE public.categories FROM localoca_app;", "TABLE_PRIVILEGE_MISSING")]
    [InlineData("GRANT TRUNCATE ON TABLE public.visits TO localoca_app;", "TABLE_PRIVILEGE_EXTRA")]
    [InlineData("GRANT SELECT ON TABLE public.users TO PUBLIC;", "PUBLIC_TABLE_PRIVILEGE")]
    [InlineData("CREATE ROLE drift_reader NOLOGIN; GRANT SELECT ON TABLE public.users TO drift_reader;", "UNAPPROVED_ROLE_PRIVILEGE")]
    [InlineData("GRANT UPDATE (login) ON TABLE public.users TO localoca_app;", "COLUMN_PRIVILEGE_EXTRA")]
    [InlineData("REVOKE UPDATE (display_name) ON TABLE public.users FROM localoca_app;", "COLUMN_PRIVILEGE_MISSING")]
    [InlineData("GRANT UPDATE (login) ON TABLE public.users TO PUBLIC;", "PUBLIC_COLUMN_PRIVILEGE")]
    [InlineData("GRANT SELECT ON TABLE public.users TO localoca_app WITH GRANT OPTION;", "GRANT_OPTION")]
    [InlineData("GRANT UPDATE (display_name) ON TABLE public.users TO localoca_app WITH GRANT OPTION;", "COLUMN_GRANT_OPTION")]
    [InlineData("GRANT USAGE ON SEQUENCE public.categories_category_id_seq TO localoca_app;", "SEQUENCE_PRIVILEGE_EXTRA")]
    [InlineData("CREATE ROLE drift_reader NOLOGIN; GRANT USAGE ON SEQUENCE public.users_user_id_seq TO drift_reader;", "UNAPPROVED_ROLE_PRIVILEGE")]
    [InlineData("ALTER SEQUENCE public.users_user_id_seq OWNER TO localoca_app;", "OBJECT_OWNERSHIP")]
    [InlineData("GRANT USAGE ON SEQUENCE public.users_user_id_seq TO localoca_app WITH GRANT OPTION;", "SEQUENCE_GRANT_OPTION")]
    [InlineData("REVOKE USAGE ON SEQUENCE public.users_user_id_seq FROM localoca_app;", "SEQUENCE_PRIVILEGE_MISSING")]
    [InlineData("GRANT SELECT ON SEQUENCE public.users_user_id_seq TO PUBLIC;", "PUBLIC_SEQUENCE_PRIVILEGE")]
    [InlineData("GRANT TEMP ON DATABASE postgres TO localoca_app;", "DATABASE_PRIVILEGE_EXTRA")]
    [InlineData("GRANT CONNECT ON DATABASE postgres TO localoca_app WITH GRANT OPTION;", "DATABASE_GRANT_OPTION")]
    [InlineData("GRANT CREATE ON DATABASE postgres TO PUBLIC;", "PUBLIC_DATABASE_PRIVILEGE")]
    [InlineData("ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO localoca_app;", "DEFAULT_PRIVILEGE_DRIFT")]
    [InlineData("ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO PUBLIC;", "DEFAULT_PRIVILEGE_DRIFT")]
    [InlineData("CREATE ROLE drift_reader NOLOGIN; ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO drift_reader;", "UNAPPROVED_DEFAULT_PRIVILEGE")]
    [InlineData("ALTER TABLE public.categories OWNER TO localoca_app;", "OBJECT_OWNERSHIP")]
    [InlineData("ALTER SCHEMA public OWNER TO localoca_app;", "OBJECT_OWNERSHIP")]
    [InlineData(
        "CREATE FUNCTION public.permission_drift_probe() RETURNS integer LANGUAGE SQL AS 'SELECT 1'; GRANT EXECUTE ON FUNCTION public.permission_drift_probe() TO localoca_app;",
        "FUNCTION_PRIVILEGE_EXTRA")]
    [InlineData(
        "CREATE FUNCTION public.permission_drift_probe() RETURNS integer LANGUAGE SQL AS 'SELECT 1';",
        "PUBLIC_FUNCTION_PRIVILEGE")]
    [InlineData(
        "CREATE FUNCTION public.permission_drift_probe() RETURNS integer LANGUAGE SQL SECURITY DEFINER AS 'SELECT 1';",
        "SECURITY_DEFINER_FUNCTION")]
    [InlineData(
        "CREATE FUNCTION public.permission_drift_probe() RETURNS integer LANGUAGE SQL AS 'SELECT 1'; GRANT EXECUTE ON FUNCTION public.permission_drift_probe() TO localoca_app WITH GRANT OPTION;",
        "FUNCTION_GRANT_OPTION")]
    [InlineData(
        "CREATE FUNCTION public.permission_drift_probe() RETURNS integer LANGUAGE SQL AS 'SELECT 1'; ALTER FUNCTION public.permission_drift_probe() OWNER TO localoca_app;",
        "OBJECT_OWNERSHIP")]
    [InlineData(
        "CREATE TYPE public.permission_drift_enum AS ENUM ('x', 'y'); ALTER TYPE public.permission_drift_enum OWNER TO localoca_app;",
        "OBJECT_OWNERSHIP")]
    [InlineData(
        "CREATE ROLE drift_owner NOLOGIN; ALTER DEFAULT PRIVILEGES FOR ROLE localoca_app IN SCHEMA public GRANT SELECT ON TABLES TO drift_owner;",
        "OBJECT_OWNERSHIP")]
    [InlineData(
        "SELECT lo_create(987654321); GRANT SELECT ON LARGE OBJECT 987654321 TO localoca_app;",
        "LARGE_OBJECT_PRIVILEGE")]
    [InlineData(
        "SELECT lo_create(987654322); ALTER LARGE OBJECT 987654322 OWNER TO localoca_app;",
        "OBJECT_OWNERSHIP")]
    [InlineData("DROP OWNED BY localoca_app; DROP ROLE localoca_app;", "ROLE_MISSING")]
    public async Task Audit_Detects_Individual_Permission_Drift(
        string mutationSql,
        string expectedFindingCode)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteSqlAsync(connection, transaction, mutationSql);

        var findings = await ReadFindingsAsync(connection, transaction);

        Assert.Contains(findings, finding => finding.FindingCode == expectedFindingCode);

        // Explicit rollback makes every mutation disposable, including role,
        // schema, ACL, ownership, default-privilege and function changes.
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Audit_Reports_Multiple_Independent_Drift_Findings_In_One_Run()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteSqlAsync(connection, transaction, """
            ALTER ROLE localoca_app SUPERUSER;
            GRANT DELETE ON TABLE public.categories TO localoca_app;
            GRANT SELECT ON TABLE public.users TO PUBLIC;
            GRANT UPDATE (login) ON TABLE public.users TO localoca_app;
            GRANT USAGE ON SEQUENCE public.categories_category_id_seq TO localoca_app;
            """);

        var findings = await ReadFindingsAsync(connection, transaction);
        var codes = findings.Select(finding => finding.FindingCode).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ROLE_ATTRIBUTE_DRIFT", codes);
        Assert.Contains("TABLE_PRIVILEGE_EXTRA", codes);
        Assert.Contains("PUBLIC_TABLE_PRIVILEGE", codes);
        Assert.Contains("COLUMN_PRIVILEGE_EXTRA", codes);
        Assert.Contains("SEQUENCE_PRIVILEGE_EXTRA", codes);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Audit_Is_Read_Only_And_Does_Not_Repair_Detected_Drift()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteSqlAsync(
            connection,
            transaction,
            "GRANT DELETE ON TABLE public.categories TO localoca_app;");

        var firstRun = await ReadFindingsAsync(connection, transaction);
        var secondRun = await ReadFindingsAsync(connection, transaction);

        Assert.Contains(firstRun, finding => finding.FindingCode == "TABLE_PRIVILEGE_EXTRA");
        Assert.Contains(secondRun, finding => finding.FindingCode == "TABLE_PRIVILEGE_EXTRA");

        var stillGranted = await ExecuteScalarAsync<bool>(
            connection,
            transaction,
            "SELECT has_table_privilege('localoca_app', 'public.categories', 'DELETE');");

        Assert.True(stillGranted);

        await transaction.RollbackAsync();
    }

    private async Task<IReadOnlyList<PermissionDriftFinding>> ReadFindingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction)
    {
        var sql = await _database.ReadSqlFileAsync("audits", "check_runtime_permission_drift.sql");
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();

        var findings = new List<PermissionDriftFinding>();
        while (await reader.ReadAsync())
        {
            findings.Add(new PermissionDriftFinding(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }

        return findings;
    }

    private static async Task ExecuteSqlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T));
    }

    private sealed record PermissionDriftFinding(
        string Severity,
        string FindingCode,
        string ObjectType,
        string ObjectName,
        string Expected,
        string Actual,
        string Recommendation);
}
