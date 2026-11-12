using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LocaLoca.Api.Security;
using Npgsql;
using Xunit;

namespace LocaLoca.Api.Tests;

[Collection(PostgreSqlApiCollection.Name)]
public sealed class ApiIntegrationTests
{
    private readonly PostgreSqlApiFixture _database;

    public ApiIntegrationTests(PostgreSqlApiFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Health_And_Ready_Are_Available_With_The_Isolated_Database()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/health");
        using var ready = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Unknown_Route_Returns_404_With_The_Standard_Error_Contract()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/route-that-does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Malformed_Json_Request_Returns_400()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        SetBearer(client, account.Token);

        using var content = new StringContent("{ invalid json", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/me/visits", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registration_Stores_A_Password_Hash_Not_The_Original_Password()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        var storedHash = await ReadStoredPasswordHashAsync(account.Login);

        Assert.False(
            string.Equals(account.Password, storedHash, StringComparison.Ordinal),
            "The stored password representation must differ from the supplied password.");
        Assert.True(PasswordHasher.Verify(account.Password, storedHash));
        Assert.StartsWith("pbkdf2-sha256$", storedHash, StringComparison.Ordinal);
        Assert.Equal(4, storedHash.Split((char)36).Length);
    }

    [Fact]
    public async Task Duplicate_Login_Is_Rejected_Without_Creating_A_Second_Account()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        using var duplicate = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login = account.Login.ToUpperInvariant(),
            password = CreateTestPassword(),
            displayName = "Duplicate account"
        });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, await CountUsersAsync(account.Login));
    }

    [Fact]
    public async Task Login_Accepts_The_Correct_Password_And_Rejects_A_Wrong_One()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);

        using var validLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            login = account.Login,
            password = account.Password
        });
        using var invalidLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            login = account.Login,
            password = CreateTestPassword()
        });

        Assert.Equal(HttpStatusCode.OK, validLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidLogin.StatusCode);
    }

    [Fact]
    public async Task Login_Upgrades_A_Legacy_Password_Hash_After_Successful_Authentication()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        var legacyHash = CreateLegacyPasswordHash(account.Password, 10_000);
        await StorePasswordHashAsync(account.Login, legacyHash);

        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            login = account.Login,
            password = account.Password
        });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var upgradedHash = await ReadStoredPasswordHashAsync(account.Login);
        Assert.NotEqual(legacyHash, upgradedHash);
        Assert.StartsWith("pbkdf2-sha256$", upgradedHash, StringComparison.Ordinal);
        Assert.True(PasswordHasher.Verify(account.Password, upgradedHash));
        Assert.False(PasswordHasher.NeedsRehash(upgradedHash));
    }

    [Fact]
    public async Task Failed_Login_Does_Not_Upgrade_A_Legacy_Password_Hash()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        var legacyHash = CreateLegacyPasswordHash(account.Password, 10_000);
        await StorePasswordHashAsync(account.Login, legacyHash);

        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            login = account.Login,
            password = CreateTestPassword()
        });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(legacyHash, await ReadStoredPasswordHashAsync(account.Login));
    }

    [Fact]
    public async Task Protected_Endpoints_Reject_Requests_Without_A_Session()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        using var visits = await client.GetAsync("/api/me/visits");
        using var wishlist = await client.GetAsync("/api/me/wishlist");
        using var map = await client.GetAsync("/api/me/map-markers");

        Assert.Equal(HttpStatusCode.Unauthorized, visits.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wishlist.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, map.StatusCode);

        using var errorDocument = JsonDocument.Parse(await visits.Content.ReadAsStringAsync());
        Assert.True(errorDocument.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Visits_Are_Isolated_And_Cannot_Be_Changed_By_Another_Account()
    {
        using var factory = _database.CreateFactory();
        using var ownerClient = factory.CreateClient();
        using var otherClient = factory.CreateClient();

        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        SetBearer(ownerClient, owner.Token);
        SetBearer(otherClient, other.Token);

        var visitId = await CreateVisitAsync(ownerClient);

        using var ownerResponse = await ownerClient.GetAsync("/api/me/visits");
        using var otherResponse = await otherClient.GetAsync("/api/me/visits");
        var ownerItems = await ReadItemsAsync(ownerResponse);
        var otherItems = await ReadItemsAsync(otherResponse);

        Assert.Contains(ownerItems, item => item.GetProperty("id").GetInt32() == visitId);
        Assert.Empty(otherItems);

        using var update = await otherClient.PutAsJsonAsync($"/api/me/visits/{visitId}", new
        {
            attractionId = _database.AttractionId,
            visitDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-2)).ToString("yyyy-MM-dd"),
            rating = 1,
            impressions = "attempted cross-account update",
            notes = ""
        });
        using var delete = await otherClient.DeleteAsync($"/api/me/visits/{visitId}");

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task Wishlist_Is_Isolated_Between_Accounts()
    {
        using var factory = _database.CreateFactory();
        using var ownerClient = factory.CreateClient();
        using var otherClient = factory.CreateClient();

        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        SetBearer(ownerClient, owner.Token);
        SetBearer(otherClient, other.Token);

        using var add = await ownerClient.PostAsync($"/api/me/wishlist/{_database.AttractionId}", content: null);
        using var duplicateAdd = await ownerClient.PostAsync($"/api/me/wishlist/{_database.AttractionId}", content: null);
        using var ownerResponse = await ownerClient.GetAsync("/api/me/wishlist");
        using var otherResponse = await otherClient.GetAsync("/api/me/wishlist");

        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicateAdd.StatusCode);

        var ownerItems = await ReadIntegerItemsAsync(ownerResponse);
        var otherItems = await ReadIntegerItemsAsync(otherResponse);

        Assert.Equal(new[] { _database.AttractionId }, ownerItems);
        Assert.Empty(otherItems);
    }

    [Fact]
    public async Task Photo_Binary_Is_Persisted_And_Only_Its_Owner_Can_Read_It()
    {
        using var factory = _database.CreateFactory();
        using var ownerClient = factory.CreateClient();
        using var otherClient = factory.CreateClient();

        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        SetBearer(ownerClient, owner.Token);
        SetBearer(otherClient, other.Token);

        var visitId = await CreateVisitAsync(ownerClient);
        byte[] jpegBytes = [0xFF, 0xD8, 0xFF, 0xD9];

        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(jpegBytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(image, "file", "test-photo.jpg");

        using var upload = await ownerClient.PostAsync($"/api/me/visits/{visitId}/photos", form);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var photo = await upload.Content.ReadFromJsonAsync<ApiTestPhotoResponse>();
        Assert.NotNull(photo);
        var photoId = photo!.Id;

        using var ownerPhoto = await ownerClient.GetAsync($"/api/me/photos/{photoId}");
        using var otherPhoto = await otherClient.GetAsync($"/api/me/photos/{photoId}");

        Assert.Equal(HttpStatusCode.OK, ownerPhoto.StatusCode);
        Assert.Equal("image/jpeg", ownerPhoto.Content.Headers.ContentType?.MediaType);
        Assert.Equal(jpegBytes, await ownerPhoto.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, otherPhoto.StatusCode);

        using var foreignDelete = await otherClient.DeleteAsync($"/api/me/photos/{photoId}");
        using var stillAvailableToOwner = await ownerClient.GetAsync($"/api/me/photos/{photoId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillAvailableToOwner.StatusCode);

        using var delete = await ownerClient.DeleteAsync($"/api/me/photos/{photoId}");
        using var afterDelete = await ownerClient.GetAsync($"/api/me/photos/{photoId}");

        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    [Fact]
    public async Task Personal_Map_Place_Cannot_Be_Changed_By_Another_Account()
    {
        using var factory = _database.CreateFactory();
        using var ownerClient = factory.CreateClient();
        using var otherClient = factory.CreateClient();

        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        SetBearer(ownerClient, owner.Token);
        SetBearer(otherClient, other.Token);

        using var create = await ownerClient.PostAsJsonAsync("/api/me/map-places", new
        {
            name = "Owner-only test point",
            latitude = 52.52,
            longitude = 13.405,
            status = "planned",
            visitDate = (string?)null,
            rating = (int?)null,
            impressions = "",
            notes = ""
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var place = await create.Content.ReadFromJsonAsync<ApiTestMapPlaceResponse>();
        Assert.NotNull(place);
        var placeId = place!.Id;

        using var foreignUpdate = await otherClient.PutAsJsonAsync($"/api/me/map-places/{placeId}", new
        {
            name = "Attempted foreign edit",
            status = "planned",
            visitDate = (string?)null,
            rating = (int?)null,
            impressions = "",
            notes = ""
        });

        Assert.Equal(HttpStatusCode.NotFound, foreignUpdate.StatusCode);

        using var ownerMarkersResponse = await ownerClient.GetAsync("/api/me/map-markers");
        using var otherMarkersResponse = await otherClient.GetAsync("/api/me/map-markers");
        var ownerMarkers = await ReadItemsAsync(ownerMarkersResponse);
        var otherMarkers = await ReadItemsAsync(otherMarkersResponse);

        Assert.Contains(ownerMarkers, item =>
            item.TryGetProperty("userPlaceId", out var markerId) &&
            markerId.ValueKind == JsonValueKind.Number &&
            markerId.GetInt32() == placeId);
        Assert.DoesNotContain(otherMarkers, item =>
            item.TryGetProperty("userPlaceId", out var markerId) &&
            markerId.ValueKind == JsonValueKind.Number &&
            markerId.GetInt32() == placeId);

        using var ownerUpdate = await ownerClient.PutAsJsonAsync($"/api/me/map-places/{placeId}", new
        {
            name = "Owner updated test point",
            status = "visited",
            visitDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)).ToString("yyyy-MM-dd"),
            rating = 5,
            impressions = "Updated by owner",
            notes = "Map point update test"
        });
        Assert.Equal(HttpStatusCode.OK, ownerUpdate.StatusCode);

        using var ownerDelete = await ownerClient.DeleteAsync($"/api/me/map-places/{placeId}");
        using var afterDeleteResponse = await ownerClient.GetAsync("/api/me/map-markers");
        var afterDeleteMarkers = await ReadItemsAsync(afterDeleteResponse);

        Assert.Equal(HttpStatusCode.OK, ownerDelete.StatusCode);
        Assert.DoesNotContain(afterDeleteMarkers, item =>
            item.TryGetProperty("userPlaceId", out var markerId) &&
            markerId.ValueKind == JsonValueKind.Number &&
            markerId.GetInt32() == placeId);
    }

    [Fact]
    public async Task Personal_Map_Place_Photo_Can_Be_Deleted_Only_By_Its_Owner()
    {
        using var factory = _database.CreateFactory();
        using var ownerClient = factory.CreateClient();
        using var otherClient = factory.CreateClient();
        using var anonymousClient = factory.CreateClient();

        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        SetBearer(ownerClient, owner.Token);
        SetBearer(otherClient, other.Token);

        using var createPlace = await ownerClient.PostAsJsonAsync("/api/me/map-places", new
        {
            name = "Photo deletion test point",
            latitude = 52.52,
            longitude = 13.405,
            status = "planned",
            visitDate = (string?)null,
            rating = (int?)null,
            impressions = "",
            notes = ""
        });
        Assert.Equal(HttpStatusCode.OK, createPlace.StatusCode);
        var place = await createPlace.Content.ReadFromJsonAsync<ApiTestMapPlaceResponse>();
        Assert.NotNull(place);
        var placeId = place!.Id;

        byte[] jpegBytes = [0xFF, 0xD8, 0xFF, 0xD9];
        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(jpegBytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(image, "photos", "map-place-photo.jpg");

        using var upload = await ownerClient.PostAsync($"/api/me/map-places/{placeId}/photos", form);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        using var photosResponse = await ownerClient.GetAsync($"/api/me/map-places/{placeId}/photos");
        var photos = await ReadItemsAsync(photosResponse);
        var photo = Assert.Single(photos);
        var photoId = photo.GetProperty("id").GetInt32();

        using var ownerPhoto = await ownerClient.GetAsync($"/api/me/map-place-photos/{photoId}");
        Assert.Equal(HttpStatusCode.OK, ownerPhoto.StatusCode);
        Assert.Equal(jpegBytes, await ownerPhoto.Content.ReadAsByteArrayAsync());

        using var foreignRead = await otherClient.GetAsync($"/api/me/map-place-photos/{photoId}");
        using var anonymousDelete = await anonymousClient.DeleteAsync($"/api/me/map-place-photos/{photoId}");
        using var foreignDelete = await otherClient.DeleteAsync($"/api/me/map-place-photos/{photoId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);

        using var stillAvailable = await ownerClient.GetAsync($"/api/me/map-place-photos/{photoId}");
        Assert.Equal(HttpStatusCode.OK, stillAvailable.StatusCode);

        using var delete = await ownerClient.DeleteAsync($"/api/me/map-place-photos/{photoId}");
        using var afterDelete = await ownerClient.GetAsync($"/api/me/map-place-photos/{photoId}");
        using var photosAfterDelete = await ownerClient.GetAsync($"/api/me/map-places/{place.Id}/photos");

        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
        Assert.Empty(await ReadItemsAsync(photosAfterDelete));
    }

    [Fact]
    public async Task Owner_Can_Update_And_Delete_Their_Own_Visit()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        SetBearer(client, account.Token);
        var visitId = await CreateVisitAsync(client);

        using var update = await client.PutAsJsonAsync($"/api/me/visits/{visitId}", new
        {
            attractionId = _database.AttractionId,
            visitDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-2)).ToString("yyyy-MM-dd"),
            rating = 4,
            impressions = "Updated by owner",
            notes = "Owner update test"
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        using var afterUpdateResponse = await client.GetAsync("/api/me/visits");
        var updatedItems = await ReadItemsAsync(afterUpdateResponse);
        Assert.Contains(updatedItems, item =>
            item.GetProperty("id").GetInt32() == visitId &&
            item.GetProperty("rating").GetInt16() == 4);

        using var delete = await client.DeleteAsync($"/api/me/visits/{visitId}");
        using var afterDeleteResponse = await client.GetAsync("/api/me/visits");
        var remainingItems = await ReadItemsAsync(afterDeleteResponse);

        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.DoesNotContain(remainingItems, item => item.GetProperty("id").GetInt32() == visitId);
    }


    [Fact]
    public async Task Concurrent_Visit_Updates_Leave_One_Complete_Consistent_Record()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        SetBearer(client, account.Token);
        var visitId = await CreateVisitAsync(client);

        const int updateCount = 12;
        var updates = Enumerable.Range(0, updateCount)
            .Select(index => new
            {
                VisitDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-(index + 2))).ToString("yyyy-MM-dd"),
                Rating = index % 5 + 1,
                Impressions = $"concurrent-update-{index}",
                Notes = $"concurrent-notes-{index}"
            })
            .ToArray();

        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateTasks = updates.Select(async update =>
        {
            await startGate.Task;
            return await client.PutAsJsonAsync($"/api/me/visits/{visitId}", new
            {
                attractionId = _database.AttractionId,
                visitDate = update.VisitDate,
                rating = update.Rating,
                impressions = update.Impressions,
                notes = update.Notes
            });
        }).ToArray();

        startGate.SetResult(true);
        var responses = await Task.WhenAll(updateTasks);
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }

        using var visitsResponse = await client.GetAsync("/api/me/visits");
        var storedVisit = Assert.Single(
            await ReadItemsAsync(visitsResponse),
            item => item.GetProperty("id").GetInt32() == visitId);

        var finalStateMatchesOneCompleteUpdate = updates.Any(update =>
            storedVisit.GetProperty("visitDate").GetString() == update.VisitDate &&
            storedVisit.GetProperty("rating").GetInt32() == update.Rating &&
            storedVisit.GetProperty("impressions").GetString() == update.Impressions &&
            storedVisit.GetProperty("notes").GetString() == update.Notes);

        Assert.True(
            finalStateMatchesOneCompleteUpdate,
            "The final visit must contain all fields from one submitted update, not a mixture of concurrent updates.");
    }

    [Fact]
    public async Task Concurrent_Wishlist_Additions_Create_Only_One_Record()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        SetBearer(client, account.Token);

        const int requestCount = 12;
        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var addTasks = Enumerable.Range(0, requestCount)
            .Select(async _ =>
            {
                await startGate.Task;
                return await client.PostAsync($"/api/me/wishlist/{_database.AttractionId}", content: null);
            })
            .ToArray();

        startGate.SetResult(true);
        var responses = await Task.WhenAll(addTasks);
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }

        using var wishlistResponse = await client.GetAsync("/api/me/wishlist");
        Assert.Equal(new[] { _database.AttractionId }, await ReadIntegerItemsAsync(wishlistResponse));
    }

    [Fact]
    public async Task Api_Returns_503_During_PostgreSql_Outage_And_Recovers_After_Restart()
    {
        // Disable pooling so the outage is observed on a fresh TCP connection,
        // rather than depending on whether an idle pooled connection was reused.
        var connectionString = new NpgsqlConnectionStringBuilder(_database.ConnectionString)
        {
            Pooling = false
        }.ConnectionString;

        using var factory = new ApiTestFactory(connectionString);
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        SetBearer(client, account.Token);
        var visitId = await CreateVisitAsync(client);

        var databaseStopped = false;
        try
        {
            await _database.StopDatabaseAsync();
            databaseStopped = true;

            using var health = await client.GetAsync("/health");
            using var unavailable = await client.GetAsync("/api/me/visits");

            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);

            var errorBody = await unavailable.Content.ReadAsStringAsync();
            Assert.Contains("Сервис временно недоступен", errorBody);
            Assert.DoesNotContain("localoca_api_tests", errorBody);
            Assert.DoesNotContain("Password=", errorBody);
        }
        finally
        {
            if (databaseStopped)
                await _database.StartDatabaseAsync();
        }

        HttpResponseMessage? ready = null;
        string lastReadyBody = "(no response)";
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var response = await client.GetAsync("/ready");
            lastReadyBody = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                ready = response;
                break;
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        using var readyResponse = ready ?? throw new Xunit.Sdk.XunitException(
            $"PostgreSQL did not become ready within 30 seconds after the container restart. Last /ready response: {lastReadyBody}");
        using var recovered = await client.GetAsync("/api/me/visits");

        Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
        var recoveredVisits = await ReadItemsAsync(recovered);
        Assert.Contains(recoveredVisits, item => item.GetProperty("id").GetInt32() == visitId);
    }

    [Fact]
    public async Task Photo_Upload_Rejects_Invalid_Content_Before_Writing_To_PostgreSql()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        SetBearer(client, account.Token);
        var visitId = await CreateVisitAsync(client);
        var initialCount = await CountPhotosAsync();

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([0x00, 0x01, 0x02]), "file", "not-an-image.jpg");

        using var upload = await client.PostAsync($"/api/me/visits/{visitId}/photos", form);

        Assert.Equal(HttpStatusCode.BadRequest, upload.StatusCode);
        Assert.Equal(initialCount, await CountPhotosAsync());
    }

    private async Task<TestAccount> RegisterAsync(HttpClient client)
    {
        var login = "api-test-" + Guid.NewGuid().ToString("N");
        var password = CreateTestPassword();

        using var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login,
            password,
            displayName = "API integration test"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<ApiTestAuthResponse>();
        Assert.NotNull(auth);
        Assert.False(string.IsNullOrWhiteSpace(auth!.Token));

        return new TestAccount(login, password, auth.Token, auth.User.Id);
    }

    private static string CreateTestPassword() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)) + "Aa!";

    private static void SetBearer(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<int> CreateVisitAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/me/visits", new
        {
            attractionId = _database.AttractionId,
            visitDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)).ToString("yyyy-MM-dd"),
            rating = 5,
            impressions = "Synthetic API test data",
            notes = ""
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiTestVisitResponse>();
        Assert.NotNull(result);
        return result!.Id;
    }

    private async Task<string> ReadStoredPasswordHashAsync(string login)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT password_hash FROM users WHERE login = @login",
            connection);
        command.Parameters.AddWithValue("login", login);
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Expected test user was not stored."));
    }

    private async Task StorePasswordHashAsync(string login, string passwordHash)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE users SET password_hash = @hash WHERE login = @login",
            connection);
        command.Parameters.AddWithValue("hash", passwordHash);
        command.Parameters.AddWithValue("login", login);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static string CreateLegacyPasswordHash(string password, int iterations)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return iterations.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
               Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }

    private async Task<int> CountUsersAsync(string login)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM users WHERE login = @login",
            connection);
        command.Parameters.AddWithValue("login", login);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<long> CountPhotosAsync()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM photos", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<List<JsonElement>> ReadItemsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToList();
    }

    private static async Task<int[]> ReadIntegerItemsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetInt32()).ToArray();
    }

    private sealed record TestAccount(string Login, string Password, string Token, int UserId);
}

public sealed record ApiTestAuthResponse(ApiTestUserResponse User, string Token);
public sealed record ApiTestUserResponse(int Id, string Login, string DisplayName);
public sealed record ApiTestVisitResponse(int Id, string Message);
public sealed record ApiTestPhotoResponse(int Id, string FilePath, string Caption);
public sealed record ApiTestMapPlaceResponse(int Id, string Message);
