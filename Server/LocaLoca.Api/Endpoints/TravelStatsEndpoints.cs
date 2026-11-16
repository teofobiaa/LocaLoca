using System.Data;
using Npgsql;
using NpgsqlTypes;
using LocaLoca.Api;
using LocaLoca.Api.Security;
using static LocaLoca.Api.ApiEndpointHelpers;

namespace LocaLoca.Api.Endpoints;

public static class TravelStatsEndpoints
{
    // A common projection keeps each aggregate aligned. The queries share one repeatable-read snapshot.
    private const string EventsCte = @"
WITH events AS (
    SELECT
        v.visit_date,
        'catalog:' || v.attraction_id::text AS place_key,
        city.city_id,
        country.country_id,
        category.name AS category_name,
        false AS is_personal
    FROM public.visits AS v
    JOIN public.attractions AS attraction ON attraction.attraction_id = v.attraction_id
    JOIN public.categories AS category ON category.category_id = attraction.category_id
    JOIN public.cities AS city ON city.city_id = attraction.city_id
    JOIN public.countries AS country ON country.country_id = city.country_id
    WHERE v.user_id = @user
      AND v.visit_date >= @start_date
      AND v.visit_date < @end_date

    UNION ALL

    SELECT
        personal.visit_date,
        personal.place_key,
        NULL::integer AS city_id,
        NULL::integer AS country_id,
        personal.category_name,
        true AS is_personal
    FROM public.personal_place_visits AS personal
    WHERE personal.user_id = @user
      AND personal.visit_date >= @start_date
      AND personal.visit_date < @end_date
)";

    public static void MapTravelStatsEndpoints(this WebApplication app, string? connectionString)
    {
        app.MapGet("/api/me/travel-stats", async Task<IResult> (
            HttpContext context,
            SessionStore sessions,
            int? year,
            CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId))
                return Results.Unauthorized();

            var requestedYear = year ?? DateTime.UtcNow.Year;
            if (requestedYear is < 1900 or > 2200)
                return Results.BadRequest(new ApiErrorResponse("Год должен находиться в диапазоне от 1900 до 2200."));

            var startDate = new DateOnly(requestedYear, 1, 1);
            var endDate = startDate.AddYears(1);
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken);

            long visitCount;
            long uniquePlaces;
            long cities;
            long countries;
            long personalVisits;

