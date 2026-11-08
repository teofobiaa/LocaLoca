using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LocaLoca.Api.Tests;

public sealed class LocaLocaApiFactory : WebApplicationFactory<Program>
{
    private const string TestSessionSecret = "LocaLoca.Api.Tests.Secret.Only.Not.Production";
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(),
        "LocaLoca.Api.Tests",
        Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PORT"] = string.Empty,
                ["LOCA_DB_CONNECTION"] = "CHANGE_ME",
                ["LOCA_SESSION_SECRET"] = TestSessionSecret,
                ["LOCA_DATA_ROOT"] = _dataRoot
            });
        });
    }

    public string CreateSessionToken(int userId)
    {
        var payload = Encoding.UTF8.GetBytes(
            $"{userId}:{DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()}");
        var encodedPayload = Base64UrlEncode(payload);
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(TestSessionSecret),
            Encoding.ASCII.GetBytes(encodedPayload));

        return $"{encodedPayload}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing || !Directory.Exists(_dataRoot))
            return;

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
            // A failed cleanup must not hide the actual test result.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup must not hide the actual test result.
        }
    }
}
