using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace LocaLoca.Api.Tests;

public sealed class ApiContractTests : IClassFixture<LocaLocaApiFactory>
{
    private readonly HttpClient _client;

    public ApiContractTests(LocaLocaApiFactory factory)
    {
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