            await using (var summary = new NpgsqlCommand($@"
{EventsCte}
SELECT
    count(*)::bigint,
    count(DISTINCT place_key)::bigint,
    count(DISTINCT city_id)::bigint,
    count(DISTINCT country_id)::bigint,
    count(*) FILTER (WHERE is_personal)::bigint
FROM events", connection, transaction))
            {
                AddEventParameters(summary, userId, startDate, endDate);
                await using var reader = await summary.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                visitCount = reader.GetInt64(0);
                uniquePlaces = reader.GetInt64(1);
                cities = reader.GetInt64(2);
                countries = reader.GetInt64(3);
                personalVisits = reader.GetInt64(4);
            }

            var months = new List<MonthlyTravelStatsDto>(12);
            await using (var monthly = new NpgsqlCommand($@"
{EventsCte}
SELECT month_numbers.month_number, count(events.visit_date)::bigint
FROM generate_series(1, 12) AS month_numbers(month_number)
LEFT JOIN events
    ON extract(month FROM events.visit_date)::integer = month_numbers.month_number
GROUP BY month_numbers.month_number
ORDER BY month_numbers.month_number", connection, transaction))
            {
                AddEventParameters(monthly, userId, startDate, endDate);
                await using var reader = await monthly.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    months.Add(new MonthlyTravelStatsDto(reader.GetInt32(0), reader.GetInt64(1)));
            }

            var categories = new List<CategoryTravelStatsDto>();
            await using (var byCategory = new NpgsqlCommand($@"
{EventsCte}
SELECT category_name, count(*)::bigint AS visit_count
FROM events
GROUP BY category_name
ORDER BY visit_count DESC, category_name", connection, transaction))
            {
                AddEventParameters(byCategory, userId, startDate, endDate);
                await using var reader = await byCategory.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    categories.Add(new CategoryTravelStatsDto(reader.GetString(0), reader.GetInt64(1)));
            }

            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new AnnualTravelStatsDto(
                requestedYear, visitCount, uniquePlaces, cities, countries,
                personalVisits, months, categories));
        });

        app.MapPost("/api/me/map-places/{placeId:int}/visits", async Task<IResult> (
            int placeId,
            PersonalPlaceVisitWriteRequest request,
            HttpContext context,
            SessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId))
                return Results.Unauthorized();

            if (placeId <= 0)
                return Results.BadRequest(new ApiErrorResponse("Некорректный идентификатор личной точки."));
            if (!DateOnly.TryParse(request.VisitDate, out var visitDate) ||
                visitDate > DateOnly.FromDateTime(DateTime.Today))
                return Results.BadRequest(new ApiErrorResponse("Укажите корректную дату посещения, не позднее сегодняшнего дня."));
            if (request.Rating is < 1 or > 5)
                return Results.BadRequest(new ApiErrorResponse("Оценка должна быть от 1 до 5."));

            var impressions = request.Impressions ?? string.Empty;
            var notes = request.Notes ?? string.Empty;
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using var insert = new NpgsqlCommand(@"
INSERT INTO public.personal_place_visits (
    user_id, user_map_place_id, place_key, place_name, latitude, longitude,
    visit_date, rating, category_name, impressions, notes
)
SELECT
    point.user_id, point.user_map_place_id,
    'personal:' || point.user_map_place_id::text,
    point.name, point.latitude, point.longitude,
    @visit_date, @rating, 'Личные точки', @impressions, @notes
FROM public.user_map_places AS point
WHERE point.user_map_place_id = @place_id
  AND point.user_id = @user
RETURNING personal_place_visit_id", connection, transaction);
            insert.Parameters.AddWithValue("visit_date", NpgsqlDbType.Date, visitDate);
            insert.Parameters.AddWithValue("rating", NpgsqlDbType.Smallint, (short)request.Rating);
            insert.Parameters.AddWithValue("impressions", NpgsqlDbType.Text, impressions);
            insert.Parameters.AddWithValue("notes", NpgsqlDbType.Text, notes);
            insert.Parameters.AddWithValue("place_id", NpgsqlDbType.Integer, placeId);
            insert.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            var insertedId = await insert.ExecuteScalarAsync(cancellationToken);
            if (insertedId is null)
                return Results.NotFound(new ApiErrorResponse("Личная точка не найдена."));

            // Keep legacy marker fields aligned with the latest visit. The trigger updates
            // the newly inserted latest snapshot and does not overwrite earlier events.
            await using var updatePoint = new NpgsqlCommand(@"
UPDATE public.user_map_places
SET status = 'visited',
    visit_date = @visit_date,
    rating = @rating,
    impressions = @impressions,
    notes = @notes
WHERE user_map_place_id = @place_id AND user_id = @user", connection, transaction);
            updatePoint.Parameters.AddWithValue("visit_date", NpgsqlDbType.Date, visitDate);
            updatePoint.Parameters.AddWithValue("rating", NpgsqlDbType.Smallint, (short)request.Rating);
            updatePoint.Parameters.AddWithValue("impressions", NpgsqlDbType.Text, impressions);
            updatePoint.Parameters.AddWithValue("notes", NpgsqlDbType.Text, notes);
            updatePoint.Parameters.AddWithValue("place_id", NpgsqlDbType.Integer, placeId);
            updatePoint.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            await updatePoint.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new PersonalPlaceVisitSavedDto(
                Convert.ToInt32(insertedId), "Повторное посещение сохранено."));
        });
    }

    private static void AddEventParameters(
        NpgsqlCommand command, int userId, DateOnly startDate, DateOnly endDate)
    {
        command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
        command.Parameters.AddWithValue("start_date", NpgsqlDbType.Date, startDate);
        command.Parameters.AddWithValue("end_date", NpgsqlDbType.Date, endDate);
    }
}
