using Npgsql;
using NpgsqlTypes;
using LocaLoca.Api;
using LocaLoca.Api.Security;
using static LocaLoca.Api.ApiEndpointHelpers;

namespace LocaLoca.Api.Endpoints;

public static class LocalRecommendationEndpoints
{
    private const int DefaultTake = 8;
    private const int MaxTake = 20;

    public static void MapLocalRecommendationEndpoints(this WebApplication app, string? connectionString)
    {
        app.MapGet("/api/me/recommendations", async Task<IResult> (
            int? take,
            HttpContext context,
            SessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId))
                return Results.Unauthorized();

            var pageSize = take ?? DefaultTake;
            if (pageSize is < 1 or > MaxTake)
                return Results.BadRequest(new ApiErrorResponse(
                    $"Количество рекомендаций должно быть от 1 до {MaxTake}."));

            const string sql = @"
SELECT a.attraction_id, a.name, a.description, COALESCE(a.history, ''),
       a.latitude, a.longitude, cat.name, ci.name, co.name
FROM users u
JOIN countries co
  ON lower(btrim(co.name)) = lower(btrim(u.residence_country))
JOIN cities ci
  ON ci.country_id = co.country_id
JOIN attractions a
  ON a.city_id = ci.city_id
JOIN categories cat
  ON cat.category_id = a.category_id
WHERE u.user_id = @user
  AND NULLIF(btrim(u.residence_country), '') IS NOT NULL
  AND (
      u.date_of_birth IS NULL
      OR (
          (a.min_recommended_age IS NULL
           OR a.min_recommended_age <= EXTRACT(YEAR FROM age(CURRENT_DATE, u.date_of_birth))::integer)
          AND
          (a.max_recommended_age IS NULL
           OR a.max_recommended_age >= EXTRACT(YEAR FROM age(CURRENT_DATE, u.date_of_birth))::integer)
      )
  )
  AND NOT EXISTS (
      SELECT 1
      FROM visits v
      WHERE v.user_id = u.user_id
        AND v.attraction_id = a.attraction_id
  )
ORDER BY
    CASE
        WHEN NULLIF(btrim(u.residence_city), '') IS NOT NULL
         AND lower(btrim(ci.name)) = lower(btrim(u.residence_city)) THEN 0
        ELSE 1
    END,
    a.name,
    a.attraction_id
LIMIT @take";

            var items = new List<AttractionDto>();
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            command.Parameters.AddWithValue("take", NpgsqlDbType.Integer, pageSize);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new AttractionDto(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    (double)reader.GetDecimal(4),
                    (double)reader.GetDecimal(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8)));
            }

            return Results.Ok(new ItemsResponse<AttractionDto>(items));
        });
    }
}
