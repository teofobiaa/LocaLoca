using Npgsql;
using NpgsqlTypes;
using LocaLoca.Api;
using LocaLoca.Api.Security;
using static LocaLoca.Api.ApiEndpointHelpers;
namespace LocaLoca.Api.Endpoints;

public static class AuthenticationEndpoints
{
    public static void MapAuthenticationEndpoints(this WebApplication app, string? connectionString, int passwordHashIterations)
    {
        app.MapPost("/api/auth/register", async Task<IResult> (RegisterRequest request, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            var login = (request.Login ?? string.Empty).Trim().ToLowerInvariant();
            var displayName = (request.DisplayName ?? string.Empty).Trim();
            if (login.Length is < 3 or > 50 || login.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
                return Results.BadRequest(new ApiErrorResponse("Логин должен содержать 3–50 символов: буквы, цифры, точку, дефис или подчёркивание."));
            if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
                return Results.BadRequest(new ApiErrorResponse("Пароль должен содержать не менее 6 символов."));
            if (displayName.Length > 100) return Results.BadRequest(new ApiErrorResponse("Имя слишком длинное."));
            if (string.IsNullOrWhiteSpace(displayName)) displayName = login;
        
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                INSERT INTO users (login, password_hash, display_name)
                VALUES (@login, @hash, @display)
                ON CONFLICT (login) DO NOTHING
                RETURNING user_id", connection);
            command.Parameters.AddWithValue("login", NpgsqlDbType.Varchar, login);
            command.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, PasswordHasher.Hash(request.Password, passwordHashIterations));
            command.Parameters.AddWithValue("display", NpgsqlDbType.Varchar, displayName);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is null) return Results.Conflict(new ApiErrorResponse("Пользователь с таким логином уже существует."));
            var user = new UserDto(Convert.ToInt32(result), login, displayName);
            return Results.Ok(new AuthResponse(user, await sessions.CreateAsync(user.Id, connectionString!, cancellationToken)));
        }).RequireRateLimiting("auth-register");
        
        app.MapPost("/api/auth/login", async Task<IResult> (LoginRequest request, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            var login = (request.Login ?? string.Empty).Trim().ToLowerInvariant();
            var password = request.Password ?? string.Empty;
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT user_id, password_hash, display_name FROM users WHERE login = @login", connection);
            command.Parameters.AddWithValue("login", NpgsqlDbType.Varchar, login);
        
            int userId;
            string storedHash;
            string displayName;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                    return Results.Unauthorized();
        
                userId = reader.GetInt32(0);
                storedHash = reader.GetString(1);
                displayName = reader.GetString(2);
            }
        
            if (!PasswordHasher.Verify(password, storedHash))
                return Results.Unauthorized();
        
            if (PasswordHasher.NeedsRehash(storedHash, passwordHashIterations))
            {
                var upgradedHash = PasswordHasher.Hash(
                    password,
                    PasswordHasher.GetUpgradeIterations(storedHash, passwordHashIterations));
        
                // Compare-and-swap avoids overwriting a concurrent password reset.
                await using var updateCommand = new NpgsqlCommand(@"
                    UPDATE users
                    SET password_hash = @new_hash
                    WHERE user_id = @user_id AND password_hash = @old_hash", connection);
                updateCommand.Parameters.AddWithValue("new_hash", NpgsqlDbType.Varchar, upgradedHash);
                updateCommand.Parameters.AddWithValue("user_id", NpgsqlDbType.Integer, userId);
                updateCommand.Parameters.AddWithValue("old_hash", NpgsqlDbType.Varchar, storedHash);
        
                var updated = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (updated == 0)
                {
                    // Another login may have upgraded first. Issue a session only if the
                    // current password record still matches the submitted password.
                    await using var currentHashCommand = new NpgsqlCommand(
                        "SELECT password_hash FROM users WHERE user_id = @user_id", connection);
                    currentHashCommand.Parameters.AddWithValue("user_id", NpgsqlDbType.Integer, userId);
                    var currentHash = await currentHashCommand.ExecuteScalarAsync(cancellationToken) as string;
                    if (!PasswordHasher.Verify(password, currentHash ?? string.Empty))
                        return Results.Unauthorized();
                }
            }
        
            var user = new UserDto(userId, login, displayName);
            return Results.Ok(new AuthResponse(user, await sessions.CreateAsync(user.Id, connectionString!, cancellationToken)));
        }).RequireRateLimiting("auth-login");
        
        app.MapPost("/api/auth/logout", async (HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            var header = context.Request.Headers["Authorization"].ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();
        
            var token = header[7..].Trim();
            return await sessions.RevokeAsync(token, connectionString!, cancellationToken)
                ? Results.Ok(new MessageDto("Сессия завершена."))
                : Results.Unauthorized();
        }).RequireRateLimiting("auth-logout");
        
        app.MapGet("/api/me/sessions", async (HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
         if(!TryUser(context,sessions,out var userId))return Results.Unauthorized();
         var token=context.Request.Headers["Authorization"].ToString()[7..].Trim();
         return Results.Ok(new ItemsResponse<SessionDto>(await sessions.GetActiveAsync(userId,token, connectionString!, cancellationToken)));
        });
        app.MapDelete("/api/me/sessions/others", async (HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
         if(!TryUser(context,sessions,out var userId))return Results.Unauthorized();
         var token=context.Request.Headers["Authorization"].ToString()[7..].Trim();
         var count=await sessions.RevokeOthersAsync(userId,token, connectionString!, cancellationToken);
         return Results.Ok(new MessageDto($"Отозвано других сессий: {count}."));
        });
        app.MapPut("/api/me/profile/display-name", async Task<IResult> (UpdateDisplayNameRequest request, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
        
            var displayName = (request.DisplayName ?? string.Empty).Trim();
            if (displayName.Length is < 1 or > 40)
                return Results.BadRequest(new ApiErrorResponse("Имя должно содержать от 1 до 40 символов."));
        
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(
                "UPDATE users SET display_name = @display WHERE user_id = @user RETURNING login", connection);
            command.Parameters.AddWithValue("display", NpgsqlDbType.Varchar, displayName);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            var login = await command.ExecuteScalarAsync(cancellationToken) as string;
            if (login is null) return Results.NotFound(new ApiErrorResponse("Аккаунт не найден."));
        
            return Results.Ok(new UserDto(userId, login, displayName));
        });
    }
}
