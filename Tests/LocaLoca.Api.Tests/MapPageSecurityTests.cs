using System.Net;
using Xunit;

namespace LocaLoca.Api.Tests;

[Collection(PostgreSqlApiCollection.Name)]
public sealed class MapPageSecurityTests
{
    private readonly PostgreSqlApiFixture _database;

    public MapPageSecurityTests(PostgreSqlApiFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Map_Page_Uses_Local_Assets_And_Restrictive_Csp()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/map");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var values));
        var csp = string.Join(" ", values!);
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("/map-bootstrap.js", html);
        Assert.Contains("/leaflet/leaflet.js", html);
        Assert.Contains("/map.js", html);
        Assert.DoesNotContain("unpkg.com", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Map_Page_Rejects_Legacy_Query_Tokens_Without_Reflecting_Them()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();
        const string token = "legacy-token-must-not-be-reflected";

        using var response = await client.GetAsync("/map?theme=dark&token=" + token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Map_JavaScript_Does_Not_Read_Or_Send_Bearer_Tokens()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var script = await client.GetStringAsync("/map.js");

        Assert.DoesNotContain("sessionStorage", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localStorage", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("location.hash", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Leaflet_And_Map_Assets_Are_Served_From_The_Api_Origin()
    {
        using var factory = _database.CreateFactory();
        using var client = factory.CreateClient();

        var assets = new[]
        {
            ("/map-bootstrap.js", "text/javascript"),
            ("/map.js", "text/javascript"),
            ("/map.css", "text/css"),
            ("/leaflet/leaflet.js", "text/javascript"),
            ("/leaflet/leaflet.css", "text/css"),
            ("/leaflet/images/layers.png", "image/png"),
            ("/leaflet/images/marker-icon.png", "image/png")
        };

        foreach (var (path, contentType) in assets)
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        }

        var leaflet = await client.GetStringAsync("/leaflet/leaflet.js");
        Assert.Contains("Leaflet 1.9.4", leaflet);
    }
}
