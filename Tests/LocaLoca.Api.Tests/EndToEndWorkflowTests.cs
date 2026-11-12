using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace LocaLoca.Api.Tests;

[Collection(PostgreSqlApiCollection.Name)]
public sealed class EndToEndWorkflowTests
{
    private readonly PostgreSqlApiFixture _database;

    public EndToEndWorkflowTests(PostgreSqlApiFixture database) => _database = database;

    [Fact]
    public async Task User_Can_Register_Login_Load_Catalog_Save_Favorite_And_Sync_Map_Photo()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();
        var login = "e2e-" + Guid.NewGuid().ToString("N")[..12];
        const string password = "E2e-test-password-2026";

        using var registration = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login,
            password,
            displayName = "E2E test user"
        });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);

        using var authentication = await client.PostAsJsonAsync("/api/auth/login", new { login, password });
        Assert.Equal(HttpStatusCode.OK, authentication.StatusCode);
        using var authJson = JsonDocument.Parse(await authentication.Content.ReadAsStringAsync());
        var token = authJson.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var catalog = await client.GetAsync("/api/catalog");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        using var catalogJson = JsonDocument.Parse(await catalog.Content.ReadAsStringAsync());
        var attraction = catalogJson.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "API test attraction");
        var attractionId = attraction.GetProperty("id").GetInt32();

        using var favorite = await client.PostAsync($"/api/me/wishlist/{attractionId}", content: null);
        Assert.Equal(HttpStatusCode.OK, favorite.StatusCode);
        using var favorites = await client.GetAsync("/api/me/wishlist");
        Assert.Equal(HttpStatusCode.OK, favorites.StatusCode);
        using var favoritesJson = JsonDocument.Parse(await favorites.Content.ReadAsStringAsync());
        Assert.Contains(favoritesJson.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetInt32() == attractionId);

        using var placeResponse = await client.PostAsJsonAsync("/api/me/map-places", new
        {
            name = "E2E photo destination",
            latitude = 59.3293,
            longitude = 18.0686,
            status = "planned",
            visitDate = (string?)null,
            rating = (int?)null,
            impressions = "",
            notes = "Created by the end-to-end workflow"
        });
        Assert.Equal(HttpStatusCode.OK, placeResponse.StatusCode);
        using var placeJson = JsonDocument.Parse(await placeResponse.Content.ReadAsStringAsync());
        var placeId = placeJson.RootElement.GetProperty("id").GetInt32();

        byte[] imageBytes = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIeJBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAABgf/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwCQgKADv//Z");
        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(imageBytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(image, "photos", "e2e-photo.jpg");
        using var upload = await client.PostAsync($"/api/me/map-places/{placeId}/photos", form);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        using var photoList = await client.GetAsync($"/api/me/map-places/{placeId}/photos");
        Assert.Equal(HttpStatusCode.OK, photoList.StatusCode);
        using var photoJson = JsonDocument.Parse(await photoList.Content.ReadAsStringAsync());
        var photoId = photoJson.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("id").GetInt32();

        using var downloadedPhoto = await client.GetAsync($"/api/me/map-place-photos/{photoId}");
        Assert.Equal(HttpStatusCode.OK, downloadedPhoto.StatusCode);
        Assert.Equal("image/jpeg", downloadedPhoto.Content.Headers.ContentType?.MediaType);
        Assert.Equal(imageBytes, await downloadedPhoto.Content.ReadAsByteArrayAsync());

        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var verify = new NpgsqlCommand(@"
            SELECT
                (SELECT count(*) FROM wishlist WHERE user_id = @user AND attraction_id = @attraction),
                (SELECT count(*) FROM user_map_places WHERE user_map_place_id = @place AND user_id = @user),
                (SELECT count(*) FROM user_map_place_photos p
                 JOIN user_map_places m ON m.user_map_place_id = p.user_map_place_id
                 WHERE p.photo_id = @photo AND m.user_id = @user AND p.image_data = @image)", connection);
        verify.Parameters.AddWithValue("user", authJson.RootElement.GetProperty("user").GetProperty("id").GetInt32());
        verify.Parameters.AddWithValue("attraction", attractionId);
        verify.Parameters.AddWithValue("place", placeId);
        verify.Parameters.AddWithValue("photo", photoId);
        verify.Parameters.AddWithValue("image", imageBytes);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }
}
