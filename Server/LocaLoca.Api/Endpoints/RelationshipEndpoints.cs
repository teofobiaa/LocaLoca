using Npgsql;
using NpgsqlTypes;
using LocaLoca.Api;
using static LocaLoca.Api.ApiEndpointHelpers;

namespace LocaLoca.Api.Endpoints;

public static class RelationshipEndpoints
{
    public static void MapRelationshipEndpoints(this WebApplication app, string? connectionString)
    {
        app.MapPost("/api/users/{targetUserId:int}/friend-request", async Task<IResult> (
            int targetUserId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
            if (targetUserId <= 0 || targetUserId == userId)
                return Results.BadRequest(new ApiErrorResponse("Нельзя отправить запрос дружбы самому себе."));

            var (lowId, highId) = OrderedPair(userId, targetUserId);
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var targetCheck = new NpgsqlCommand(@"
                SELECT EXISTS (SELECT 1 FROM public.users WHERE user_id = @target)
                   AND NOT EXISTS (
                       SELECT 1 FROM public.user_blocks
                       WHERE (blocker_user_id = @user AND blocked_user_id = @target)
                          OR (blocker_user_id = @target AND blocked_user_id = @user)
                   )", connection, transaction))
            {
                targetCheck.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
                targetCheck.Parameters.AddWithValue("target", NpgsqlDbType.Integer, targetUserId);
                if (!(bool)(await targetCheck.ExecuteScalarAsync(cancellationToken) ?? false))
                    return Results.NotFound(new ApiErrorResponse("Пользователь не найден."));
            }

            await using var command = new NpgsqlCommand(@"
                INSERT INTO public.user_friendships AS existing (
                    user_low_id, user_high_id, requested_by_user_id, status)
                VALUES (@low, @high, @requester, 'pending')
                ON CONFLICT (user_low_id, user_high_id) DO UPDATE
                SET status = CASE
                        WHEN existing.status = 'pending'
                         AND existing.requested_by_user_id <> EXCLUDED.requested_by_user_id
                        THEN 'accepted'
                        ELSE public.user_friendships.status
                    END,
                    updated_at = now()
                RETURNING status", connection, transaction);
            command.Parameters.AddWithValue("low", NpgsqlDbType.Integer, lowId);
            command.Parameters.AddWithValue("high", NpgsqlDbType.Integer, highId);
            command.Parameters.AddWithValue("requester", NpgsqlDbType.Integer, userId);

            string status;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                status = reader.GetString(0);
            }

            await transaction.CommitAsync(cancellationToken);
            var message = status == "accepted"
                ? "Вы теперь друзья."
                : "Запрос дружбы отправлен.";
            return Results.Ok(new MessageDto(message));
        });

        app.MapGet("/api/me/friend-requests", async Task<IResult> (
            HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                SELECT u.user_id,
                       f.requested_by_user_id <> @user AS is_incoming,
                       f.created_at
                FROM public.user_friendships f
                JOIN public.users u ON u.user_id =
                    CASE WHEN f.user_low_id = @user THEN f.user_high_id ELSE f.user_low_id END
                WHERE f.status = 'pending'
                  AND (f.user_low_id = @user OR f.user_high_id = @user)
                ORDER BY f.created_at DESC, u.user_id", connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);

            var items = new List<FriendRequestDto>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add(new FriendRequestDto(
                    reader.GetInt32(0),
                    reader.GetBoolean(1),
                    reader.GetDateTime(2)));

            return Results.Ok(new ItemsResponse<FriendRequestDto>(items));
        });

        app.MapPost("/api/me/friend-requests/{otherUserId:int}/accept", async Task<IResult> (
            int otherUserId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
            if (otherUserId <= 0 || otherUserId == userId)
                return Results.BadRequest(new ApiErrorResponse("Некорректный запрос дружбы."));

            var (lowId, highId) = OrderedPair(userId, otherUserId);
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                UPDATE public.user_friendships
                SET status = 'accepted', updated_at = now()
                WHERE user_low_id = @low AND user_high_id = @high
                  AND status = 'pending' AND requested_by_user_id = @other
                  AND NOT EXISTS (
                      SELECT 1 FROM public.user_blocks
                      WHERE (blocker_user_id = @user AND blocked_user_id = @other)
                         OR (blocker_user_id = @other AND blocked_user_id = @user)
                  )", connection);
            command.Parameters.AddWithValue("low", NpgsqlDbType.Integer, lowId);
            command.Parameters.AddWithValue("high", NpgsqlDbType.Integer, highId);
            command.Parameters.AddWithValue("other", NpgsqlDbType.Integer, otherUserId);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);

            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? Results.Ok(new MessageDto("Запрос дружбы принят."))
                : Results.NotFound(new ApiErrorResponse("Входящий запрос дружбы не найден."));
        });

        app.MapDelete("/api/me/friend-requests/{otherUserId:int}", async Task<IResult> (
            int otherUserId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
            if (otherUserId <= 0 || otherUserId == userId)
                return Results.BadRequest(new ApiErrorResponse("Некорректный запрос дружбы."));

            var (lowId, highId) = OrderedPair(userId, otherUserId);
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                DELETE FROM public.user_friendships
                WHERE user_low_id = @low AND user_high_id = @high AND status = 'pending'", connection);
            command.Parameters.AddWithValue("low", NpgsqlDbType.Integer, lowId);
            command.Parameters.AddWithValue("high", NpgsqlDbType.Integer, highId);

            return Results.Ok(new MessageDto(
                await command.ExecuteNonQueryAsync(cancellationToken) == 1
                    ? "Запрос дружбы отменён."
                    : "Ожидающий запрос не найден."));
        });

        app.MapGet("/api/me/friends", async Task<IResult> (
            HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                SELECT u.user_id,
                       CASE WHEN COALESCE(v.display_name_visibility, 'registered') IN ('friends', 'registered')
                            THEN u.display_name ELSE NULL END,
                       CASE WHEN COALESCE(v.avatar_visibility, 'registered') IN ('friends', 'registered')
                            THEN EXISTS (SELECT 1 FROM public.user_avatars a WHERE a.user_id = u.user_id)
                            ELSE NULL END
                FROM public.user_friendships f
                JOIN public.users u ON u.user_id =
                    CASE WHEN f.user_low_id = @user THEN f.user_high_id ELSE f.user_low_id END
                LEFT JOIN public.user_profile_visibility v ON v.user_id = u.user_id
                WHERE f.status = 'accepted'
                  AND (f.user_low_id = @user OR f.user_high_id = @user)
                  AND NOT EXISTS (
                      SELECT 1 FROM public.user_blocks b
                      WHERE (b.blocker_user_id = @user AND b.blocked_user_id = u.user_id)
                         OR (b.blocker_user_id = u.user_id AND b.blocked_user_id = @user)
                  )
                ORDER BY COALESCE(v.display_name_visibility, 'registered'), u.user_id", connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);

            var items = new List<UserRelationshipDto>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add(new UserRelationshipDto(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetBoolean(2)));

            return Results.Ok(new ItemsResponse<UserRelationshipDto>(items));
        });

        app.MapDelete("/api/me/friends/{otherUserId:int}", async Task<IResult> (
            int otherUserId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
            if (otherUserId <= 0 || otherUserId == userId)
                return Results.BadRequest(new ApiErrorResponse("Некорректный пользователь."));

            var (lowId, highId) = OrderedPair(userId, otherUserId);
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                DELETE FROM public.user_friendships
                WHERE user_low_id = @low AND user_high_id = @high AND status = 'accepted'", connection);
            command.Parameters.AddWithValue("low", NpgsqlDbType.Integer, lowId);
            command.Parameters.AddWithValue("high", NpgsqlDbType.Integer, highId);

            return Results.Ok(new MessageDto(
                await command.ExecuteNonQueryAsync(cancellationToken) == 1
                    ? "Пользователь удалён из друзей."
                    : "Дружеская связь не найдена."));
        });

        app.MapPost("/api/users/{targetUserId:int}/block", async Task<IResult> (
            int targetUserId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
            if (targetUserId <= 0 || targetUserId == userId)
                return Results.BadRequest(new ApiErrorResponse("Нельзя заблокировать самого себя."));

            var (lowId, highId) = OrderedPair(userId, targetUserId);
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var targetCheck = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM public.users WHERE user_id = @target)", connection, transaction))
            {
                targetCheck.Parameters.AddWithValue("target", NpgsqlDbType.Integer, targetUserId);
                if (!(bool)(await targetCheck.ExecuteScalarAsync(cancellationToken) ?? false))
                    return Results.NotFound(new ApiErrorResponse("Пользователь не найден."));
            }

            await using (var insert = new NpgsqlCommand(@"
                INSERT INTO public.user_blocks(blocker_user_id, blocked_user_id)
                VALUES (@user, @target)
                ON CONFLICT (blocker_user_id, blocked_user_id) DO NOTHING", connection, transaction))
            {
                insert.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
                insert.Parameters.AddWithValue("target", NpgsqlDbType.Integer, targetUserId);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var removeRelationship = new NpgsqlCommand(@"
                DELETE FROM public.user_friendships
                WHERE user_low_id = @low AND user_high_id = @high", connection, transaction))
            {
                removeRelationship.Parameters.AddWithValue("low", NpgsqlDbType.Integer, lowId);
                removeRelationship.Parameters.AddWithValue("high", NpgsqlDbType.Integer, highId);
                await removeRelationship.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new MessageDto("Пользователь заблокирован. Дружеская связь и ожидающие запросы удалены."));
        });

        app.MapGet("/api/me/blocks", async Task<IResult> (
            HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                SELECT b.blocked_user_id
                FROM public.user_blocks b
                WHERE b.blocker_user_id = @user
                ORDER BY b.blocked_user_id", connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);

            var items = new List<UserBlockDto>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add(new UserBlockDto(reader.GetInt32(0)));

            return Results.Ok(new ItemsResponse<UserBlockDto>(items));
        });

        app.MapDelete("/api/me/blocks/{blockedUserId:int}", async Task<IResult> (
            int blockedUserId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
            if (blockedUserId <= 0 || blockedUserId == userId)
                return Results.BadRequest(new ApiErrorResponse("Некорректный пользователь."));

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                DELETE FROM public.user_blocks
                WHERE blocker_user_id = @user AND blocked_user_id = @target", connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            command.Parameters.AddWithValue("target", NpgsqlDbType.Integer, blockedUserId);

            return Results.Ok(new MessageDto(
                await command.ExecuteNonQueryAsync(cancellationToken) == 1
                    ? "Блокировка снята."
                    : "Пользователь не найден в списке блокировок."));
        });
    }

    private static (int LowId, int HighId) OrderedPair(int first, int second) =>
        first < second ? (first, second) : (second, first);
}
