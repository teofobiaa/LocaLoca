using System.Text.Json;
using LocaLoca.Api.Endpoints;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using Xunit.Abstractions;

namespace LocaLoca.Api.Tests;

[Collection(PostgreSqlApiCollection.Name)]
public sealed class CatalogSearchQueryPlanTests
{
    private const int SyntheticRowCount = 30_000;
    private readonly PostgreSqlApiFixture _database;
    private readonly ITestOutputHelper _output;

    public CatalogSearchQueryPlanTests(PostgreSqlApiFixture database, ITestOutputHelper output)
    {
        _database = database;
        _output = output;
    }

    [Fact]
    public async Task Substring_search_uses_trigram_indexes_for_all_catalog_name_fields()
    {
        var isolated = await CreateIsolatedDatabaseAsync();
        try
        {
            await using var connection = new NpgsqlConnection(isolated.ConnectionString);
            await connection.OpenAsync();
            await SeedCatalogDataAsync(connection, isolated.CategoryId);
            await AnalyzeCatalogTablesAsync(connection);

            const string search = "RareNeedleToken";
            var sql = CatalogSearchSqlBuilder.MatchingAttractionsCte + @"
SELECT count(*)
FROM public.attractions AS a
JOIN public.categories AS category ON category.category_id = a.category_id
JOIN public.cities AS city ON city.city_id = a.city_id
JOIN public.countries AS country ON country.country_id = city.country_id
WHERE (@category IS NULL OR a.category_id = @category)"
                + CatalogSearchSqlBuilder.BuildPredicate(search);
            var pattern = "%" + search + "%";
            var plan = await ExplainAsync(connection, sql, pattern);
            var attractionPlan = await ExplainAsync(
                connection,
                "SELECT attraction_id FROM public.attractions WHERE name ILIKE @pattern",
                pattern,
                forceIndex: true);
            var cityPlan = await ExplainAsync(
                connection,
                "SELECT city_id FROM public.cities WHERE name ILIKE @pattern",
                pattern,
                forceIndex: true);
            var countryPlan = await ExplainAsync(
                connection,
                "SELECT country_id FROM public.countries WHERE name ILIKE @pattern",
                pattern,
                forceIndex: true);
            var matches = await ExecuteCountAsync(connection, sql, pattern);

            _output.WriteLine($"Synthetic dataset: {SyntheticRowCount:N0} attractions, cities and countries.");
            _output.WriteLine($"Matching attraction count: {matches:N0}.");
            _output.WriteLine($"Complete catalog query: {plan.Format()}");
            _output.WriteLine($"Attraction lookup: {attractionPlan.Format()}");
            _output.WriteLine($"City lookup: {cityPlan.Format()}");
            _output.WriteLine($"Country lookup: {countryPlan.Format()}");

            Assert.Equal(3L, matches);
            Assert.Contains("ix_attractions_name_trgm", attractionPlan.IndexNames);
            Assert.Contains("ix_cities_name_trgm", cityPlan.IndexNames);
            Assert.Contains("ix_countries_name_trgm", countryPlan.IndexNames);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(isolated.DatabaseName);
        }
    }

    private async Task<IsolatedDatabase> CreateIsolatedDatabaseAsync()
    {
        var databaseName = "catalog_search_plan_" + Guid.NewGuid().ToString("N");
        var adminConnectionString = new NpgsqlConnectionStringBuilder(_database.ConnectionString)
        {
            Database = "postgres",
            Pooling = false
        }.ConnectionString;

        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var createDatabase = new NpgsqlCommand($"CREATE DATABASE {databaseName}", admin);
            await createDatabase.ExecuteNonQueryAsync();
        }

        var isolatedConnectionString = new NpgsqlConnectionStringBuilder(_database.ConnectionString)
        {
            Database = databaseName,
            Pooling = false
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(isolatedConnectionString);
        await connection.OpenAsync();
        await ExecuteSqlFileAsync(connection, "schema", "01_schema.sql");
        await using (var extensionSchema = new NpgsqlCommand(
            "CREATE SCHEMA IF NOT EXISTS extensions", connection))
        {
            await extensionSchema.ExecuteNonQueryAsync();
        }
        await ExecuteSqlFileAsync(connection, "migrations", "16_catalog_search_trigram_indexes.sql");

        await using var category = new NpgsqlCommand(
            "INSERT INTO categories(name) VALUES ('Catalog search plan category') RETURNING category_id",
            connection);
        var categoryId = Convert.ToInt32(await category.ExecuteScalarAsync());

        return new IsolatedDatabase(databaseName, isolatedConnectionString, categoryId);
    }

