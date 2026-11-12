using Npgsql;
using NpgsqlTypes;
using LocaLoca.Api;
using LocaLoca.Api.Security;
using static LocaLoca.Api.ApiEndpointHelpers;
namespace LocaLoca.Api.Endpoints;

public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this WebApplication app, string? connectionString)
    {
        const int DefaultPageSize = 50;
        const int MaxPageSize = 100;
        const int MaxLookupIds = 500;
        
        app.MapGet("/api/catalog/categories", async Task<IResult> (CancellationToken cancellationToken) =>
        {
            var items = new List<CategoryDto>();
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand("SELECT category_id, name FROM categories ORDER BY name", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) items.Add(new CategoryDto(reader.GetInt32(0), reader.GetString(1)));
            return Results.Ok(new ItemsResponse<CategoryDto>(items));
        });
        
        
        app.MapGet("/api/catalog/by-ids", async Task<IResult> (
            int[]? ids, CancellationToken cancellationToken) =>
        {
            if (ids is null || ids.Length == 0 || ids.Length > MaxLookupIds || ids.Any(id => id <= 0))
                return Results.BadRequest(new ApiErrorResponse(
                    $"Для поиска по ID укажи от 1 до {MaxLookupIds} положительных идентификаторов."));

            var uniqueIds = ids.Distinct().ToArray();
            const string sql = @"
        SELECT a.attraction_id, a.name, a.description, COALESCE(a.history, ''),
               a.latitude, a.longitude, cat.name, ci.name, co.name
        FROM attractions a
        JOIN categories cat ON cat.category_id = a.category_id
        JOIN cities ci ON ci.city_id = a.city_id
        JOIN countries co ON co.country_id = ci.country_id
        WHERE a.attraction_id = ANY(@ids)
        ORDER BY co.name, ci.name, a.name, a.attraction_id";

            var items = new List<AttractionDto>();
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, uniqueIds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add(new AttractionDto(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    (double)reader.GetDecimal(4), (double)reader.GetDecimal(5), reader.GetString(6), reader.GetString(7), reader.GetString(8)));

            return Results.Ok(new ItemsResponse<AttractionDto>(items));
        });


        app.MapGet("/api/catalog", async Task<IResult> (
            string? search, int? categoryId, int? page, int? pageSize, CancellationToken cancellationToken) =>
        {
            var currentPage = page ?? 1;
            var currentPageSize = pageSize ?? DefaultPageSize;
            if (currentPage < 1 || currentPageSize is < 1 or > MaxPageSize)
                return Results.BadRequest(new ApiErrorResponse($"Параметры пагинации должны задавать страницу от 1 и размер от 1 до {MaxPageSize}."));
        
            var offset = ((long)currentPage - 1) * currentPageSize;
            var normalizedSearch = search?.Trim() ?? string.Empty;
            const string filters = @"
        FROM attractions a
        JOIN categories cat ON cat.category_id = a.category_id
        JOIN cities ci ON ci.city_id = a.city_id
        JOIN countries co ON co.country_id = ci.country_id
        WHERE (@q = '' OR a.name ILIKE '%' || @q || '%' OR ci.name ILIKE '%' || @q || '%' OR co.name ILIKE '%' || @q || '%')
          AND (@cat IS NULL OR a.category_id = @cat)";
        
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var countCommand = new NpgsqlCommand("SELECT count(*) " + filters, connection);
            countCommand.Parameters.AddWithValue("q", NpgsqlDbType.Text, normalizedSearch);
            countCommand.Parameters.AddWithValue("cat", NpgsqlDbType.Integer, (object?)categoryId ?? DBNull.Value);
            var totalCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));
        
            var sql = @"
        SELECT a.attraction_id, a.name, a.description, COALESCE(a.history, ''),
               a.latitude, a.longitude, cat.name, ci.name, co.name
        " + filters + @"
        ORDER BY co.name, ci.name, a.name, a.attraction_id
        LIMIT @limit OFFSET @offset";
            var items = new List<AttractionDto>();
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("q", NpgsqlDbType.Text, normalizedSearch);
            command.Parameters.AddWithValue("cat", NpgsqlDbType.Integer, (object?)categoryId ?? DBNull.Value);
            command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, currentPageSize);
            command.Parameters.AddWithValue("offset", NpgsqlDbType.Bigint, offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add(new AttractionDto(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    (double)reader.GetDecimal(4), (double)reader.GetDecimal(5), reader.GetString(6), reader.GetString(7), reader.GetString(8)));
        
            return Results.Ok(new PagedItemsResponse<AttractionDto>(
                items, currentPage, currentPageSize, totalCount, (long)currentPage * currentPageSize < totalCount));
        });
    }
}
