using Npgsql;
using NpgsqlTypes;
using LocaLoca.Api;
using static LocaLoca.Api.ApiEndpointHelpers;

namespace LocaLoca.Api.Endpoints;

public static class ProfilePrivacyEndpoints
{
    private static readonly string[] AllowedVisibilityValues = ["private", "friends", "registered"];

    public static void MapProfilePrivacyEndpoints(this WebApplication app, string? connectionString)
    {
        app.MapGet("/api/me/profile/privacy", async Task<IResult> (
            HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await EnsurePrivacyRowAsync(connection, userId, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                SELECT display_name_visibility, avatar_visibility,
                       residence_country_visibility, residence_city_visibility
                FROM public.user_profile_visibility
                WHERE user_id = @user", connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return Results.NotFound(new ApiErrorResponse("Настройки приватности не найдены."));

            context.Response.Headers["Cache-Control"] = "private, no-store";
            return Results.Ok(ReadPrivacy(reader));
        });

        app.MapPut("/api/me/profile/privacy", async Task<IResult> (
            UpdateProfilePrivacyRequest request,
            HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();

            if (!IsAllowed(request.DisplayNameVisibility) ||
                !IsAllowed(request.AvatarVisibility) ||
                !IsAllowed(request.ResidenceCountryVisibility) ||
                !IsAllowed(request.ResidenceCityVisibility))
            {
                return Results.BadRequest(new ApiErrorResponse(
                    "Для каждого поля выбери один из вариантов: private, friends или registered."));
            }

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await EnsurePrivacyRowAsync(connection, userId, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                UPDATE public.user_profile_visibility
                SET display_name_visibility = @display_name,
                    avatar_visibility = @avatar,
                    residence_country_visibility = @country,
                    residence_city_visibility = @city,
                    updated_at = now()
                WHERE user_id = @user
                RETURNING display_name_visibility, avatar_visibility,
                          residence_country_visibility, residence_city_visibility", connection);
            command.Parameters.AddWithValue("display_name", NpgsqlDbType.Varchar, request.DisplayNameVisibility!);
            command.Parameters.AddWithValue("avatar", NpgsqlDbType.Varchar, request.AvatarVisibility!);
            command.Parameters.AddWithValue("country", NpgsqlDbType.Varchar, request.ResidenceCountryVisibility!);
            command.Parameters.AddWithValue("city", NpgsqlDbType.Varchar, request.ResidenceCityVisibility!);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return Results.NotFound(new ApiErrorResponse("Аккаунт не найден."));

            context.Response.Headers["Cache-Control"] = "private, no-store";
            return Results.Ok(ReadPrivacy(reader));
        });

        app.MapGet("/api/users/{profileUserId:int}/profile", async Task<IResult> (
            int profileUserId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var viewerId)) return Results.Unauthorized();
            if (profileUserId <= 0) return Results.NotFound();

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                WITH target AS (
                    SELECT u.user_id,
                           u.display_name,
                           u.residence_country,
                           u.residence_city,
                           COALESCE(v.display_name_visibility, 'registered') AS display_name_visibility,
                           COALESCE(v.avatar_visibility, 'registered') AS avatar_visibility,
                           COALESCE(v.residence_country_visibility, 'private') AS residence_country_visibility,
                           COALESCE(v.residence_city_visibility, 'private') AS residence_city_visibility,
                           (@viewer = u.user_id) AS is_owner,
                           EXISTS (
                               SELECT 1
                               FROM public.user_friendships f
                               WHERE f.status = 'accepted'
                                 AND f.user_low_id = LEAST(@viewer, u.user_id)
                                 AND f.user_high_id = GREATEST(@viewer, u.user_id)
                           ) AS is_friend
                    FROM public.users u
                    LEFT JOIN public.user_profile_visibility v ON v.user_id = u.user_id
                    WHERE u.user_id = @profile
                      AND NOT EXISTS (
                          SELECT 1 FROM public.user_blocks b
                          WHERE (b.blocker_user_id = @viewer AND b.blocked_user_id = u.user_id)
                             OR (b.blocker_user_id = u.user_id AND b.blocked_user_id = @viewer)
                      )
                )
                SELECT user_id,
                       CASE WHEN is_owner OR display_name_visibility = 'registered'
                                  OR (display_name_visibility = 'friends' AND is_friend)
                            THEN display_name ELSE NULL END,
                       CASE WHEN is_owner OR residence_country_visibility = 'registered'
                                  OR (residence_country_visibility = 'friends' AND is_friend)
                            THEN residence_country ELSE NULL END,
                       CASE WHEN is_owner OR residence_city_visibility = 'registered'
                                  OR (residence_city_visibility = 'friends' AND is_friend)
                            THEN residence_city ELSE NULL END,
                       CASE WHEN is_owner OR avatar_visibility = 'registered'
                                  OR (avatar_visibility = 'friends' AND is_friend)
                            THEN EXISTS (SELECT 1 FROM public.user_avatars a WHERE a.user_id = target.user_id)
                            ELSE NULL END
                FROM target", connection);
            command.Parameters.AddWithValue("viewer", NpgsqlDbType.Integer, viewerId);
            command.Parameters.AddWithValue("profile", NpgsqlDbType.Integer, profileUserId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return Results.NotFound(new ApiErrorResponse("Профиль не найден."));

            context.Response.Headers["Cache-Control"] = "private, no-store";
            return Results.Ok(new UserProfileViewDto(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetBoolean(4)));
        });

        app.MapGet("/api/users/{profileUserId:int}/avatar", async Task<IResult> (
            int profileUserId, string? size, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var viewerId)) return Results.Unauthorized();
            if (profileUserId <= 0) return Results.NotFound();

            var thumbnail = string.Equals(size, "thumbnail", StringComparison.OrdinalIgnoreCase);
            if (size is not null &&
                !string.Equals(size, "thumbnail", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(size, "full", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new ApiErrorResponse("Параметр size должен быть full или thumbnail."));

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                SELECT CASE WHEN
                    @viewer = u.user_id
                    OR COALESCE(v.avatar_visibility, 'registered') = 'registered'
                    OR (COALESCE(v.avatar_visibility, 'registered') = 'friends' AND EXISTS (
                        SELECT 1 FROM public.user_friendships f
                        WHERE f.status = 'accepted'
                          AND f.user_low_id = LEAST(@viewer, u.user_id)
                          AND f.user_high_id = GREATEST(@viewer, u.user_id)
                    ))
                    THEN CASE WHEN @thumbnail THEN a.thumbnail_data ELSE a.image_data END
                    ELSE NULL END
                FROM public.users u
                LEFT JOIN public.user_profile_visibility v ON v.user_id = u.user_id
                LEFT JOIN public.user_avatars a ON a.user_id = u.user_id
                WHERE u.user_id = @profile
                  AND NOT EXISTS (
                      SELECT 1 FROM public.user_blocks b
                      WHERE (b.blocker_user_id = @viewer AND b.blocked_user_id = u.user_id)
                         OR (b.blocker_user_id = u.user_id AND b.blocked_user_id = @viewer)
                  )", connection);
            command.Parameters.AddWithValue("viewer", NpgsqlDbType.Integer, viewerId);
            command.Parameters.AddWithValue("profile", NpgsqlDbType.Integer, profileUserId);
            command.Parameters.AddWithValue("thumbnail", NpgsqlDbType.Boolean, thumbnail);

            var image = await command.ExecuteScalarAsync(cancellationToken) as byte[];
            if (image is null) return Results.NotFound(new ApiErrorResponse("Аватар недоступен."));

            context.Response.Headers["Cache-Control"] = "private, no-store";
            return Results.File(image, "image/jpeg");
        });
    }

    private static bool IsAllowed(string? visibility) =>
        visibility is not null && AllowedVisibilityValues.Contains(visibility, StringComparer.Ordinal);

    private static async Task EnsurePrivacyRowAsync(
        NpgsqlConnection connection, int userId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
            INSERT INTO public.user_profile_visibility(user_id)
            VALUES (@user)
            ON CONFLICT (user_id) DO NOTHING", connection);
        command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ProfilePrivacyDto ReadPrivacy(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3));
}