    private static async Task SeedCatalogDataAsync(NpgsqlConnection connection, int categoryId)
    {
        await using var countries = new NpgsqlCommand(@"
INSERT INTO public.countries(name)
SELECT CASE
    WHEN generated.id = 1 THEN 'RareNeedleToken Country'
    ELSE 'Catalog plan country noise ' || generated.id::text
END
FROM generate_series(1, @count) AS generated(id)", connection)
        {
            CommandTimeout = 120
        };
        countries.Parameters.AddWithValue("count", SyntheticRowCount);
        await countries.ExecuteNonQueryAsync();

        await using var cities = new NpgsqlCommand(@"
INSERT INTO public.cities(country_id, name)
SELECT country_id,
       CASE
           WHEN country_id = 2 THEN 'RareNeedleToken City'
           ELSE 'Catalog plan city noise ' || country_id::text
       END
FROM public.countries", connection)
        {
            CommandTimeout = 120
        };
        await cities.ExecuteNonQueryAsync();

        await using var attractions = new NpgsqlCommand(@"
INSERT INTO public.attractions(
    name, description, history, latitude, longitude, category_id, city_id)
SELECT CASE
           WHEN city.country_id = 3 THEN 'RareNeedleToken Attraction'
           ELSE 'Catalog plan attraction noise ' || city.country_id::text
       END,
       'Synthetic query-plan fixture',
       '',
       10,
       20,
       @category,
       city.city_id
FROM public.cities AS city", connection)
        {
            CommandTimeout = 120
        };
        attractions.Parameters.AddWithValue("category", categoryId);
        await attractions.ExecuteNonQueryAsync();
    }

    private static async Task AnalyzeCatalogTablesAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(@"
ANALYZE public.attractions;
ANALYZE public.cities;
ANALYZE public.countries;", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PlanSummary> ExplainAsync(
        NpgsqlConnection connection,
        string sql,
        string pattern,
        bool forceIndex = false)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        if (forceIndex)
        {
            await using var planner = new NpgsqlCommand(
                "SET LOCAL enable_seqscan = off",
                connection,
                transaction);
            await planner.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql,
            connection,
            transaction)
        {
            CommandTimeout = 120
        };
        command.Parameters.AddWithValue("pattern", NpgsqlDbType.Text, pattern);
        if (sql.Contains("@category", StringComparison.Ordinal))
            command.Parameters.AddWithValue("category", NpgsqlDbType.Integer, DBNull.Value);
        var json = (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL returned an empty EXPLAIN result."));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement[0];
        var indexes = new SortedSet<string>(StringComparer.Ordinal);
        CollectIndexes(root.GetProperty("Plan"), indexes);

        var summary = new PlanSummary(
            root.GetProperty("Planning Time").GetDouble(),
            root.GetProperty("Execution Time").GetDouble(),
            indexes.ToArray());
        await transaction.CommitAsync();
        return summary;
    }

    private static void CollectIndexes(JsonElement plan, ISet<string> indexes)
    {
        if (plan.TryGetProperty("Index Name", out var indexName) &&
            indexName.ValueKind == JsonValueKind.String)
        {
            indexes.Add(indexName.GetString()!);
        }

        if (!plan.TryGetProperty("Plans", out var childPlans) ||
            childPlans.ValueKind != JsonValueKind.Array)
            return;

        foreach (var child in childPlans.EnumerateArray())
            CollectIndexes(child, indexes);
    }

    private static async Task<long> ExecuteCountAsync(
        NpgsqlConnection connection,
        string sql,
        string pattern)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("pattern", NpgsqlDbType.Text, pattern);
        command.Parameters.AddWithValue("category", NpgsqlDbType.Integer, DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteSqlFileAsync(
        NpgsqlConnection connection,
        params string[] relativeParts)
    {
        var path = Path.Combine(
            new[] { AppContext.BaseDirectory, "Database" }
                .Concat(relativeParts).ToArray());
        var sql = await File.ReadAllTextAsync(path);
        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = 120
        };
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropIsolatedDatabaseAsync(string databaseName)
    {
        var adminConnectionString = new NpgsqlConnectionStringBuilder(_database.ConnectionString)
        {
            Database = "postgres",
            Pooling = false
        }.ConnectionString;

        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using (var terminate = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name",
            admin))
        {
            terminate.Parameters.AddWithValue("name", databaseName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {databaseName}", admin);
        await drop.ExecuteNonQueryAsync();
    }

    private sealed record IsolatedDatabase(
        string DatabaseName,
        string ConnectionString,
        int CategoryId);

    private sealed record PlanSummary(
        double PlanningMs,
        double ExecutionMs,
        string[] IndexNames)
    {
        public string Format() =>
            $"planning {PlanningMs:F2} ms, execution {ExecutionMs:F2} ms, indexes [{string.Join(", ", IndexNames)}]";
    }
}
