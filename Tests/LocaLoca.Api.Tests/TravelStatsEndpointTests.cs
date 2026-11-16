using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace LocaLoca.Api.Tests;

[Collection(PostgreSqlApiCollection.Name)]
public sealed class TravelStatsEndpointTests
{
    private readonly PostgreSqlApiFixture _database;
    public TravelStatsEndpointTests(PostgreSqlApiFixture database) => _database = database;

    [Fact]
    public async Task Annual_Stats_Count_Repeat_Visits_Keep_Personal_History_And_Isolate_Users()
    {
        using var anonymousFactory = _database.CreateFactory();
        using var anonymousClient = anonymousFactory.CreateClient();
        using var anonymousResponse = await anonymousClient.GetAsync("/api/me/travel-stats?year=2025");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();
        await RegisterAndAuthenticateAsync(client, "annual-owner-");

        await SaveCatalogVisitAsync(client, _database.AttractionId, "2025-01-15");
        await SaveCatalogVisitAsync(client, _database.AttractionId, "2025-12-31");
        await SaveCatalogVisitAsync(client, _database.AttractionId, "2024-12-31");
        await SaveCatalogVisitAsync(client, _database.AttractionId, "2026-01-01");

        using var placeResponse = await client.PostAsJsonAsync("/api/me/map-places", new
        {
            name = "Annual personal place", latitude = 59.3293, longitude = 18.0686,
            status = "visited", visitDate = "2025-03-10", rating = 5,
            impressions = "First visit", notes = "Legacy create flow"
        });
        Assert.Equal(HttpStatusCode.OK, placeResponse.StatusCode);
        using var placeJson = JsonDocument.Parse(await placeResponse.Content.ReadAsStringAsync());
        var placeId = placeJson.RootElement.GetProperty("id").GetInt32();

        using var repeat = await client.PostAsJsonAsync($"/api/me/map-places/{placeId}/visits", new
        {
            visitDate = "2025-03-20", rating = 4, impressions = "Returned", notes = "Second visit"
        });
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);

        using var statsResponse = await client.GetAsync("/api/me/travel-stats?year=2025");
        Assert.Equal(HttpStatusCode.OK, statsResponse.StatusCode);
        using var stats = JsonDocument.Parse(await statsResponse.Content.ReadAsStringAsync());
        var root = stats.RootElement;
        Assert.Equal(2025, root.GetProperty("year").GetInt32());
        Assert.Equal(4, root.GetProperty("visitCount").GetInt64());
        Assert.Equal(2, root.GetProperty("uniquePlaces").GetInt64());
        Assert.Equal(1, root.GetProperty("cities").GetInt64());
        Assert.Equal(1, root.GetProperty("countries").GetInt64());
        Assert.Equal(2, root.GetProperty("personalVisits").GetInt64());

        var months = root.GetProperty("months").EnumerateArray().ToArray();
        Assert.Equal(12, months.Length);
        Assert.Equal(1, months.Single(x => x.GetProperty("month").GetInt32() == 1).GetProperty("visitCount").GetInt64());
        Assert.Equal(2, months.Single(x => x.GetProperty("month").GetInt32() == 3).GetProperty("visitCount").GetInt64());
        Assert.Equal(1, months.Single(x => x.GetProperty("month").GetInt32() == 12).GetProperty("visitCount").GetInt64());
        Assert.Equal(0, months.Single(x => x.GetProperty("month").GetInt32() == 2).GetProperty("visitCount").GetInt64());

        var categories = root.GetProperty("categories").EnumerateArray().ToArray();
        Assert.Equal(2, categories.Single(x => x.GetProperty("name").GetString() == "API test category")
            .GetProperty("visitCount").GetInt64());
        Assert.Equal(2, categories.Single(x => x.GetProperty("name").GetString() == "Личные точки")
            .GetProperty("visitCount").GetInt64());

        // Deleting the point must not delete its event snapshots.
        using var deletePlace = await client.DeleteAsync($"/api/me/map-places/{placeId}");
        Assert.Equal(HttpStatusCode.OK, deletePlace.StatusCode);
        using var afterDeleteResponse = await client.GetAsync("/api/me/travel-stats?year=2025");
        Assert.Equal(HttpStatusCode.OK, afterDeleteResponse.StatusCode);
        using var afterDelete = JsonDocument.Parse(await afterDeleteResponse.Content.ReadAsStringAsync());
        Assert.Equal(4, afterDelete.RootElement.GetProperty("visitCount").GetInt64());
        Assert.Equal(2, afterDelete.RootElement.GetProperty("uniquePlaces").GetInt64());
        Assert.Equal(2, afterDelete.RootElement.GetProperty("personalVisits").GetInt64());

        using var otherFactory = _database.CreateFactory();
        using var otherClient = otherFactory.CreateClient();
        await RegisterAndAuthenticateAsync(otherClient, "annual-other-");
        using var otherStatsResponse = await otherClient.GetAsync("/api/me/travel-stats?year=2025");
        Assert.Equal(HttpStatusCode.OK, otherStatsResponse.StatusCode);
        using var otherStats = JsonDocument.Parse(await otherStatsResponse.Content.ReadAsStringAsync());
        Assert.Equal(0, otherStats.RootElement.GetProperty("visitCount").GetInt64());
        Assert.Equal(0, otherStats.RootElement.GetProperty("uniquePlaces").GetInt64());
    }

    [Fact]
    public async Task Annual_Stats_Reject_Invalid_Years_And_Repeat_Visit_Requests()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();
        await RegisterAndAuthenticateAsync(client, "annual-validation-");

        using var invalidYear = await client.GetAsync("/api/me/travel-stats?year=1800");
        Assert.Equal(HttpStatusCode.BadRequest, invalidYear.StatusCode);

        using var invalidPlace = await client.PostAsJsonAsync("/api/me/map-places/999999/visits", new
        {
            visitDate = "2025-03-20", rating = 4, impressions = "", notes = ""
        });
        Assert.Equal(HttpStatusCode.NotFound, invalidPlace.StatusCode);

        using var invalidRating = await client.PostAsJsonAsync("/api/me/map-places/999999/visits", new
        {
            visitDate = "2025-03-20", rating = 6, impressions = "", notes = ""
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidRating.StatusCode);
    }

    private static async Task RegisterAndAuthenticateAsync(HttpClient client, string prefix)
    {
        var login = prefix + Guid.NewGuid().ToString("N")[..12];
        const string password = "Annual-test-password-2026";
        using var registration = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login, password, displayName = "Annual stats test"
        });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        using var authentication = await client.PostAsJsonAsync("/api/auth/login", new { login, password });
        Assert.Equal(HttpStatusCode.OK, authentication.StatusCode);
        using var authJson = JsonDocument.Parse(await authentication.Content.ReadAsStringAsync());
        var token = authJson.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task SaveCatalogVisitAsync(HttpClient client, int attractionId, string date)
    {
        using var response = await client.PostAsJsonAsync("/api/me/visits", new
        {
            attractionId, visitDate = date, rating = 5, impressions = "", notes = ""
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
