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
    public async Task Registration_Stores_A_Password_Hash_Not_The_Original_Password()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var account = await RegisterAsync(client);
        var storedHash = await ReadStoredPasswordHashAsync(account.Login);

        Assert.NotEqual(account.Password, storedHash);
        Assert.True(PasswordHasher.Verify(account.Password, storedHash));
        Assert.Equal(3, storedHash.Split('.').Length);
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

        var photo = await upload.Content.ReadFromJsonAsync<PhotoResponse>();
        Assert.NotNull(photo);

        using var ownerPhoto = await ownerClient.GetAsync($"/api/me/photos/{photo.Id}");
        using var otherPhoto = await otherClient.GetAsync($"/api/me/photos/{photo.Id}");

        Assert.Equal(HttpStatusCode.OK, ownerPhoto.StatusCode);
        Assert.Equal("image/jpeg", ownerPhoto.Content.Headers.ContentType?.MediaType);
        Assert.Equal(jpegBytes, await ownerPhoto.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, otherPhoto.StatusCode);
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
        var place = await create.Content.ReadFromJsonAsync<MapPlaceResponse>();
        Assert.NotNull(place);

        using var foreignUpdate = await otherClient.PutAsJsonAsync($"/api/me/map-places/{place.Id}", new
        {
            name = "Attempted foreign edit",
            status = "planned",
            visitDate = (string?)null,
            rating = (int?)null,
            impressions = "",
            notes = ""
        });

        Assert.Equal(HttpStatusCode.NotFound, foreignUpdate.StatusCode);
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

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));

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
        var result = await response.Content.ReadFromJsonAsync<VisitResponse>();
        Assert.NotNull(result);
        return result.Id;
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
    private sealed record AuthResponse(UserResponse User, string Token);
    private sealed record UserResponse(int Id, string Login, string DisplayName);
    private sealed record VisitResponse(int Id, string Message);
    private sealed record PhotoResponse(int Id, string FilePath, string Caption);
    private sealed record MapPlaceResponse(int Id, string Message);
}
