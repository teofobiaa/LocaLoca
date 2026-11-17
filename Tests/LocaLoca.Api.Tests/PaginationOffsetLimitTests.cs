using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using LocaLoca.Api;
using LocaLoca.Api.Endpoints;
using LocaLoca.Api.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LocaLoca.Api.Tests;

public sealed class PaginationOffsetLimitTests
{
    [Fact]
    public async Task Pagination_offset_limit_is_inclusive_and_rejects_excessive_pages_before_database_access()
    {
        await using var app = await CreateTestAppAsync();
        using var client = app.GetTestClient();

        // An exact offset of 100,000 is valid. The intentionally absent database
        // makes the request return 503 only after it passes pagination validation.
        var boundaryRequests = new[]
        {
            "/api/me/visits?page=1001&pageSize=100",
            "/api/me/wishlist?page=1001&pageSize=100",
            "/api/me/map-markers?page=101&pageSize=1000",
            "/api/catalog?page=1001&pageSize=100"
        };

        foreach (var request in boundaryRequests)
        {
            using var response = await client.GetAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        // The first offset above the limit and the largest bindable Int32 page
        // must be rejected without attempting to open the database.
        var rejectedRequests = new[]
        {
            "/api/me/visits?page=1002&pageSize=100",
            "/api/me/wishlist?page=1002&pageSize=100",
            "/api/me/map-markers?page=102&pageSize=1000",
            "/api/catalog?page=1002&pageSize=100",
            "/api/me/visits?page=2147483647&pageSize=100",
            "/api/me/wishlist?page=2147483647&pageSize=100",
            "/api/me/map-markers?page=2147483647&pageSize=1000",
            "/api/catalog?page=2147483647&pageSize=100"
        };

        foreach (var request in rejectedRequests)
        {
            using var response = await client.GetAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("100000", body);
        }
    }

    private static async Task<WebApplication> CreateTestAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["LOCA_SESSION_SECRET"] = "pagination-offset-test-secret-0123456789";
        builder.Services.AddSingleton<SessionStore>();

        var app = builder.Build();
        app.UseMiddleware<ApiExceptionMiddleware>();
        app.Use(async (context, next) =>
        {
            // Exercise the authenticated endpoint path without requiring a
            // database-backed session lookup in this focused validation test.
            context.Items["LocaLoca.UserId"] = 1;
            await next();
        });

        string? noDatabaseConnection = null;
        app.MapCatalogEndpoints(noDatabaseConnection);
        app.MapPersonalEndpoints(noDatabaseConnection, Path.GetTempPath());
        app.MapUserMapEndpoints(noDatabaseConnection, Path.GetTempPath());

        await app.StartAsync();
        return app;
    }
}
