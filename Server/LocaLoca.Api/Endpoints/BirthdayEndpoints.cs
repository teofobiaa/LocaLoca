using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using LocaLoca.Api;
using LocaLoca.Api.Security;
using static LocaLoca.Api.ApiEndpointHelpers;

namespace LocaLoca.Api.Endpoints;

public static class BirthdayEndpoints
{
    private static readonly DateOnly EarliestBirthDate = new(1900, 1, 1);

    public static void MapBirthdayEndpoints(this WebApplication app, string? connectionString)
    {
        // The full date of birth is returned only by this owner-authenticated endpoint.
        // It is intentionally absent from UserDto, authentication responses and public routes.
        app.MapGet("/api/me/profile/birthday", async Task<IResult> (
            HttpContext context,
            SessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
            context.Response.Headers["Cache-Control"] = "private, no-store";

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT date_of_birth FROM public.users WHERE user_id = @user", connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is null) return Results.NotFound(new ApiErrorResponse("Аккаунт не найден."));

            return Results.Ok(new PrivateBirthDateDto(value is DBNull ? null : ReadNullableDate(value)));
        });

        app.MapPut("/api/me/profile/birthday", async Task<IResult> (
            UpdateBirthDateRequest request,
            HttpContext context,
            SessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
            context.Response.Headers["Cache-Control"] = "private, no-store";

            var today = GetUserToday(context);
            if (request.DateOfBirth is { } date &&
                (date < EarliestBirthDate || date > today))
            {
                return Results.BadRequest(new ApiErrorResponse(
                    $"Дата рождения должна находиться между {EarliestBirthDate:yyyy-MM-dd} и сегодняшним днём."));
            }

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                UPDATE public.users
                SET date_of_birth = @birth_date
                WHERE user_id = @user
                RETURNING date_of_birth", connection);
            command.Parameters.Add("birth_date", NpgsqlDbType.Date).Value =
                request.DateOfBirth is { } birthDate ? birthDate : DBNull.Value;
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);

            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is null) return Results.NotFound(new ApiErrorResponse("Аккаунт не найден."));

            return Results.Ok(new PrivateBirthDateDto(
                value is DBNull ? null : ReadNullableDate(value)));
        });

    }

    private static DateOnly? ReadNullableDate(object? value)
    {
        if (value is null || value is DBNull) return null;
        return value switch
        {
            DateOnly date => date,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => DateOnly.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)
        };
    }
}
