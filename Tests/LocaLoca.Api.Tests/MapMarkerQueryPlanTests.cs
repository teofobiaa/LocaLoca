using System.Text.Json;
using LocaLoca.Api.Endpoints;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using Xunit.Abstractions;

namespace LocaLoca.Api.Tests;

[Collection(PostgreSqlApiCollection.Name)]
public sealed class MapMarkerQueryPlanTests
{
    private const int SyntheticAttractionCount = 50_000;
    private const int SyntheticPersonalPlaceCount = 10_000;

    private readonly PostgreSqlApiFixture _database;
    private readonly ITestOutputHelper _output;

    public MapMarkerQueryPlanTests(PostgreSqlApiFixture database, ITestOutputHelper output)
    {
        _database = database;
        _output = output;
    }

    [Fact]
    public async Task Viewport_Count_And_Page_Queries_Use_The_Expected_Indexes()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var userId = await CreateBenchmarkUserAsync(connection);
        await SeedMapBenchmarkDataAsync(connection, userId);
        await AnalyzeMapTablesAsync(connection);

        const decimal south = 10m;
        const decimal north = 15m;
        const decimal west = 20m;
        const decimal east = 30m;

        var baselineCountSql = BuildPreviousCountSql();
        var currentCountSql = MapMarkerSqlBuilder.BuildCountSql(hasBounds: true, crossesAntimeridian: false);
        var currentPageSql = MapMarkerSqlBuilder.BuildPageSql(hasBounds: true, crossesAntimeridian: false);

        var baselinePlan = await ExplainAsync(
            connection, baselineCountSql, userId, south, north, west, east,
            includeHasBounds: true, includePaging: false);
        var currentCountPlan = await ExplainAsync(
            connection, currentCountSql, userId, south, north, west, east,
            includeHasBounds: false, includePaging: false);
        var currentPagePlan = await ExplainAsync(
            connection, currentPageSql, userId, south, north, west, east,
            includeHasBounds: false, includePaging: true);

        var baselineCount = await ExecuteCountAsync(
            connection, baselineCountSql, userId, south, north, west, east, includeHasBounds: true);
        var currentCount = await ExecuteCountAsync(
            connection, currentCountSql, userId, south, north, west, east, includeHasBounds: false);
        Assert.Equal(baselineCount, currentCount);
        Assert.InRange(currentCount, 1, long.MaxValue);

        Assert.Contains("ix_attractions_coordinates", currentCountPlan.IndexNames);
        Assert.Contains("ix_user_map_places_user_coordinates", currentCountPlan.IndexNames);
        Assert.Contains("ix_visits_user_attraction", currentPagePlan.IndexNames);
        Assert.Contains("wishlist_pkey", currentPagePlan.IndexNames);

