using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocaLoca.Api.Security;

public sealed record GoogleIdentity(string Subject, string Email, string DisplayName);

/// <summary>Validates Google OpenID Connect ID tokens using Google's published signing keys.</summary>
public sealed class GoogleIdTokenValidator
{
    private const string JwksUri = "https://www.googleapis.com/oauth2/v3/certs";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _clientId;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Dictionary<string, RSAParameters> _keys = new(StringComparer.Ordinal);
    private DateTimeOffset _keysExpireAt = DateTimeOffset.MinValue;

    public GoogleIdTokenValidator(string clientId)
    {
        _clientId = string.IsNullOrWhiteSpace(clientId)
            ? throw new InvalidOperationException("LOCA_GOOGLE_CLIENT_ID is not configured.")
            : clientId;
    }

    public async Task<GoogleIdentity?> ValidateAsync(
        string? idToken,
        string expectedNonce,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > 16_384 ||
            string.IsNullOrWhiteSpace(expectedNonce))
            return null;

        try
        {
            var parts = idToken.Split('.');
            if (parts.Length != 3) return null;

            using var header = JsonDocument.Parse(Decode(parts[0]));
            var root = header.RootElement;
            if (!root.TryGetProperty("alg", out var alg) || alg.GetString() != "RS256" ||
                !root.TryGetProperty("kid", out var kidElement))
                return null;

            var kid = kidElement.GetString();
            if (string.IsNullOrWhiteSpace(kid)) return null;

            var keys = await GetKeysAsync(kid, cancellationToken);
            if (!keys.TryGetValue(kid, out var parameters)) return null;

            using var rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            var signedBytes = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!rsa.VerifyData( signedBytes, DecodeBytes(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return null;

            using var payload = JsonDocument.Parse(Decode(parts[1]));
            var claims = payload.RootElement;

            var issuer = GetString(claims, "iss");
            if (issuer is not ("accounts.google.com" or "https://accounts.google.com"))
                return null;

            if (!HasAudience(claims, _clientId)) return null;
            var authorizedParty = GetString(claims, "azp");
            if (authorizedParty is not null && !string.Equals(authorizedParty, _clientId, StringComparison.Ordinal))
                return null;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!TryInt64(claims, "exp", out var expiry) || expiry <= now ||
                !TryInt64(claims, "iat", out var issuedAt) || issuedAt > now + 60 ||
                issuedAt < now - 3600)
                return null;

            var nonce = GetString(claims, "nonce");
            if (nonce is null || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(nonce), Encoding.UTF8.GetBytes(expectedNonce)))
                return null;

            var subject = GetString(claims, "sub");
            var email = GetString(claims, "email");
            if (string.IsNullOrWhiteSpace(subject) || subject.Length > 255 ||
                string.IsNullOrWhiteSpace(email) || email.Length > 320 ||
                !IsEmailVerified(claims))
                return null;

            var displayName = GetString(claims, "name");
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = email.Split('@')[0];
            displayName = displayName.Trim();
            if (displayName.Length > 100) displayName = displayName[..100];

            return new GoogleIdentity(subject, email.Trim(), displayName);
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or CryptographicException or
            HttpRequestException or TaskCanceledException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private async Task<Dictionary<string, RSAParameters>> GetKeysAsync(
        string requestedKid, CancellationToken cancellationToken)
    {
        if (_keysExpireAt > DateTimeOffset.UtcNow && _keys.ContainsKey(requestedKid))
            return _keys;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_keysExpireAt > DateTimeOffset.UtcNow && _keys.ContainsKey(requestedKid))
                return _keys;

            using var response = await Http.GetAsync(JwksUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var refreshed = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);

            foreach (var key in document.RootElement.GetProperty("keys").EnumerateArray())
            {
                if (GetString(key, "kty") != "RSA" || GetString(key, "use") != "sig" ||
                    GetString(key, "alg") is { } keyAlg && keyAlg != "RS256")
                    continue;

                var id = GetString(key, "kid");
                var modulus = GetString(key, "n");
                var exponent = GetString(key, "e");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(modulus) ||
                    string.IsNullOrWhiteSpace(exponent))
                    continue;

                refreshed[id] = new RSAParameters
                {
                    Modulus = DecodeBytes(modulus),
                    Exponent = DecodeBytes(exponent)
                };
            }

            if (refreshed.Count == 0 || !refreshed.ContainsKey(requestedKid))
                return new Dictionary<string, RSAParameters>(StringComparer.Ordinal);

            _keys = refreshed;
            _keysExpireAt = DateTimeOffset.UtcNow.AddMinutes(30);
            return _keys;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static bool HasAudience(JsonElement claims, string clientId)
    {
        if (!claims.TryGetProperty("aud", out var audience)) return false;
        if (audience.ValueKind == JsonValueKind.String)
            return audience.GetString() == clientId;
        return audience.ValueKind == JsonValueKind.Array &&
               audience.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == clientId);
    }

    private static bool IsEmailVerified(JsonElement claims)
    {
        if (!claims.TryGetProperty("email_verified", out var value)) return false;
        return value.ValueKind == JsonValueKind.True ||
               value.ValueKind == JsonValueKind.String && value.GetString() == "true";
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryInt64(JsonElement element, string name, out long result)
    {
        result = 0;
        return element.TryGetProperty(name, out var value) && value.TryGetInt64(out result);
    }

    private static string Decode(string value) => Encoding.UTF8.GetString(DecodeBytes(value));

    private static byte[] DecodeBytes(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }
}
