using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace LocaLoca.Api.Tests;

[Collection(PostgreSqlApiCollection.Name)]
public sealed class AuthenticationSecurityTests
{
    private readonly PostgreSqlApiFixture _database;

    public AuthenticationSecurityTests(PostgreSqlApiFixture database) => _database = database;

    [Fact]
    public async Task Protected_Endpoint_Rejects_An_Expired_Session_Token()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        var expiredToken = CreateSignedToken(
            factory.SessionSecretForTests,
            account.UserId,
            DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds());

        SetBearer(client, expiredToken);
        using var response = await client.GetAsync("/api/me/visits");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_Endpoint_Rejects_Tampered_And_Malformed_Tokens()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);

        SetBearer(client, account.Token);
        using (var valid = await client.GetAsync("/api/me/visits"))
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        var invalidTokens = new[]
        {
            TamperSignature(account.Token),
            TamperUserId(account.Token, account.UserId + 1),
            "not-a-token",
            "a.b.c",
            "."
        };

        foreach (var token in invalidTokens)
        {
            SetBearer(client, token);
            using var response = await client.GetAsync("/api/me/visits");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Logout_Revokes_The_Current_Session_For_Protected_Endpoints()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        SetBearer(client, account.Token);

        using var logout = await client.PostAsync("/api/auth/logout", content: null);
        using var protectedRequest = await client.GetAsync("/api/me/visits");
        using var repeatedLogout = await client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, protectedRequest.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, repeatedLogout.StatusCode);
    }

    [Fact]
    public async Task Login_Rate_Limit_Rejects_A_Burst_Of_Failed_Attempts()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 7; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", new
            {
                login = account.Login,
                password = "Definitely-the-wrong-password!"
            });
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                using var errorDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.True(errorDocument.RootElement.TryGetProperty("error", out _));
            }
        }

        Assert.Equal(5, statuses.Count(status => status == HttpStatusCode.Unauthorized));
        Assert.Equal(2, statuses.Count(status => status == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Registration_Rate_Limit_Rejects_Excessive_Accounts_From_One_Ip()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();
        var logins = Enumerable.Range(0, 4)
            .Select(_ => "auth-burst-" + Guid.NewGuid().ToString("N"))
            .ToArray();
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < logins.Length; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/register", new
            {
                login = logins[attempt],
                password = CreateTestPassword(),
                displayName = "Registration rate-limit test"
            });
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                using var errorDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.True(errorDocument.RootElement.TryGetProperty("error", out _));
            }
        }

        Assert.Equal(3, statuses.Count(status => status == HttpStatusCode.OK));
        Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.TooManyRequests));
        Assert.Equal(0L, await CountUsersAsync(logins[^1]));
    }

    [Fact]
    public async Task Logout_Rate_Limit_Rejects_Excessive_Requests_From_One_Ip()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 11; attempt++)
        {
            using var response = await client.PostAsync("/api/auth/logout", content: null);
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                using var errorDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.True(errorDocument.RootElement.TryGetProperty("error", out _));
            }
        }

        Assert.Equal(10, statuses.Count(status => status == HttpStatusCode.Unauthorized));
        Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.TooManyRequests));
    }

    private async Task<TestAccount> RegisterAsync(HttpClient client)
    {
        var login = "auth-security-" + Guid.NewGuid().ToString("N");
        var password = CreateTestPassword();

        using var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login,
            password,
            displayName = "Authentication security test"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("token").GetString();
        var userId = json.RootElement.GetProperty("user").GetProperty("id").GetInt32();

        Assert.False(string.IsNullOrWhiteSpace(token));
        return new TestAccount(login, password, token!, userId);
    }

    private async Task<long> CountUsersAsync(string login)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM users WHERE login = @login",
            connection);
        command.Parameters.AddWithValue("login", login);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string CreateTestPassword() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "Aa!";

    private static void SetBearer(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static string CreateSignedToken(string secret, int userId, long expiresAt)
    {
        var payload = Encoding.UTF8.GetBytes($"{userId}:{expiresAt}");
        var encodedPayload = Base64UrlEncode(payload);
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.ASCII.GetBytes(encodedPayload));

        return $"{encodedPayload}.{Base64UrlEncode(signature)}";
    }

    private static string TamperSignature(string token)
    {
        var parts = token.Split('.');
        var signature = parts[1].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A';
        return $"{parts[0]}.{new string(signature)}";
    }

    private static string TamperUserId(string token, int replacementUserId)
    {
        var parts = token.Split('.');
        var fields = Encoding.UTF8.GetString(Base64UrlDecode(parts[0])).Split(':');
        fields[0] = replacementUserId.ToString();
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(string.Join(':', fields)));
        return $"{payload}.{parts[1]}";
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }

    private sealed record TestAccount(string Login, string Password, string Token, int UserId);
}
