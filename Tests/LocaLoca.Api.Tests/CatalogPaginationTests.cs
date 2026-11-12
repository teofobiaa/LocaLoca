using System.Net;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace LocaLoca.Api.Tests;

[Collection(PostgreSqlApiCollection.Name)]
public sealed class CatalogPaginationTests(PostgreSqlApiFixture database)
{
    [Fact]
    public async Task Catalog_returns_only_the_requested_page_with_paging_metadata()
    {
        await InsertAttractionAsync("Second API catalog attraction");

        using var factory = database.CreateFactory();
        using var client = factory.CreateClient();

        using var firstResponse = await client.GetAsync("/api/catalog?page=1&pageSize=1");
        using var firstDocument = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = firstDocument.RootElement;
        Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Equal(1, first.GetProperty("page").GetInt32());
        Assert.Equal(1, first.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, first.GetProperty("totalCount").GetInt64());
        Assert.True(first.GetProperty("hasMore").GetBoolean());

        using var secondResponse = await client.GetAsync("/api/catalog?page=2&pageSize=1");
        using var secondDocument = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = secondDocument.RootElement;
        Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.Equal(2, second.GetProperty("page").GetInt32());
        Assert.Equal(2, second.GetProperty("totalCount").GetInt64());
        Assert.False(second.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("/api/catalog?page=0&pageSize=10")]
    [InlineData("/api/catalog?page=1&pageSize=101")]
    public async Task Catalog_rejects_invalid_paging_parameters(string path)
    {
        using var factory = database.CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Catalog_lookup_by_ids_returns_only_the_requested_attractions()
    {
        var secondId = await InsertAttractionAsync("Lookup-only API catalog attraction");

        using var factory = database.CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/catalog/by-ids?ids={database.AttractionId}&ids={secondId}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Contains(items, item => item.GetProperty("id").GetInt32() == database.AttractionId);
        Assert.Contains(items, item => item.GetProperty("id").GetInt32() == secondId);
    }

    [Fact]
    public async Task Catalog_lookup_by_ids_rejects_more_than_500_ids()
    {
        using var factory = database.CreateFactory();
        using var client = factory.CreateClient();
        var query = string.Join("&", Enumerable.Range(1, 501).Select(id => $"ids={id}"));

        using var response = await client.GetAsync("/api/catalog/by-ids?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<int> InsertAttractionAsync(string name)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"
INSERT INTO attractions(name, description, history, latitude, longitude, category_id, city_id)
SELECT @name, 'Synthetic catalog test fixture', '', 11, 21, cat.category_id, ci.city_id
FROM categories cat
CROSS JOIN cities ci
WHERE cat.name = 'API test category'
  AND ci.name = 'API test city'
LIMIT 1
RETURNING attraction_id", connection);
        command.Parameters.AddWithValue("name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