        _output.WriteLine($"Synthetic dataset: {SyntheticAttractionCount:N0} attractions, {SyntheticPersonalPlaceCount:N0} personal map places and {SyntheticAttractionCount:N0} visits for one user.");
        _output.WriteLine($"Viewport COUNT result: {currentCount:N0}.");
        _output.WriteLine($"Previous COUNT: {baselinePlan.Format()}");
        _output.WriteLine($"Rewritten COUNT: {currentCountPlan.Format()}");
        _output.WriteLine($"Rewritten page query: {currentPagePlan.Format()}");
    }

    private async Task<int> CreateBenchmarkUserAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(@"
INSERT INTO users(login, password_hash, display_name)
VALUES (@login, 'plan-test-not-a-real-credential', 'Map query plan test')
RETURNING user_id", connection);
        command.Parameters.AddWithValue("login", "map-plan-" + Guid.NewGuid().ToString("N"));
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task SeedMapBenchmarkDataAsync(NpgsqlConnection connection, int userId)
    {
        await using (var attractions = new NpgsqlCommand(@"
INSERT INTO attractions(name, description, history, latitude, longitude, category_id, city_id)
SELECT
    'Map plan attraction ' || generated.id,
    'Synthetic map query plan fixture',
    '',
    round((-90 + random() * 180)::numeric, 6),
    round((-180 + random() * 360)::numeric, 6),
    template.category_id,
    template.city_id
FROM generate_series(1, @count) AS generated(id)
CROSS JOIN (
    SELECT category_id, city_id
    FROM attractions
    WHERE attraction_id = @template
) AS template", connection))
        {
            attractions.Parameters.AddWithValue("count", SyntheticAttractionCount);
            attractions.Parameters.AddWithValue("template", _database.AttractionId);
            attractions.CommandTimeout = 60;
            await attractions.ExecuteNonQueryAsync();
        }

        await using (var personalPlaces = new NpgsqlCommand(@"
INSERT INTO user_map_places(user_id, name, latitude, longitude, status)
SELECT
    @user,
    'Map plan personal place ' || generated.id,
    round((-90 + random() * 180)::numeric, 6),
    round((-180 + random() * 360)::numeric, 6),
    CASE WHEN generated.id % 2 = 0 THEN 'planned' ELSE 'visited' END
FROM generate_series(1, @count) AS generated(id)", connection))
        {
            personalPlaces.Parameters.AddWithValue("user", userId);
            personalPlaces.Parameters.AddWithValue("count", SyntheticPersonalPlaceCount);
            personalPlaces.CommandTimeout = 60;
            await personalPlaces.ExecuteNonQueryAsync();
        }

        await using (var visits = new NpgsqlCommand(@"
INSERT INTO visits(user_id, attraction_id, visit_date, rating)
SELECT @user, attraction_id, CURRENT_DATE, 5
FROM attractions", connection))
        {
            visits.Parameters.AddWithValue("user", userId);
            visits.CommandTimeout = 60;
            await visits.ExecuteNonQueryAsync();
        }

        await using var wishlist = new NpgsqlCommand(@"
INSERT INTO wishlist(user_id, attraction_id)
SELECT @user, attraction_id
FROM attractions
WHERE attraction_id % 20 = 0", connection);
        wishlist.Parameters.AddWithValue("user", userId);
        wishlist.CommandTimeout = 60;
        await wishlist.ExecuteNonQueryAsync();
    }

    private static async Task AnalyzeMapTablesAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(@"
ANALYZE attractions;
ANALYZE user_map_places;
ANALYZE visits;
ANALYZE wishlist;", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string BuildPreviousCountSql() => @"
WITH markers AS (
    SELECT a.attraction_id AS id, a.name, a.latitude, a.longitude,
           EXISTS(SELECT 1 FROM visits v WHERE v.user_id = @u AND v.attraction_id = a.attraction_id) AS visited,
           EXISTS(SELECT 1 FROM wishlist w WHERE w.user_id = @u AND w.attraction_id = a.attraction_id) AS wished,
           NULL::integer AS user_place_id,
           NULL::date AS visit_date,
           NULL::smallint AS rating,
           ''::text AS impressions,
           ''::text AS notes
    FROM attractions a

    UNION ALL

    SELECT m.user_map_place_id AS id, m.name, m.latitude, m.longitude,
           m.status = 'visited' AS visited,
           m.status = 'planned' AS wished,
           m.user_map_place_id AS user_place_id,
           m.visit_date,
           m.rating,
           m.impressions,
           m.notes
    FROM user_map_places m
    WHERE m.user_id = @u
)
SELECT count(*) FROM markers
WHERE (@hasBounds = FALSE OR (
    latitude BETWEEN @south AND @north
    AND (
        (@west <= @east AND longitude BETWEEN @west AND @east)
        OR
        (@west > @east AND (longitude >= @west OR longitude <= @east))
    )
))";

    private static async Task<PlanSummary> ExplainAsync(
        NpgsqlConnection connection,
        string sql,
        int userId,
        decimal south,
        decimal north,
        decimal west,
        decimal east,
        bool includeHasBounds,
        bool includePaging)
    {
        await using var command = new NpgsqlCommand(
            "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql, connection)
        {
            CommandTimeout = 60
        };
        AddParameters(command, userId, south, north, west, east, includeHasBounds);
        if (includePaging)
        {
            command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, 50);
            command.Parameters.AddWithValue("offset", NpgsqlDbType.Bigint, 0L);
        }

        var json = (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL returned an empty EXPLAIN result."));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement[0];
        var indexes = new SortedSet<string>(StringComparer.Ordinal);
        CollectIndexes(root.GetProperty("Plan"), indexes);

        return new PlanSummary(
            root.GetProperty("Planning Time").GetDouble(),
            root.GetProperty("Execution Time").GetDouble(),
            root.GetProperty("Plan").GetProperty("Shared Hit Blocks").GetInt32(),
            root.GetProperty("Plan").GetProperty("Shared Read Blocks").GetInt32(),
            indexes.ToArray());
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
        int userId,
        decimal south,
        decimal north,
        decimal west,
        decimal east,
        bool includeHasBounds)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, userId, south, north, west, east, includeHasBounds);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static void AddParameters(
        NpgsqlCommand command,
        int userId,
        decimal south,
        decimal north,
        decimal west,
        decimal east,
        bool includeHasBounds)
    {
        command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
        if (includeHasBounds)
            command.Parameters.AddWithValue("hasBounds", NpgsqlDbType.Boolean, true);

        command.Parameters.AddWithValue("south", NpgsqlDbType.Numeric, south);
        command.Parameters.AddWithValue("north", NpgsqlDbType.Numeric, north);
        command.Parameters.AddWithValue("west", NpgsqlDbType.Numeric, west);
        command.Parameters.AddWithValue("east", NpgsqlDbType.Numeric, east);
    }

    private sealed record PlanSummary(
        double PlanningMs,
        double ExecutionMs,
        int SharedHitBlocks,
        int SharedReadBlocks,
        string[] IndexNames)
    {
        public string Format() =>
            $"planning {PlanningMs:F2} ms, execution {ExecutionMs:F2} ms, buffers hit/read {SharedHitBlocks}/{SharedReadBlocks}, indexes [{string.Join(", ", IndexNames)}]";
    }
}
