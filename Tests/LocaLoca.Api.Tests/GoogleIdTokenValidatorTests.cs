using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocaLoca.Api.Security;
using Xunit;

namespace LocaLoca.Api.Tests;

public sealed class GoogleIdTokenValidatorTests
{
    private const string ClientId = "test-desktop-client.apps.googleusercontent.com";
    private const string Nonce = "test-oauth-nonce";

    [Fact]
    public async Task Accepts_A_Google_Id_Token_With_Valid_Signature_And_Claims()
    {
        using var rsa = RSA.Create(2048);
        using var http = CreateJwksHttpClient(rsa);
        var validator = new GoogleIdTokenValidator(ClientId, http, "https://test.invalid/jwks");
        var token = CreateToken(rsa, CreateClaims());

        var identity = await validator.ValidateAsync(token, Nonce);

        Assert.NotNull(identity);
        Assert.Equal("google-subject-123", identity.Subject);
        Assert.Equal("verified@example.com", identity.Email);
        Assert.Equal("Verified User", identity.DisplayName);
    }

    [Fact]
    public async Task Rejects_Tokens_With_Wrong_Audience_Unverified_Email_Or_Nonce()
    {
        using var rsa = RSA.Create(2048);
        using var http = CreateJwksHttpClient(rsa);
        var validator = new GoogleIdTokenValidator(ClientId, http, "https://test.invalid/jwks");

        var wrongAudience = CreateToken(rsa, CreateClaims(aud: "another-client.apps.googleusercontent.com"));
        var unverifiedEmail = CreateToken(rsa, CreateClaims(email_verified: false));
        var wrongNonce = CreateToken(rsa, CreateClaims(nonce: "different-nonce"));

        Assert.Null(await validator.ValidateAsync(wrongAudience, Nonce));
        Assert.Null(await validator.ValidateAsync(unverifiedEmail, Nonce));
        Assert.Null(await validator.ValidateAsync(wrongNonce, Nonce));
    }

    [Fact]
    public async Task Rejects_Expired_And_Tampered_Google_Id_Tokens()
    {
        using var rsa = RSA.Create(2048);
        using var http = CreateJwksHttpClient(rsa);
        var validator = new GoogleIdTokenValidator(ClientId, http, "https://test.invalid/jwks");

        var expired = CreateToken(rsa, CreateClaims(
            exp: DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()));
        var valid = CreateToken(rsa, CreateClaims());
        var parts = valid.Split('.');
        parts[2] = parts[2][..^2] + "aa";
        var tampered = string.Join('.', parts);

        Assert.Null(await validator.ValidateAsync(expired, Nonce));
        Assert.Null(await validator.ValidateAsync(tampered, Nonce));
    }

    private static object CreateClaims(
        string? aud = null,
        string? nonce = null,
        bool email_verified = true,
        long? exp = null) => new
    {
        iss = "https://accounts.google.com",
        aud = aud ?? ClientId,
        exp = exp ?? DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        nonce = nonce ?? Nonce,
        sub = "google-subject-123",
        email = "verified@example.com",
        email_verified,
        name = "Verified User"
    };

    private static string CreateToken(RSA rsa, object claims)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            alg = "RS256",
            kid = "test-key",
            typ = "JWT"
        }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signed = Encoding.ASCII.GetBytes(header + "." + payload);
        var signature = rsa.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + Base64Url(signature);
    }

    private static HttpClient CreateJwksHttpClient(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = "test-key",
                    n = Base64Url(parameters.Modulus!),
                    e = Base64Url(parameters.Exponent!)
                }
            }
        });
        return new HttpClient(new StubHandler(jwks));
    }

    private sealed class StubHandler(string jwks) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jwks, Encoding.UTF8, "application/json")
            });
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
