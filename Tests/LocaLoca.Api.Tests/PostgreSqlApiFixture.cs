using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Security.Cryptography;
using Testcontainers.PostgreSql;
using Xunit;

namespace LocaLoca.Api.Tests;

[CollectionDefinition("PostgreSQL API integration tests", DisableParallelization = true)]
public sealed class PostgreSqlApiCollection : ICollectionFixture<PostgreSqlApiFixture>
{
    public const string Name = "PostgreSQL API integration tests";
}

public sealed class PostgreSqlApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("localoca_api_tests")
        .WithUsername("localoca_test")
        .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)))
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();
    public int AttractionId { get; private set; }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Database", "schema", "01_schema.sql");
        var schemaSql = await File.ReadAllTextAsync(schemaPath);
        await using (var schemaCommand = new NpgsqlCommand(schemaSql, connection))
        {
            await schemaCommand.ExecuteNonQueryAsync();
        }
        var sessionSql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "08_server_sessions.sql"));
        await using (var sessionCommand = new NpgsqlCommand(sessionSql, connection))
            await sessionCommand.ExecuteNonQueryAsync();

        var viewportIndexesSql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "09_map_marker_viewport_indexes.sql"));
        await using (var viewportIndexesCommand = new NpgsqlCommand(viewportIndexesSql, connection))
            await viewportIndexesCommand.ExecuteNonQueryAsync();

        var mapVisitIndexSql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "10_map_marker_visit_lookup_index.sql"));
        await using (var mapVisitIndexCommand = new NpgsqlCommand(mapVisitIndexSql, connection))
            await mapVisitIndexCommand.ExecuteNonQueryAsync();

        var residenceSql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "11_user_residence_location.sql"));
        await using (var residenceCommand = new NpgsqlCommand(residenceSql, connection))
            await residenceCommand.ExecuteNonQueryAsync();

        var rateLimitSql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "12_distributed_auth_rate_limits.sql"));
        await using (var rateLimitCommand = new NpgsqlCommand(rateLimitSql, connection))
            await rateLimitCommand.ExecuteNonQueryAsync();

        var avatarsSql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "13_user_avatars.sql"));
        await using (var avatarsCommand = new NpgsqlCommand(avatarsSql, connection))
            await avatarsCommand.ExecuteNonQueryAsync();

        await using var categoryCommand = new NpgsqlCommand(
            "INSERT INTO categories(name) VALUES ('API test category') RETURNING category_id",
            connection);
        var categoryId = Convert.ToInt32(await categoryCommand.ExecuteScalarAsync());

        await using var countryCommand = new NpgsqlCommand(
            "INSERT INTO countries(name) VALUES ('API test country') RETURNING country_id",
            connection);
        var countryId = Convert.ToInt32(await countryCommand.ExecuteScalarAsync());

        await using var cityCommand = new NpgsqlCommand(
            "INSERT INTO cities(country_id, name) VALUES (@country, 'API test city') RETURNING city_id",
            connection);
        cityCommand.Parameters.AddWithValue("country", countryId);
        var cityId = Convert.ToInt32(await cityCommand.ExecuteScalarAsync());

        await using var attractionCommand = new NpgsqlCommand(@"
            INSERT INTO attractions(name, description, history, latitude, longitude, category_id, city_id)
            VALUES ('API test attraction', 'Synthetic test fixture', '', 10, 20, @category, @city)
            RETURNING attraction_id",
            connection);
        attractionCommand.Parameters.AddWithValue("category", categoryId);
        attractionCommand.Parameters.AddWithValue("city", cityId);
        AttractionId = Convert.ToInt32(await attractionCommand.ExecuteScalarAsync());
    }

    public ApiTestFactory CreateFactory(string? sessionSecret = null, IPAddress? rateLimitClientIp = null) => new(ConnectionString, sessionSecret, rateLimitClientIp);

    // Tests in this xUnit collection are serialized, so an outage test can safely
    // stop and restart the shared isolated PostgreSQL container.
    public Task StopDatabaseAsync() => _postgres.StopAsync();

    public async Task StartDatabaseAsync()
    {
        await _postgres.StartAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        Exception? lastError = null;
        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Pooling = false,
            Timeout = 2
        }.ConnectionString;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(150));
            }
        }

        throw new TimeoutException(
            "The isolated PostgreSQL container did not accept connections after restart.",
            lastError);
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }
}

public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(),
        "LocaLoca.Api.Tests",
        Guid.NewGuid().ToString("N"));

    private readonly string _sessionSecret;

    public ApiTestFactory(string connectionString, string? sessionSecret = null, IPAddress? rateLimitClientIp = null)
    {
        ConnectionString = connectionString;
        _sessionSecret = sessionSecret ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        RateLimitClientIpForTests = rateLimitClientIp ?? CreateUniqueTestAddress();
    }

    private string ConnectionString { get; }

    public string SessionSecretForTests => _sessionSecret;
    public IPAddress RateLimitClientIpForTests { get; }

    private static IPAddress CreateUniqueTestAddress()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        bytes[0] = 0x20;
        bytes[1] = 0x01;
        bytes[2] = 0x0d;
        bytes[3] = 0xb8;
        return new IPAddress(bytes);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PORT"] = string.Empty,
                ["LOCA_DB_CONNECTION"] = ConnectionString,
                ["LOCA_SESSION_SECRET"] = _sessionSecret,
                ["LOCA_DATA_ROOT"] = _dataRoot
            });
        });
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new TestClientIpStartupFilter(RateLimitClientIpForTests)));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing || !Directory.Exists(_dataRoot))
            return;

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup must not conceal the result of the test itself.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not conceal the result of the test itself.
        }
    }
}

internal sealed class TestClientIpStartupFilter(IPAddress remoteIpAddress) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        application =>
        {
            application.Use(async (context, nextRequest) =>
            {
                context.Connection.RemoteIpAddress = remoteIpAddress;
                await nextRequest();
            });
            next(application);
        };
}
