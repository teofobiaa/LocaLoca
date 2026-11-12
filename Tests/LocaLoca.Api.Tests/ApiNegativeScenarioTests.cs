using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace LocaLoca.Api.Tests;

[Collection(PostgreSqlApiCollection.Name)]
public sealed class ApiNegativeScenarioTests
{
    private const int PhotoLimitBytes = 20 * 1024 * 1024;
    private readonly PostgreSqlApiFixture _database;

    public ApiNegativeScenarioTests(PostgreSqlApiFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Visits_Reject_Invalid_Updates_And_Missing_Update_Delete_Ids()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        SetBearer(client, account.Token);
        var visitId = await CreateVisitAsync(client);

        using var invalidUpdate = await client.PutAsJsonAsync($"/api/me/visits/{visitId}", new
        {
            attractionId = _database.AttractionId,
            visitDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-2)).ToString("yyyy-MM-dd"),
            rating = 0,
            impressions = "Invalid update must not be persisted",
            notes = ""
        });

        Assert.Equal(HttpStatusCode.BadRequest, invalidUpdate.StatusCode);

        using var afterInvalidUpdate = await client.GetAsync("/api/me/visits");
        var visits = await ReadItemsAsync(afterInvalidUpdate);
        var storedVisit = Assert.Single(visits);
        Assert.Equal(visitId, storedVisit.GetProperty("id").GetInt32());
        Assert.Equal(5, storedVisit.GetProperty("rating").GetInt32());

        using var missingUpdate = await client.PutAsJsonAsync($"/api/me/visits/{int.MaxValue}", new
        {
            attractionId = _database.AttractionId,
            visitDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-2)).ToString("yyyy-MM-dd"),
            rating = 4,
            impressions = "Missing visit",
            notes = ""
        });
        using var missingDelete = await client.DeleteAsync($"/api/me/visits/{int.MaxValue}");

        Assert.Equal(HttpStatusCode.NotFound, missingUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingDelete.StatusCode);
        Assert.Equal(1L, await CountVisitsAsync(account.UserId));
    }

    [Fact]
    public async Task Map_Place_Crud_Hides_Foreign_And_Missing_Ids_And_Preserves_Owner_Data()
    {
        using var factory = _database.CreateFactory();
        using var ownerClient = factory.CreateClient();
        using var otherClient = factory.CreateClient();

        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        SetBearer(ownerClient, owner.Token);
        SetBearer(otherClient, other.Token);

        var placeId = await CreateMapPlaceAsync(ownerClient);

        using var foreignUpdate = await otherClient.PutAsJsonAsync($"/api/me/map-places/{placeId}", new
        {
            name = "Foreign update attempt",
            status = "planned",
            visitDate = (string?)null,
            rating = (int?)null,
            impressions = "",
            notes = ""
        });
        using var foreignDelete = await otherClient.DeleteAsync($"/api/me/map-places/{placeId}");

        Assert.Equal(HttpStatusCode.NotFound, foreignUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);

        using var invalidOwnerUpdate = await ownerClient.PutAsJsonAsync($"/api/me/map-places/{placeId}", new
        {
            name = " ",
            status = "planned",
            visitDate = (string?)null,
            rating = (int?)null,
            impressions = "",
            notes = ""
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidOwnerUpdate.StatusCode);

        using var ownerMarkersResponse = await ownerClient.GetAsync("/api/me/map-markers");
        var ownerMarkers = await ReadItemsAsync(ownerMarkersResponse);
        Assert.Contains(ownerMarkers, marker =>
            marker.TryGetProperty("userPlaceId", out var id) &&
            id.ValueKind == JsonValueKind.Number &&
            id.GetInt32() == placeId &&
            marker.GetProperty("name").GetString() == "Negative-test map place");

        using var missingUpdate = await ownerClient.PutAsJsonAsync($"/api/me/map-places/{int.MaxValue}", new
        {
            name = "Missing place",
            status = "planned",
            visitDate = (string?)null,
            rating = (int?)null,
            impressions = "",
            notes = ""
        });
        using var missingDelete = await ownerClient.DeleteAsync($"/api/me/map-places/{int.MaxValue}");

        Assert.Equal(HttpStatusCode.NotFound, missingUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingDelete.StatusCode);
    }

    [Fact]
    public async Task Photo_Routes_Reject_Foreign_And_Missing_Parents_And_Missing_Photo_Ids()
    {
        using var factory = _database.CreateFactory();
        using var ownerClient = factory.CreateClient();
        using var otherClient = factory.CreateClient();

        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        SetBearer(ownerClient, owner.Token);
        SetBearer(otherClient, other.Token);

        var visitId = await CreateVisitAsync(ownerClient);
        var placeId = await CreateMapPlaceAsync(ownerClient);

        using (var foreignVisitForm = new MultipartFormDataContent())
        {
            AddJpeg(foreignVisitForm, "file", "foreign-visit.jpg", 3);
            using var response = await otherClient.PostAsync(
                $"/api/me/visits/{visitId}/photos", foreignVisitForm);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var missingVisitForm = new MultipartFormDataContent())
        {
            AddJpeg(missingVisitForm, "file", "missing-visit.jpg", 3);
            using var response = await ownerClient.PostAsync(
                $"/api/me/visits/{int.MaxValue}/photos", missingVisitForm);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var foreignPlaceForm = new MultipartFormDataContent())
        {
            AddJpeg(foreignPlaceForm, "photos", "foreign-place.jpg", 3);
            using var response = await otherClient.PostAsync(
                $"/api/me/map-places/{placeId}/photos", foreignPlaceForm);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var missingPlaceForm = new MultipartFormDataContent())
        {
            AddJpeg(missingPlaceForm, "photos", "missing-place.jpg", 3);
            using var response = await ownerClient.PostAsync(
                $"/api/me/map-places/{int.MaxValue}/photos", missingPlaceForm);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var missingVisitPhotoRead = await ownerClient.GetAsync($"/api/me/photos/{int.MaxValue}");
        using var missingVisitPhotoDelete = await ownerClient.DeleteAsync($"/api/me/photos/{int.MaxValue}");
        using var missingMapPhotoRead = await ownerClient.GetAsync($"/api/me/map-place-photos/{int.MaxValue}");
        using var missingMapPhotoDelete = await ownerClient.DeleteAsync($"/api/me/map-place-photos/{int.MaxValue}");

        Assert.Equal(HttpStatusCode.NotFound, missingVisitPhotoRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingVisitPhotoDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingMapPhotoRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingMapPhotoDelete.StatusCode);
        Assert.Equal(0L, await CountVisitPhotosAsync(owner.UserId));
        Assert.Equal(0L, await CountMapPlacePhotosAsync(owner.UserId));
    }

    [Fact]
    public async Task Visit_Photo_Upload_Accepts_Exactly_20_MiB_Rejects_One_Byte_Over_And_Deletes_Photo()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        SetBearer(client, account.Token);
        var visitId = await CreateVisitAsync(client);

        using var validForm = new MultipartFormDataContent();
        AddJpeg(validForm, "file", "exact-limit.jpg", PhotoLimitBytes);
        using var validUpload = await client.PostAsync(
            $"/api/me/visits/{visitId}/photos", validForm);

        Assert.Equal(HttpStatusCode.OK, validUpload.StatusCode);
        var photo = await validUpload.Content.ReadFromJsonAsync<ApiTestPhotoResponse>();
        Assert.NotNull(photo);
        Assert.Equal(1L, await CountVisitPhotosAsync(account.UserId));

        using var delete = await client.DeleteAsync($"/api/me/photos/{photo!.Id}");
        using var afterDelete = await client.GetAsync($"/api/me/photos/{photo.Id}");
        using var repeatedDelete = await client.DeleteAsync($"/api/me/photos/{photo.Id}");

        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, repeatedDelete.StatusCode);
        Assert.Equal(0L, await CountVisitPhotosAsync(account.UserId));

        using var oversizedForm = new MultipartFormDataContent();
        AddJpeg(oversizedForm, "file", "over-limit.jpg", PhotoLimitBytes + 1);
        using var oversizedUpload = await client.PostAsync(
            $"/api/me/visits/{visitId}/photos", oversizedForm);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedUpload.StatusCode);
        Assert.Equal(0L, await CountVisitPhotosAsync(account.UserId));
    }

    [Fact]
    public async Task Map_Place_Photo_Upload_Enforces_20_MiB_Total_And_Ten_File_Limits()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        SetBearer(client, account.Token);
        var placeId = await CreateMapPlaceAsync(client);

        using var exactLimitForm = new MultipartFormDataContent();
        AddJpeg(exactLimitForm, "photos", "first-10-mib.jpg", PhotoLimitBytes / 2);
        AddJpeg(exactLimitForm, "photos", "second-10-mib.jpg", PhotoLimitBytes / 2);
        using var exactLimitUpload = await client.PostAsync(
            $"/api/me/map-places/{placeId}/photos", exactLimitForm);

        Assert.Equal(HttpStatusCode.OK, exactLimitUpload.StatusCode);
        using var photosResponse = await client.GetAsync($"/api/me/map-places/{placeId}/photos");
        var savedPhotos = await ReadItemsAsync(photosResponse);
        Assert.Equal(2, savedPhotos.Count);

        using var tenFilesForm = new MultipartFormDataContent();
        for (var index = 0; index < 10; index++)
            AddJpeg(tenFilesForm, "photos", $"within-count-limit-{index}.jpg", 3);

        using var tenFilesUpload = await client.PostAsync(
            $"/api/me/map-places/{placeId}/photos", tenFilesForm);

        Assert.Equal(HttpStatusCode.OK, tenFilesUpload.StatusCode);
        Assert.Equal(12L, await CountMapPlacePhotosAsync(account.UserId));

        using var overLimitForm = new MultipartFormDataContent();
        AddJpeg(overLimitForm, "photos", "first-10-mib.jpg", PhotoLimitBytes / 2);
        AddJpeg(overLimitForm, "photos", "over-total-by-one.jpg", PhotoLimitBytes / 2 + 1);
        using var overLimitUpload = await client.PostAsync(
            $"/api/me/map-places/{placeId}/photos", overLimitForm);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overLimitUpload.StatusCode);
        Assert.Equal(12L, await CountMapPlacePhotosAsync(account.UserId));

        using var tooManyFilesForm = new MultipartFormDataContent();
        for (var index = 0; index < 11; index++)
            AddJpeg(tooManyFilesForm, "photos", $"photo-{index}.jpg", 3);

        using var tooManyFilesUpload = await client.PostAsync(
            $"/api/me/map-places/{placeId}/photos", tooManyFilesForm);

        Assert.Equal(HttpStatusCode.BadRequest, tooManyFilesUpload.StatusCode);
        Assert.Equal(12L, await CountMapPlacePhotosAsync(account.UserId));

        var firstPhotoId = savedPhotos[0].GetProperty("id").GetInt32();
        using var delete = await client.DeleteAsync($"/api/me/map-place-photos/{firstPhotoId}");
        using var afterDelete = await client.GetAsync($"/api/me/map-place-photos/{firstPhotoId}");
        using var repeatedDelete = await client.DeleteAsync($"/api/me/map-place-photos/{firstPhotoId}");

        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, repeatedDelete.StatusCode);
        Assert.Equal(11L, await CountMapPlacePhotosAsync(account.UserId));
    }

    [Fact]
    public async Task Database_Foreign_Key_Failure_Is_Sanitized_And_Does_Not_Persist_Visit()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        SetBearer(client, account.Token);

        using var invalidRating = await client.PostAsJsonAsync("/api/me/visits", new
        {
            attractionId = _database.AttractionId,
            visitDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)).ToString("yyyy-MM-dd"),
            rating = 6,
            impressions = "Invalid rating",
            notes = ""
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidRating.StatusCode);

        using var invalidReference = await client.PostAsJsonAsync("/api/me/visits", new
        {
            attractionId = int.MaxValue,
            visitDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)).ToString("yyyy-MM-dd"),
            rating = 5,
            impressions = "Missing attraction foreign key",
            notes = ""
        });

        Assert.Equal(HttpStatusCode.BadRequest, invalidReference.StatusCode);
        var errorBody = await invalidReference.Content.ReadAsStringAsync();
        using var errorDocument = JsonDocument.Parse(errorBody);
        Assert.True(errorDocument.RootElement.TryGetProperty("error", out _));
        Assert.False(errorBody.Contains("23503", StringComparison.Ordinal));
        Assert.False(errorBody.Contains("PostgresException", StringComparison.OrdinalIgnoreCase));
        Assert.False(errorBody.Contains("localoca_api_tests", StringComparison.OrdinalIgnoreCase));
        Assert.False(errorBody.Contains("Password=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0L, await CountVisitsAsync(account.UserId));
    }

    private async Task<TestAccount> RegisterAsync(HttpClient client)
    {
        var login = "api-negative-" + Guid.NewGuid().ToString("N");
        using var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login,
            password = Guid.NewGuid().ToString("N") + "Aa1!",
            displayName = "API negative test"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        return new TestAccount(
            root.GetProperty("user").GetProperty("id").GetInt32(),
            root.GetProperty("token").GetString()
                ?? throw new InvalidOperationException("The registration response did not contain a token."));
    }

    private async Task<int> CreateVisitAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/me/visits", new
        {
            attractionId = _database.AttractionId,
            visitDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)).ToString("yyyy-MM-dd"),
            rating = 5,
            impressions = "Synthetic API negative test data",
            notes = ""
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetInt32();
    }

    private async Task<int> CreateMapPlaceAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/me/map-places", new
        {
            name = "Negative-test map place",
            latitude = 52.52,
            longitude = 13.405,
            status = "planned",
            visitDate = (string?)null,
            rating = (int?)null,
            impressions = "",
            notes = ""
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetInt32();
    }

    private async Task<long> CountVisitsAsync(int userId)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM visits WHERE user_id = @user", connection);
        command.Parameters.AddWithValue("user", userId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<long> CountVisitPhotosAsync(int userId)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"
            SELECT count(*)
            FROM photos p
            JOIN visits v ON v.visit_id = p.visit_id
            WHERE v.user_id = @user", connection);
        command.Parameters.AddWithValue("user", userId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<long> CountMapPlacePhotosAsync(int userId)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"
            SELECT count(*)
            FROM user_map_place_photos p
            JOIN user_map_places m ON m.user_map_place_id = p.user_map_place_id
            WHERE m.user_id = @user", connection);
        command.Parameters.AddWithValue("user", userId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static void SetBearer(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private const string ValidJpegBase64 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDABQODxIPDRQSEBIXFRQYHjIhHhwcHj0sLiQySUBMS0dARkVQWnNiUFVtVkVGZIhlbXd7gYKBTmCNl4x9lnN+gXz/2wBDARUXFx4aHjshITt8U0ZTfHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHz/wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAb/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFAEBAAAAAAAAAAAAAAAAAAAAAP/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/AIwAH//Z";

    private static byte[] CreateJpeg(int size)
    {
        var validJpeg = Convert.FromBase64String(ValidJpegBase64);
        var bytes = new byte[Math.Max(size, validJpeg.Length)];
        Buffer.BlockCopy(validJpeg, 0, bytes, 0, validJpeg.Length);
        return bytes;
    }

    private static void AddJpeg(
        MultipartFormDataContent form,
        string fieldName,
        string fileName,
        int size)
    {
        var content = new ByteArrayContent(CreateJpeg(size));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(content, fieldName, fileName);
    }

    private static async Task<List<JsonElement>> ReadItemsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToList();
    }

    private sealed record TestAccount(int UserId, string Token);
}

