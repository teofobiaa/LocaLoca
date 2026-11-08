using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LocaLoca.Api.Tests;

public sealed class LocaLocaApiFactory : WebApplicationFactory<Program>
{
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
                ["LOCA_SESSION_SECRET"] = "LocaLoca.Api.Tests.Secret.Only.Not.Production",
                ["LOCA_DATA_ROOT"] = _dataRoot
            });
        });
    }

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
