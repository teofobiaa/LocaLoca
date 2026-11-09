using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace LocaLoca.Api.Security;

public sealed class SessionStore
{
    private readonly byte[] _secret;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public SessionStore(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configuredSecret = configuration["LOCA_SESSION_SECRET"];
        // Local development remains zero-configuration. In cloud hosting the
        // API startup validates a stable secret so tokens survive restarts.
        _secret = string.IsNullOrWhiteSpace(configuredSecret)
            ? RandomNumberGenerator.GetBytes(32)
            : Encoding.UTF8.GetBytes(configuredSecret);

        if (_secret.Length < 32)
            throw new InvalidOperationException("LOCA_SESSION_SECRET must contain at least 32 characters.");
    }

    public string Create(int userId)
    {
        if (userId <= 0)
            throw new ArgumentOutOfRangeException(nameof(userId), "User ID must be positive.");

        var payload = Encoding.UTF8.GetBytes(
            $"{userId}:{DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds()}");

        var encodedPayload = Base64UrlEncode(payload);
        var signature = HMACSHA256.HashData(_secret, Encoding.ASCII.GetBytes(encodedPayload));

        return $"{encodedPayload}.{Base64UrlEncode(signature)}";
    }

    public bool TryGet(string token, out int userId)
    {
        userId = 0;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2)
                return false;

            var suppliedSignature = Base64UrlDecode(parts[1]);
            var expectedSignature = HMACSHA256.HashData(
                _secret,
                Encoding.ASCII.GetBytes(parts[0]));

            if (!CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
                return false;

            var fields = Encoding.UTF8.GetString(Base64UrlDecode(parts[0])).Split(':');
            if (fields.Length != 2 ||
                !int.TryParse(fields[0], out var parsedUserId) || parsedUserId <= 0 ||
                !long.TryParse(fields[1], out var expiresAt) ||
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresAt)
            {
                return false;
            }

            userId = parsedUserId;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }
}
