using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace LocaLoca.Api.Tests;

public sealed class ApiContractTests : IClassFixture<LocaLocaApiFactory>
{
    private readonly HttpClient _client;
    private readonly LocaLocaApiFactory _factory;

    public ApiContractTests(LocaLocaApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task API_OFFLINE_001_Health_is_available_without_database()
    {
        using var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task API_OFFLINE_002_Ready_reports_missing_database_configuration_without_connecting()
    {
        using var response = await _client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("apiConfigured").GetBoolean());
        Assert.False(json.RootElement.GetProperty("databaseConnected").GetBoolean());
        Assert.Contains("строка подключения",
            json.RootElement.GetProperty("message").GetString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("contains space")]
    [InlineData("invalid/name")]
    [InlineData("invalid@name")]
    public async Task API_REG_001_Invalid_login_is_rejected_before_database_access(string login)
    {
        using var response = await RegisterAsync(login, "valid-password", "Test User");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task API_REG_002_Password_shorter_than_six_characters_is_rejected_before_database_access()
    {
        using var response = await RegisterAsync("valid_login", "12345", "Test User");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task API_REG_003_Display_name_longer_than_one_hundred_characters_is_rejected_before_database_access()
    {
        using var response = await RegisterAsync("valid_login", "valid-password", new string('A', 101));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/me/visits")]
    [InlineData("GET", "/api/me/wishlist")]
    [InlineData("GET", "/api/me/summary")]
    [InlineData("GET", "/api/me/map-markers")]
    [InlineData("GET", "/api/me/photos/1")]
    [InlineData("GET", "/api/me/map-place-photos/1")]
    [InlineData("GET", "/api/me/map-places/1/photos")]
    [InlineData("DELETE", "/api/me/visits/1")]
    [InlineData("DELETE", "/api/me/wishlist/1")]
    [InlineData("POST", "/api/me/visits/save")]
    [InlineData("POST", "/api/me/map-places/1/photos")]
    public async Task API_SEC_001_Protected_endpoint_requires_bearer_token(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task API_SEC_002_Tampered_session_token_is_rejected()
    {
        using var request = AuthenticatedRequest(HttpMethod.Get, "/api/me/visits");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tampered.token.value");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task API_PROFILE_001_Blank_display_name_is_rejected_before_database_access()
    {
        using var request = AuthenticatedRequest(HttpMethod.Put, "/api/me/profile/display-name");
        request.Content = JsonContent.Create(new { displayName = "   " });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task API_MAP_001_Invalid_latitude_is_rejected_before_database_access()
    {
        using var request = AuthenticatedRequest(HttpMethod.Post, "/api/me/map-places");
        request.Content = JsonContent.Create(new
        {
            name = "Invalid point",
            latitude = 91.0,
            longitude = 30.0,
            status = "planned",
            visitDate = (string?)null,
            rating = (int?)null,
            impressions = "",
            notes = ""
        });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task API_MAP_002_Unknown_place_status_is_rejected_before_database_access()
    {
        using var request = AuthenticatedRequest(HttpMethod.Put, "/api/me/map-places/1");
        request.Content = JsonContent.Create(new
        {
            name = "Test place",
            status = "completed",
            visitDate = (string?)null,
            rating = (int?)null,
            impressions = "",
            notes = ""
        });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task API_VISIT_001_Non_positive_attraction_id_is_rejected_before_database_access()
    {
        using var request = AuthenticatedRequest(HttpMethod.Post, "/api/me/visits");
        request.Content = JsonContent.Create(new
        {
            attractionId = 0,
            visitDate = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            rating = 5,
            impressions = "",
            notes = ""
        });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task API_VISIT_002_Future_visit_date_is_rejected_before_database_access()
    {
        using var request = AuthenticatedRequest(HttpMethod.Post, "/api/me/visits/save");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["attractionId"] = "1",
            ["rating"] = "5",
            ["visitDate"] = DateOnly.FromDateTime(DateTime.Today.AddDays(1))
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task API_PHOTO_001_Unsupported_photo_extension_is_rejected_before_database_access()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([0x01, 0x02, 0x03]), "file", "payload.txt");

        using var request = AuthenticatedRequest(HttpMethod.Post, "/api/me/visits/1/photos");
        request.Content = content;

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task API_PHOTO_002_Invalid_jpeg_signature_is_rejected_before_database_access()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([0x00, 0x01, 0x02, 0x03]), "file", "not-a-photo.jpg");

        using var request = AuthenticatedRequest(HttpMethod.Post, "/api/me/visits/1/photos");
        request.Content = content;

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task API_PHOTO_003_Invalid_personal_place_photo_signature_is_rejected_before_database_access()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([0x00, 0x01, 0x02, 0x03]), "photos", "not-a-photo.png");

        using var request = AuthenticatedRequest(HttpMethod.Post, "/api/me/map-places/1/photos");
        request.Content = content;

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private HttpRequestMessage AuthenticatedRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateSessionToken(userId: 1));
        return request;
    }

    private Task<HttpResponseMessage> RegisterAsync(string login, string password, string displayName)
    {
        return _client.PostAsJsonAsync("/api/auth/register", new
        {
            login,
            password,
            displayName
        });
    }
}
