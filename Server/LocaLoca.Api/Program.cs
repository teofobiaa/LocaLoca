using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using LocaLoca.Api;
using LocaLoca.Api.Security;

var builder = WebApplication.CreateBuilder(args);
// Local development keeps the loopback endpoint. Render supplies PORT; in the
// container the API must listen on all interfaces so the platform can reach it.
var renderPort = builder.Configuration["PORT"];
if (!string.IsNullOrWhiteSpace(renderPort))
{
    var sessionSecret = builder.Configuration["LOCA_SESSION_SECRET"];
    if (string.IsNullOrWhiteSpace(sessionSecret) || sessionSecret.Length < 32)
        throw new InvalidOperationException("LOCA_SESSION_SECRET must contain at least 32 characters for cloud hosting.");
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
}
else
{
    builder.WebHost.UseUrls("http://127.0.0.1:5181");
}
builder.Services.Configure<FormOptions>(options =>
{
    // 20 MiB is the application photo limit; leave room for multipart headers and fields.
    options.MultipartBodyLengthLimit = 22 * 1024 * 1024;
});
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("auth-register", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("auth-logout", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
var app = builder.Build();

// Empty framework responses (401/403/404/429, etc.) use the same JSON
// error contract as explicit validation failures and exception responses.
app.UseStatusCodePages(async statusCodeContext =>
{
    var response = statusCodeContext.HttpContext.Response;
    await response.WriteAsJsonAsync(
        new ApiErrorResponse(ApiErrorResponse.MessageFor(response.StatusCode)));
});
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseRateLimiter();

var dataRoot = builder.Configuration["LOCA_DATA_ROOT"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocaLoca");
var userConfigPath = Path.Combine(dataRoot, "server-config.json");
// Prefer server environment variables. Do not attempt to decrypt a Windows
// DPAPI config in Linux containers when the cloud connection string is set.
var connectionString = builder.Configuration["LOCA_DB_CONNECTION"]
    ?? builder.Configuration["ConnectionStrings:LocaLoca"]
    ?? builder.Configuration.GetConnectionString("LocaLoca")
    ?? ReadUserConnectionString(userConfigPath);
string? connectionConfigurationError = null;

// PostgreSQL is shared in Supabase. Keep 127.0.0.1 only for the local
// WinUI -> ASP.NET Core API connection; reject a local PostgreSQL endpoint.
if (!string.IsNullOrWhiteSpace(connectionString) &&
    !connectionString.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        var connectionBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        var configuredHost = (connectionBuilder.Host ?? string.Empty).Trim().Trim('[', ']');
        var isLoopbackHost =
            configuredHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            configuredHost.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
            configuredHost.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);

        if (isLoopbackHost && !builder.Environment.IsEnvironment("Testing"))
        {
            connectionString = null;
            connectionConfigurationError =
                "Локальная PostgreSQL больше не используется. Настрой подключение к Supabase через tools\\Install-CloudDependencies.ps1.";
        }
        else
        {
            // Testcontainers uses an ephemeral loopback-mapped PostgreSQL port.
            // This exception is available only inside the isolated Testing host.
            connectionBuilder.Pooling = true;
            // Each desktop installation runs a local API process. Keep its
            // pool small when connecting through Supabase Pooler.
            connectionBuilder.MaxPoolSize = Math.Min(connectionBuilder.MaxPoolSize, 5);
            connectionString = connectionBuilder.ConnectionString;
        }
    }
    catch (ArgumentException)
    {
        connectionString = null;
        connectionConfigurationError = "Строка подключения Supabase PostgreSQL имеет неверный формат.";
    }
}

app.Use(async (context, next) =>
{
    var header=context.Request.Headers["Authorization"].ToString();
    if(header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(connectionString)){
        var token=header[7..].Trim();
        var sessions=context.RequestServices.GetRequiredService<SessionStore>();
        var userId=await sessions.ValidateAsync(token,connectionString,context.RequestAborted);
        if(userId.HasValue)context.Items["LocaLoca.UserId"]=userId.Value;
    }
    await next();
});
var photosRoot = Path.Combine(dataRoot, "Photos");
Directory.CreateDirectory(photosRoot);

// Render uses this lightweight liveness endpoint. It must not depend on Supabase,
// because the platform needs to know whether the API process itself is responsive.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Desktop clients use /ready to verify the database connection and application-role permissions.
app.MapGet("/ready", async Task<IResult> (CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new HealthDto(false, false,
            connectionConfigurationError ?? "Не настроена строка подключения Supabase PostgreSQL. Запусти tools\\Install-CloudDependencies.ps1."),
            statusCode: 503);
    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string photoSchemaCheck = @"
SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='user_sessions')
    AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='photos' AND column_name='image_data')
    AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='photos' AND column_name='content_type')
    AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='user_map_place_photos' AND column_name='image_data')
    AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='user_map_place_photos' AND column_name='content_type')";
        await using (var schemaCommand = new NpgsqlCommand(photoSchemaCheck, connection))
        {
            var photoSchemaReady = Equals(await schemaCommand.ExecuteScalarAsync(cancellationToken), true);
            if (!photoSchemaReady)
                return Results.Json(new HealthDto(true, false,
                    "Не настроено хранение фотографий в PostgreSQL. Выполните database/migrations/07_migrate_photo_storage.sql в Supabase SQL Editor."),
                    statusCode: 503);
        }

        const string permissionCheck = @"
SELECT
    has_table_privilege(current_user, 'public.user_sessions', 'SELECT')
    AND has_table_privilege(current_user, 'public.user_sessions', 'INSERT')
    AND has_table_privilege(current_user, 'public.user_sessions', 'UPDATE')
    AND has_table_privilege(current_user, 'public.user_sessions', 'DELETE')
    AND has_table_privilege(current_user, 'public.users', 'SELECT')
    AND has_table_privilege(current_user, 'public.users', 'INSERT')
    AND has_column_privilege(current_user, 'public.users', 'display_name', 'UPDATE')
    AND has_table_privilege(current_user, 'public.categories', 'SELECT')
    AND has_table_privilege(current_user, 'public.countries', 'SELECT')
    AND has_table_privilege(current_user, 'public.cities', 'SELECT')
    AND has_table_privilege(current_user, 'public.attractions', 'SELECT')
    AND has_table_privilege(current_user, 'public.visits', 'SELECT')
    AND has_table_privilege(current_user, 'public.visits', 'INSERT')
    AND has_table_privilege(current_user, 'public.visits', 'UPDATE')
    AND has_table_privilege(current_user, 'public.visits', 'DELETE')
    AND has_table_privilege(current_user, 'public.photos', 'SELECT')
    AND has_table_privilege(current_user, 'public.photos', 'INSERT')
    AND has_table_privilege(current_user, 'public.photos', 'DELETE')
    AND has_column_privilege(current_user, 'public.photos', 'image_data', 'SELECT')
    AND has_column_privilege(current_user, 'public.photos', 'image_data', 'INSERT')
    AND has_column_privilege(current_user, 'public.photos', 'content_type', 'SELECT')
    AND has_column_privilege(current_user, 'public.photos', 'content_type', 'INSERT')
    AND has_table_privilege(current_user, 'public.wishlist', 'SELECT')
    AND has_table_privilege(current_user, 'public.wishlist', 'INSERT')
    AND has_table_privilege(current_user, 'public.wishlist', 'DELETE')
    AND has_table_privilege(current_user, 'public.user_map_places', 'SELECT')
    AND has_table_privilege(current_user, 'public.user_map_places', 'INSERT')
    AND has_table_privilege(current_user, 'public.user_map_places', 'UPDATE')
    AND has_table_privilege(current_user, 'public.user_map_places', 'DELETE')
    AND has_table_privilege(current_user, 'public.user_map_place_photos', 'SELECT')
    AND has_table_privilege(current_user, 'public.user_map_place_photos', 'INSERT')
    AND has_table_privilege(current_user, 'public.user_map_place_photos', 'DELETE')
    AND has_column_privilege(current_user, 'public.user_map_place_photos', 'image_data', 'SELECT')
    AND has_column_privilege(current_user, 'public.user_map_place_photos', 'image_data', 'INSERT')
    AND has_column_privilege(current_user, 'public.user_map_place_photos', 'content_type', 'SELECT')
    AND has_column_privilege(current_user, 'public.user_map_place_photos', 'content_type', 'INSERT')
    AND has_sequence_privilege(current_user, 'public.users_user_id_seq', 'USAGE')
    AND has_sequence_privilege(current_user, 'public.visits_visit_id_seq', 'USAGE')
    AND has_sequence_privilege(current_user, 'public.photos_photo_id_seq', 'USAGE')
    AND has_sequence_privilege(current_user, 'public.user_map_places_user_map_place_id_seq', 'USAGE')
    AND has_sequence_privilege(current_user, 'public.user_map_place_photos_photo_id_seq', 'USAGE')";
        await using var command = new NpgsqlCommand(permissionCheck, connection);
        var hasPermissions = Equals(await command.ExecuteScalarAsync(cancellationToken), true);
        if (!hasPermissions)
            return Results.Json(new HealthDto(true, false,
                "Подключение к PostgreSQL есть, но у роли БД не хватает прав для LocaLoca. Если localoca_app уже создана, выполни database/security/06_update_runtime_permissions.sql после миграций 04, 07 и 08_server_sessions.sql. Если роли ещё нет, создай её через database/security/05_create_app_role.sql."),
                statusCode: 503);
        return Results.Ok(new HealthDto(true, true, "Supabase PostgreSQL подключена; права роли LocaLoca проверены."));
    }
    catch (PostgresException ex)
    {
        var mapping = ApiExceptionMiddleware.MapPostgresError(ex.SqlState, ex.Message);
        app.Logger.LogWarning(ex, "Readiness check failed with PostgreSQL SQLSTATE {SqlState}.", ex.SqlState);
        return Results.Json(new HealthDto(true, false, mapping.Message), statusCode: mapping.StatusCode);
    }
    catch (Exception ex) when (ex is NpgsqlException or DatabaseUnavailableException or TimeoutException)
    {
        app.Logger.LogWarning(ex, "PostgreSQL is unavailable during readiness check.");
        return Results.Json(new HealthDto(true, false,
            ApiErrorResponse.MessageFor(StatusCodes.Status503ServiceUnavailable)),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

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
    command.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, PasswordHasher.Hash(request.Password));
    command.Parameters.AddWithValue("display", NpgsqlDbType.Varchar, displayName);
    var result = await command.ExecuteScalarAsync(cancellationToken);
    if (result is null) return Results.Conflict(new ApiErrorResponse("Пользователь с таким логином уже существует."));
    var user = new UserDto(Convert.ToInt32(result), login, displayName);
    return Results.Ok(new AuthResponse(user, await sessions.CreateAsync(user.Id, connectionString!, cancellationToken)));
}).RequireRateLimiting("auth-register");

app.MapPost("/api/auth/login", async Task<IResult> (LoginRequest request, SessionStore sessions, CancellationToken cancellationToken) =>
{
    var login = (request.Login ?? string.Empty).Trim().ToLowerInvariant();
    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand(
        "SELECT user_id, password_hash, display_name FROM users WHERE login = @login", connection);
    command.Parameters.AddWithValue("login", NpgsqlDbType.Varchar, login);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken) || !PasswordHasher.Verify(request.Password ?? string.Empty, reader.GetString(1)))
        return Results.Unauthorized();
    var user = new UserDto(reader.GetInt32(0), login, reader.GetString(2));
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

app.MapGet("/api/catalog/categories", async Task<IResult> (CancellationToken cancellationToken) =>
{
    var items = new List<CategoryDto>();
    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand("SELECT category_id, name FROM categories ORDER BY name", connection);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) items.Add(new CategoryDto(reader.GetInt32(0), reader.GetString(1)));
    return Results.Ok(new ItemsResponse<CategoryDto>(items));
});

app.MapGet("/api/catalog", async Task<IResult> (string? search, int? categoryId, CancellationToken cancellationToken) =>
{
    const string sql = @"
SELECT a.attraction_id, a.name, a.description, COALESCE(a.history, ''),
       a.latitude, a.longitude, cat.name, ci.name, co.name
FROM attractions a
JOIN categories cat ON cat.category_id = a.category_id
JOIN cities ci ON ci.city_id = a.city_id
JOIN countries co ON co.country_id = ci.country_id
WHERE (@q = '' OR a.name ILIKE '%' || @q || '%' OR ci.name ILIKE '%' || @q || '%' OR co.name ILIKE '%' || @q || '%')
  AND (@cat IS NULL OR a.category_id = @cat)
ORDER BY co.name, ci.name, a.name";
    var items = new List<AttractionDto>();
    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.AddWithValue("q", NpgsqlDbType.Text, search?.Trim() ?? string.Empty);
    command.Parameters.AddWithValue("cat", NpgsqlDbType.Integer, (object?)categoryId ?? DBNull.Value);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
        items.Add(new AttractionDto(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            (double)reader.GetDecimal(4), (double)reader.GetDecimal(5), reader.GetString(6), reader.GetString(7), reader.GetString(8)));
    return Results.Ok(new ItemsResponse<AttractionDto>(items));
});

app.MapGet("/api/me/visits", async Task<IResult> (HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var items = new List<VisitDto>();
    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand(@"
SELECT v.visit_id, v.attraction_id, a.name, ci.name, co.name, v.visit_date,
       v.rating, v.impressions, v.notes,
       (SELECT count(*) FROM photos p WHERE p.visit_id = v.visit_id)
FROM visits v
JOIN attractions a ON a.attraction_id = v.attraction_id
JOIN cities ci ON ci.city_id = a.city_id
JOIN countries co ON co.country_id = ci.country_id
WHERE v.user_id = @user ORDER BY v.visit_date DESC, v.visit_id DESC", connection);
    command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new VisitDto(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetDateTime(5).ToString("yyyy-MM-dd"), reader.GetInt16(6), reader.GetString(7), reader.GetString(8), reader.GetInt64(9)));

    await using (var schemaCheck = new NpgsqlCommand("SELECT to_regclass('public.user_map_places') IS NOT NULL", connection))
    {
        if (Equals(await schemaCheck.ExecuteScalarAsync(cancellationToken), true))
        {
            await using var customCommand = new NpgsqlCommand(@"SELECT user_map_place_id, name, visit_date, rating, impressions, notes,
                (SELECT count(*) FROM user_map_place_photos p WHERE p.user_map_place_id=m.user_map_place_id)
                FROM user_map_places m WHERE user_id=@user AND status='visited'", connection);
            customCommand.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            await using var customReader = await customCommand.ExecuteReaderAsync(cancellationToken);
            while (await customReader.ReadAsync(cancellationToken))
            {
                var placeId = customReader.GetInt32(0);
                items.Add(new VisitDto(-placeId, 0, customReader.GetString(1), "Личная точка", string.Empty,
                    customReader.GetDateTime(2).ToString("yyyy-MM-dd"), customReader.GetInt16(3), customReader.GetString(4),
                    customReader.GetString(5), customReader.GetInt64(6), placeId));
            }
        }
    }
    items.Sort((left, right) => StringComparer.Ordinal.Compare(right.VisitDate, left.VisitDate));
    return Results.Ok(new ItemsResponse<VisitDto>(items));
});

app.MapGet("/api/me/wishlist", async Task<IResult> (HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var items = new List<int>();
    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand("SELECT attraction_id FROM wishlist WHERE user_id = @u ORDER BY added_at DESC", connection);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) items.Add(reader.GetInt32(0));
    return Results.Ok(new IntItemsResponse(items));
});

app.MapPost("/api/me/wishlist/{attractionId:int}", async Task<IResult> (int attractionId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand(@"INSERT INTO wishlist(user_id, attraction_id) VALUES (@u, @a)
        ON CONFLICT (user_id, attraction_id) DO NOTHING", connection);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    command.Parameters.AddWithValue("a", NpgsqlDbType.Integer, attractionId);
    await command.ExecuteNonQueryAsync(cancellationToken);
    return Results.Ok(new MessageDto("Добавлено в список «Хочу посетить»."));
});

app.MapDelete("/api/me/wishlist/{attractionId:int}", async Task<IResult> (int attractionId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand("DELETE FROM wishlist WHERE user_id = @u AND attraction_id = @a", connection);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    command.Parameters.AddWithValue("a", NpgsqlDbType.Integer, attractionId);
    await command.ExecuteNonQueryAsync(cancellationToken);
    return Results.Ok(new MessageDto("Удалено из списка."));
});

app.MapPost("/api/me/visits/save", async Task<IResult> (HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var form = await context.Request.ReadFormAsync(cancellationToken);
    if (!int.TryParse(form["attractionId"], out var attractionId) || attractionId <= 0)
        return Results.BadRequest(new ApiErrorResponse("Выберите достопримечательность."));
    if (!int.TryParse(form["rating"], out var rating) || rating is < 1 or > 5)
        return Results.BadRequest(new ApiErrorResponse("Оценка должна быть от 1 до 5."));
    if (!DateOnly.TryParse(form["visitDate"], out var visitDate) || visitDate > DateOnly.FromDateTime(DateTime.Today))
        return Results.BadRequest(new ApiErrorResponse("Укажите корректную дату посещения, не позднее сегодняшнего дня."));
    int? visitId = int.TryParse(form["visitId"], out var parsedVisitId) && parsedVisitId > 0 ? parsedVisitId : null;
    var files = form.Files.GetFiles("photos");
    if (files.Count > 10) return Results.BadRequest(new ApiErrorResponse("За один раз можно добавить не более 10 фотографий."));
    if (files.Any(f => f.Length == 0))
        return Results.BadRequest(new ApiErrorResponse("Пустые файлы фотографий не допускаются."));
    if (files.Any(f => f.Length > 20 * 1024 * 1024) || files.Sum(f => f.Length) > 20 * 1024 * 1024)
        return Results.Json(new ApiErrorResponse("Общий размер фотографий за одно сохранение не должен превышать 20 МБ."),
            statusCode: StatusCodes.Status413PayloadTooLarge);
    var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
    if (files.Any(f => !allowedExtensions.Contains(Path.GetExtension(f.FileName).ToLowerInvariant())))
        return Results.BadRequest(new ApiErrorResponse("Поддерживаются JPG, PNG, WEBP, BMP и GIF."));

    // Read and validate every image before opening the transaction. A bad file
    // cannot leave a partially saved visit or a partial photo collection.
    var uploads = new List<(string RelativePath, string Caption, byte[] Data, string ContentType)>();
    foreach (var file in files)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var data = await ReadPhotoDataAsync(file, cancellationToken);
        if (!ImageContentValidator.HasValidImageSignature(data, extension))
            return Results.BadRequest(new ApiErrorResponse($"Файл «{Path.GetFileName(file.FileName)}» не соответствует формату {extension}."));
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var relativePath = $"Photos/{fileName}";
        var caption = Path.GetFileNameWithoutExtension(file.FileName);
        if (caption.Length > 200) caption = caption[..200];
        uploads.Add((relativePath, caption, data, ImageContentValidator.GetContentType(relativePath)));
    }

    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
    int savedVisitId;
    if (visitId is null)
    {
        await using var insert = new NpgsqlCommand(@"INSERT INTO visits(user_id, attraction_id, visit_date, rating, impressions, notes)
            VALUES (@u, @a, @d, @r, @i, @n) RETURNING visit_id", connection, transaction);
        AddVisitParameters(insert, userId, new VisitWriteRequest(attractionId, visitDate.ToString("yyyy-MM-dd"), rating,
            form["impressions"].ToString(), form["notes"].ToString()), visitDate);
        savedVisitId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
    }
    else
    {
        await using var update = new NpgsqlCommand(@"UPDATE visits SET attraction_id=@a, visit_date=@d, rating=@r,
            impressions=@i, notes=@n WHERE visit_id=@v AND user_id=@u", connection, transaction);
        AddVisitParameters(update, userId, new VisitWriteRequest(attractionId, visitDate.ToString("yyyy-MM-dd"), rating,
            form["impressions"].ToString(), form["notes"].ToString()), visitDate);
        update.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId.Value);
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return Results.NotFound(new ApiErrorResponse("Посещение не найдено."));
        savedVisitId = visitId.Value;
    }

    foreach (var upload in uploads)
    {
        await using var photoInsert = new NpgsqlCommand(
            @"INSERT INTO photos(visit_id, file_path, caption, image_data, content_type)
              VALUES (@v, @p, @c, @data, @type)", connection, transaction);
        photoInsert.Parameters.AddWithValue("v", NpgsqlDbType.Integer, savedVisitId);
        photoInsert.Parameters.AddWithValue("p", NpgsqlDbType.Varchar, upload.RelativePath);
        photoInsert.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, upload.Caption);
        photoInsert.Parameters.AddWithValue("data", NpgsqlDbType.Bytea, upload.Data);
        photoInsert.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, upload.ContentType);
        await photoInsert.ExecuteNonQueryAsync(cancellationToken);
    }

    await transaction.CommitAsync(cancellationToken);
    return Results.Ok(new VisitSavedDto(savedVisitId, "Посещение и фотографии сохранены в PostgreSQL."));
});

app.MapPost("/api/me/visits", async Task<IResult> (VisitWriteRequest request, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    return await SaveVisitAsync(connectionString!, request, userId, null, cancellationToken);
});

app.MapPut("/api/me/visits/{visitId:int}", async Task<IResult> (int visitId, VisitWriteRequest request, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    return await SaveVisitAsync(connectionString!, request, userId, visitId, cancellationToken);
});

app.MapDelete("/api/me/visits/{visitId:int}", async Task<IResult> (int visitId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var storedPaths = new List<string>();
    await using (var connection = await OpenAsync(connectionString, cancellationToken))
    await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
    {
        await using var photosCommand = new NpgsqlCommand(@"SELECT p.file_path FROM photos p
            JOIN visits v ON v.visit_id = p.visit_id WHERE v.visit_id = @v AND v.user_id = @u", connection, transaction);
        photosCommand.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId);
        photosCommand.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
        await using (var reader = await photosCommand.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) storedPaths.Add(reader.GetString(0));
        await using var delete = new NpgsqlCommand("DELETE FROM visits WHERE visit_id = @v AND user_id = @u", connection, transaction);
        delete.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId);
        delete.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
        if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0) return Results.NotFound(new ApiErrorResponse("Посещение не найдено."));
        await transaction.CommitAsync(cancellationToken);
    }
    foreach (var path in storedPaths) TryDeletePhoto(dataRoot, path);
    return Results.Ok(new MessageDto("Посещение удалено."));
});

app.MapPost("/api/me/visits/{visitId:int}/photos", async Task<IResult> (int visitId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var form = await context.Request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest(new ApiErrorResponse("Файл не выбран."));
    if (file.Length > 20 * 1024 * 1024)
        return Results.Json(new ApiErrorResponse("Размер фотографии не должен превышать 20 МБ."),
            statusCode: StatusCodes.Status413PayloadTooLarge);
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    var allowed = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
    if (!allowed.Contains(extension)) return Results.BadRequest(new ApiErrorResponse("Поддерживаются JPG, PNG, WEBP, BMP и GIF."));
    var data = await ReadPhotoDataAsync(file, cancellationToken);
    if (!ImageContentValidator.HasValidImageSignature(data, extension))
        return Results.BadRequest(new ApiErrorResponse("Содержимое файла не соответствует формату изображения."));

    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var ownership = new NpgsqlCommand("SELECT 1 FROM visits WHERE visit_id = @v AND user_id = @u", connection);
    ownership.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId);
    ownership.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    if (await ownership.ExecuteScalarAsync(cancellationToken) is null) return Results.NotFound(new ApiErrorResponse("Посещение не найдено."));

    var relativePath = $"Photos/{Guid.NewGuid():N}{extension}";
    var caption = Path.GetFileNameWithoutExtension(file.FileName);
    if (caption.Length > 200) caption = caption[..200];
    await using var insert = new NpgsqlCommand(
        @"INSERT INTO photos(visit_id, file_path, caption, image_data, content_type)
          VALUES (@v, @p, @c, @data, @type) RETURNING photo_id", connection);
    insert.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId);
    insert.Parameters.AddWithValue("p", NpgsqlDbType.Varchar, relativePath);
    insert.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, caption);
    insert.Parameters.AddWithValue("data", NpgsqlDbType.Bytea, data);
    insert.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, ImageContentValidator.GetContentType(relativePath));
    var id = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
    return Results.Ok(new PhotoDto(id, relativePath, caption));
});

app.MapGet("/api/me/visits/{visitId:int}/photos", async Task<IResult> (int visitId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var items = new List<PhotoDto>();
    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand(@"SELECT p.photo_id, p.file_path, p.caption FROM photos p
        JOIN visits v ON v.visit_id = p.visit_id WHERE v.visit_id = @v AND v.user_id = @u ORDER BY p.added_at", connection);
    command.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) items.Add(new PhotoDto(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
    return Results.Ok(new ItemsResponse<PhotoDto>(items));
});

app.MapGet("/api/me/photos/{photoId:int}", async Task<IResult> (int photoId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    string relativePath;
    byte[]? imageData;
    string contentType;
    await using (var connection = await OpenAsync(connectionString, cancellationToken))
    await using (var command = new NpgsqlCommand(@"SELECT p.file_path, p.image_data, p.content_type FROM photos p
        JOIN visits v ON v.visit_id = p.visit_id WHERE p.photo_id = @p AND v.user_id = @u", connection))
    {
        command.Parameters.AddWithValue("p", NpgsqlDbType.Integer, photoId);
        command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return Results.NotFound();
        relativePath = reader.GetString(0);
        imageData = reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1);
        contentType = reader.IsDBNull(2) ? ImageContentValidator.GetContentType(relativePath) : reader.GetString(2);
    }
    if (imageData is not null) return Results.File(imageData, contentType);

    // Backward compatibility for records created before the database migration.
    var fullPath = SafePhotoPath(dataRoot, relativePath);
    if (fullPath is null || !File.Exists(fullPath)) return Results.NotFound();
    return Results.File(fullPath, ImageContentValidator.GetContentType(fullPath));
});

app.MapDelete("/api/me/photos/{photoId:int}", async Task<IResult> (int photoId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand(@"DELETE FROM photos p USING visits v
        WHERE p.visit_id = v.visit_id AND p.photo_id = @p AND v.user_id = @u RETURNING p.file_path", connection);
    command.Parameters.AddWithValue("p", NpgsqlDbType.Integer, photoId);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    var path = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (path is null) return Results.NotFound();
    TryDeletePhoto(dataRoot, path);
    return Results.Ok(new MessageDto("Фотография удалена."));
});

app.MapGet("/api/me/summary", async Task<IResult> (HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand(@"
SELECT count(DISTINCT v.visit_id), count(DISTINCT v.attraction_id),
       count(DISTINCT ci.city_id), count(DISTINCT co.country_id)
FROM visits v
JOIN attractions a ON a.attraction_id = v.attraction_id
JOIN cities ci ON ci.city_id = a.city_id
JOIN countries co ON co.country_id = ci.country_id
WHERE v.user_id = @u", connection);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    await reader.ReadAsync(cancellationToken);
    var visitCount = reader.GetInt64(0);
    var distinctAttractions = reader.GetInt64(1);
    var cities = reader.GetInt64(2);
    var countries = reader.GetInt64(3);
    await reader.DisposeAsync();
    long customVisits = 0;
    await using (var schemaCheck = new NpgsqlCommand("SELECT to_regclass('public.user_map_places') IS NOT NULL", connection))
    {
        if (Equals(await schemaCheck.ExecuteScalarAsync(cancellationToken), true))
        {
            await using var customCount = new NpgsqlCommand(@"SELECT count(*) FROM user_map_places
                WHERE user_id=@u AND status='visited'", connection);
            customCount.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
            customVisits = Convert.ToInt64(await customCount.ExecuteScalarAsync(cancellationToken));
        }
    }
    return Results.Ok(new SummaryDto(visitCount + customVisits, distinctAttractions + customVisits, cities, countries));
});

app.MapGet("/api/me/map-markers", async Task<IResult> (HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var items = new List<MapMarkerDto>();
    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand(@"
SELECT a.attraction_id, a.name, a.latitude, a.longitude,
       EXISTS(SELECT 1 FROM visits v WHERE v.user_id = @u AND v.attraction_id = a.attraction_id),
       EXISTS(SELECT 1 FROM wishlist w WHERE w.user_id = @u AND w.attraction_id = a.attraction_id)
FROM attractions a ORDER BY a.name", connection);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        while (await reader.ReadAsync(cancellationToken)) items.Add(new MapMarkerDto(reader.GetInt32(0), reader.GetString(1),
            (double)reader.GetDecimal(2), (double)reader.GetDecimal(3), reader.GetBoolean(4), reader.GetBoolean(5)));

    await using var customCommand = new NpgsqlCommand(@"
SELECT user_map_place_id, name, latitude, longitude, status, visit_date, rating, impressions, notes
FROM user_map_places WHERE user_id = @u ORDER BY created_at", connection);
    customCommand.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    await using var customReader = await customCommand.ExecuteReaderAsync(cancellationToken);
    while (await customReader.ReadAsync(cancellationToken))
        items.Add(new MapMarkerDto(customReader.GetInt32(0), customReader.GetString(1),
            (double)customReader.GetDecimal(2), (double)customReader.GetDecimal(3),
            customReader.GetString(4) == "visited", customReader.GetString(4) == "planned",
            customReader.GetInt32(0), customReader.IsDBNull(5) ? null : customReader.GetDateTime(5).ToString("yyyy-MM-dd"),
            customReader.IsDBNull(6) ? null : customReader.GetInt16(6), customReader.GetString(7), customReader.GetString(8)));
    return Results.Ok(new ItemsResponse<MapMarkerDto>(items));
});

app.MapPost("/api/me/map-places", async Task<IResult> (MapPlaceCreateRequest request, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var name = (request.Name ?? string.Empty).Trim();
    if (name.Length is < 1 or > 150) return Results.BadRequest(new ApiErrorResponse("Название должно содержать от 1 до 150 символов."));
    if (!double.IsFinite(request.Latitude) || request.Latitude is < -90 or > 90 ||
        !double.IsFinite(request.Longitude) || request.Longitude is < -180 or > 180)
        return Results.BadRequest(new ApiErrorResponse("Укажите корректные координаты места."));
    var status = string.IsNullOrWhiteSpace(request.Status) ? "planned" : request.Status.Trim().ToLowerInvariant();
    if (status is not ("planned" or "visited")) return Results.BadRequest(new ApiErrorResponse("Выберите статус места."));
    DateOnly? visitDate = null;
    int? rating = null;
    if (status == "visited")
    {
        if (!DateOnly.TryParse(request.VisitDate, out var parsedDate) || parsedDate > DateOnly.FromDateTime(DateTime.Today))
            return Results.BadRequest(new ApiErrorResponse("Укажите корректную дату посещения, не позднее сегодняшнего дня."));
        if (request.Rating is < 1 or > 5) return Results.BadRequest(new ApiErrorResponse("Оценка должна быть от 1 до 5."));
        visitDate = parsedDate;
        rating = request.Rating;
    }

    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand(@"INSERT INTO user_map_places(user_id, name, latitude, longitude, status,
        visit_date, rating, impressions, notes) VALUES (@u, @n, @lat, @lon, @s, @d, @r, @i, @notes)
        RETURNING user_map_place_id", connection);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    command.Parameters.AddWithValue("n", NpgsqlDbType.Varchar, name);
    command.Parameters.AddWithValue("lat", NpgsqlDbType.Numeric, Convert.ToDecimal(request.Latitude));
    command.Parameters.AddWithValue("lon", NpgsqlDbType.Numeric, Convert.ToDecimal(request.Longitude));
    command.Parameters.AddWithValue("s", NpgsqlDbType.Varchar, status);
    command.Parameters.AddWithValue("d", NpgsqlDbType.Date, (object?)visitDate ?? DBNull.Value);
    command.Parameters.AddWithValue("r", NpgsqlDbType.Smallint, rating.HasValue ? (object)(short)rating.Value : DBNull.Value);
    command.Parameters.AddWithValue("i", NpgsqlDbType.Text, request.Impressions ?? string.Empty);
    command.Parameters.AddWithValue("notes", NpgsqlDbType.Text, request.Notes ?? string.Empty);
    var id = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    return Results.Ok(new MapPlaceSavedDto(id, status == "visited" ? "Посещение сохранено." : "Место добавлено в планы."));
});

app.MapPut("/api/me/map-places/{placeId:int}", async Task<IResult> (int placeId, MapPlaceUpdateRequest request, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var status = (request.Status ?? string.Empty).Trim().ToLowerInvariant();
    if (status is not ("planned" or "visited")) return Results.BadRequest(new ApiErrorResponse("Выберите статус места."));
    DateOnly? visitDate = null;
    int? rating = null;
    if (status == "visited")
    {
        if (!DateOnly.TryParse(request.VisitDate, out var parsedDate) || parsedDate > DateOnly.FromDateTime(DateTime.Today))
            return Results.BadRequest(new ApiErrorResponse("Укажите корректную дату посещения, не позднее сегодняшнего дня."));
        if (request.Rating is < 1 or > 5) return Results.BadRequest(new ApiErrorResponse("Оценка должна быть от 1 до 5."));
        visitDate = parsedDate;
        rating = request.Rating;
    }
    var name = (request.Name ?? string.Empty).Trim();
    if (name.Length is < 1 or > 150) return Results.BadRequest(new ApiErrorResponse("Название должно содержать от 1 до 150 символов."));

    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand(@"UPDATE user_map_places SET name=@n, status=@s, visit_date=@d,
        rating=@r, impressions=@i, notes=@notes WHERE user_map_place_id=@p AND user_id=@u", connection);
    command.Parameters.AddWithValue("n", NpgsqlDbType.Varchar, name);
    command.Parameters.AddWithValue("s", NpgsqlDbType.Varchar, status);
    command.Parameters.AddWithValue("d", NpgsqlDbType.Date, (object?)visitDate ?? DBNull.Value);
    command.Parameters.AddWithValue("r", NpgsqlDbType.Smallint, rating.HasValue ? (object)(short)rating.Value : DBNull.Value);
    command.Parameters.AddWithValue("i", NpgsqlDbType.Text, request.Impressions ?? string.Empty);
    command.Parameters.AddWithValue("notes", NpgsqlDbType.Text, request.Notes ?? string.Empty);
    command.Parameters.AddWithValue("p", NpgsqlDbType.Integer, placeId);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return Results.NotFound(new ApiErrorResponse("Место не найдено."));
    return Results.Ok(new MessageDto("Место обновлено."));
});

app.MapDelete("/api/me/map-places/{placeId:int}", async Task<IResult> (int placeId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var photoPaths = new List<string>();
    await using (var connection = await OpenAsync(connectionString, cancellationToken))
    await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
    {
        await using var photos = new NpgsqlCommand(@"SELECT p.file_path FROM user_map_place_photos p
            JOIN user_map_places m ON m.user_map_place_id=p.user_map_place_id
            WHERE m.user_map_place_id=@p AND m.user_id=@u", connection, transaction);
        photos.Parameters.AddWithValue("p", NpgsqlDbType.Integer, placeId);
        photos.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
        await using (var reader = await photos.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) photoPaths.Add(reader.GetString(0));
        await using var delete = new NpgsqlCommand("DELETE FROM user_map_places WHERE user_map_place_id=@p AND user_id=@u", connection, transaction);
        delete.Parameters.AddWithValue("p", NpgsqlDbType.Integer, placeId);
        delete.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
        if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0) return Results.NotFound(new ApiErrorResponse("Место не найдено."));
        await transaction.CommitAsync(cancellationToken);
    }
    foreach (var path in photoPaths) TryDeletePhoto(dataRoot, path);
    return Results.Ok(new MessageDto("Место удалено."));
});

app.MapGet("/api/me/map-places/{placeId:int}/photos", async Task<IResult> (int placeId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var items = new List<PhotoDto>();
    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand(@"SELECT p.photo_id, p.file_path, p.caption FROM user_map_place_photos p
        JOIN user_map_places m ON m.user_map_place_id=p.user_map_place_id
        WHERE m.user_map_place_id=@p AND m.user_id=@u ORDER BY p.added_at", connection);
    command.Parameters.AddWithValue("p", NpgsqlDbType.Integer, placeId);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) items.Add(new PhotoDto(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
    return Results.Ok(new ItemsResponse<PhotoDto>(items));
});

app.MapPost("/api/me/map-places/{placeId:int}/photos", async Task<IResult> (int placeId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var form = await context.Request.ReadFormAsync(cancellationToken);
    var files = form.Files.GetFiles("photos");
    if (files.Count is < 1 or > 10) return Results.BadRequest(new ApiErrorResponse("Выберите от 1 до 10 фотографий."));
    if (files.Any(f => f.Length == 0))
        return Results.BadRequest(new ApiErrorResponse("Пустые файлы фотографий не допускаются."));
    if (files.Any(f => f.Length > 20 * 1024 * 1024) || files.Sum(f => f.Length) > 20 * 1024 * 1024)
        return Results.Json(new ApiErrorResponse("Общий размер фотографий не должен превышать 20 МБ."),
            statusCode: StatusCodes.Status413PayloadTooLarge);
    var allowed = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
    if (files.Any(f => !allowed.Contains(Path.GetExtension(f.FileName).ToLowerInvariant())))
        return Results.BadRequest(new ApiErrorResponse("Поддерживаются JPG, PNG, WEBP, BMP и GIF."));

    var uploads = new List<(string RelativePath, string Caption, byte[] Data, string ContentType)>();
    foreach (var file in files)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var data = await ReadPhotoDataAsync(file, cancellationToken);
        if (!ImageContentValidator.HasValidImageSignature(data, extension))
            return Results.BadRequest(new ApiErrorResponse($"Файл «{Path.GetFileName(file.FileName)}» не соответствует формату {extension}."));
        var relativePath = $"Photos/{Guid.NewGuid():N}{extension}";
        var caption = Path.GetFileNameWithoutExtension(file.FileName);
        if (caption.Length > 200) caption = caption[..200];
        uploads.Add((relativePath, caption, data, ImageContentValidator.GetContentType(relativePath)));
    }

    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
    await using var ownership = new NpgsqlCommand("SELECT 1 FROM user_map_places WHERE user_map_place_id=@p AND user_id=@u", connection, transaction);
    ownership.Parameters.AddWithValue("p", NpgsqlDbType.Integer, placeId);
    ownership.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    if (await ownership.ExecuteScalarAsync(cancellationToken) is null) return Results.NotFound(new ApiErrorResponse("Место не найдено."));

    foreach (var upload in uploads)
    {
        await using var insert = new NpgsqlCommand(
            @"INSERT INTO user_map_place_photos(user_map_place_id, file_path, caption, image_data, content_type)
              VALUES (@p, @f, @c, @data, @type)", connection, transaction);
        insert.Parameters.AddWithValue("p", NpgsqlDbType.Integer, placeId);
        insert.Parameters.AddWithValue("f", NpgsqlDbType.Varchar, upload.RelativePath);
        insert.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, upload.Caption);
        insert.Parameters.AddWithValue("data", NpgsqlDbType.Bytea, upload.Data);
        insert.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, upload.ContentType);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }
    await transaction.CommitAsync(cancellationToken);
    return Results.Ok(new MessageDto("Фотографии сохранены в PostgreSQL."));
});

app.MapGet("/api/me/map-place-photos/{photoId:int}", async Task<IResult> (int photoId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    string relativePath;
    byte[]? imageData;
    string contentType;
    await using (var connection = await OpenAsync(connectionString, cancellationToken))
    await using (var command = new NpgsqlCommand(@"SELECT p.file_path, p.image_data, p.content_type FROM user_map_place_photos p
        JOIN user_map_places m ON m.user_map_place_id=p.user_map_place_id
        WHERE p.photo_id=@p AND m.user_id=@u", connection))
    {
        command.Parameters.AddWithValue("p", NpgsqlDbType.Integer, photoId);
        command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return Results.NotFound();
        relativePath = reader.GetString(0);
        imageData = reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1);
        contentType = reader.IsDBNull(2) ? ImageContentValidator.GetContentType(relativePath) : reader.GetString(2);
    }
    if (imageData is not null) return Results.File(imageData, contentType);

    // Backward compatibility for records created before the database migration.
    var fullPath = SafePhotoPath(dataRoot, relativePath);
    if (fullPath is null || !File.Exists(fullPath)) return Results.NotFound();
    return Results.File(fullPath, ImageContentValidator.GetContentType(fullPath));
});

app.MapDelete("/api/me/map-place-photos/{photoId:int}", async Task<IResult> (int photoId, HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    await using var connection = await OpenAsync(connectionString, cancellationToken);
    await using var command = new NpgsqlCommand(@"DELETE FROM user_map_place_photos p USING user_map_places m
        WHERE p.user_map_place_id = m.user_map_place_id AND p.photo_id = @p AND m.user_id = @u
        RETURNING p.file_path", connection);
    command.Parameters.AddWithValue("p", NpgsqlDbType.Integer, photoId);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    var path = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (path is null) return Results.NotFound();
    TryDeletePhoto(dataRoot, path);
    return Results.Ok(new MessageDto("Фотография удалена."));
});

app.MapGet("/map", async Task<IResult> (CancellationToken cancellationToken) =>
{
    var path = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "map.html");
    return File.Exists(path)
        ? Results.Content(await File.ReadAllTextAsync(path, cancellationToken), "text/html; charset=utf-8")
        : Results.NotFound();
});

app.Run();

static string? ReadUserConnectionString(string configPath)
{
    try
    {
        if (!File.Exists(configPath)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement;

        // Backward compatibility for existing installations. The installer
        // migrates this legacy plaintext format to the DPAPI format on upgrade.
        if (root.TryGetProperty("connectionString", out var legacy))
            return legacy.GetString();

        if (!root.TryGetProperty("encryptedPassword", out var encryptedPassword))
            return null;

        var encryptedBytes = Convert.FromBase64String(encryptedPassword.GetString() ?? string.Empty);
        var passwordBytes = Dpapi.Unprotect(encryptedBytes);
        try
        {
            var password = Encoding.UTF8.GetString(passwordBytes);
            var connection = new NpgsqlConnectionStringBuilder
            {
                Host = root.GetProperty("host").GetString() ?? throw new InvalidDataException("Missing database host."),
                Port = root.GetProperty("port").GetInt32(),
                Database = root.GetProperty("database").GetString() ?? "postgres",
                Username = root.GetProperty("username").GetString() ?? throw new InvalidDataException("Missing database username."),
                Password = password,
                SslMode = SslMode.Require,
                Pooling = true,
                Timeout = 15,
                CommandTimeout = 30,
                MaxPoolSize = 5
            };
            return connection.ConnectionString;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
        or CryptographicException or FormatException or InvalidOperationException or ArgumentException)
    {
        // Do not silently fall back to an unconfigured connection when a
        // DPAPI secret cannot be decrypted (for example, under another user).
        throw new InvalidOperationException(
            "Не удалось расшифровать пароль Supabase. Запусти установщик под той же учётной записью Windows.",
            ex);
    }
}

static async Task<NpgsqlConnection> OpenAsync(string? connectionString, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        throw new DatabaseUnavailableException("PostgreSQL connection is not configured.");
    var connection = new NpgsqlConnection(connectionString);
    try
    {
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
    catch
    {
        await connection.DisposeAsync();
        throw;
    }
}

static async Task<IResult> SaveVisitAsync(string connectionString, VisitWriteRequest request, int userId, int? visitId, CancellationToken cancellationToken)
{
    if (request.AttractionId <= 0) return Results.BadRequest(new ApiErrorResponse("Выберите достопримечательность."));
    if (!DateOnly.TryParse(request.VisitDate, out var date) || date > DateOnly.FromDateTime(DateTime.Today))
        return Results.BadRequest(new ApiErrorResponse("Укажите корректную дату посещения, не позднее сегодняшнего дня."));
    if (request.Rating is < 1 or > 5) return Results.BadRequest(new ApiErrorResponse("Оценка должна быть от 1 до 5."));
    int id;
    await using (var connection = await OpenAsync(connectionString, cancellationToken))
    await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
    {
        if (visitId is null)
        {
            await using var insert = new NpgsqlCommand(@"INSERT INTO visits(user_id, attraction_id, visit_date, rating, impressions, notes)
                VALUES (@u, @a, @d, @r, @i, @n) RETURNING visit_id", connection, transaction);
            AddVisitParameters(insert, userId, request, date);
            id = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
        }
        else
        {
            await using var update = new NpgsqlCommand(@"UPDATE visits SET attraction_id=@a, visit_date=@d, rating=@r,
                impressions=@i, notes=@n WHERE visit_id=@v AND user_id=@u", connection, transaction);
            AddVisitParameters(update, userId, request, date);
            update.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return Results.NotFound(new ApiErrorResponse("Посещение не найдено."));
            id = visitId.Value;
        }
        await transaction.CommitAsync(cancellationToken);
    }
    return Results.Ok(new VisitSavedDto(id, "Посещение сохранено."));
}

static void AddVisitParameters(NpgsqlCommand command, int userId, VisitWriteRequest request, DateOnly date)
{
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    command.Parameters.AddWithValue("a", NpgsqlDbType.Integer, request.AttractionId);
    command.Parameters.AddWithValue("d", NpgsqlDbType.Date, date);
    command.Parameters.AddWithValue("r", NpgsqlDbType.Smallint, (short)request.Rating);
    command.Parameters.AddWithValue("i", NpgsqlDbType.Text, request.Impressions ?? string.Empty);
    command.Parameters.AddWithValue("n", NpgsqlDbType.Text, request.Notes ?? string.Empty);
}

static bool TryUser(HttpContext context, SessionStore sessions, out int userId)
{
    userId = context.Items.TryGetValue("LocaLoca.UserId", out var value) && value is int id ? id : 0;
    return userId > 0;
}

static async Task<byte[]> ReadPhotoDataAsync(IFormFile file, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    await using var source = file.OpenReadStream();
    using var buffer = new MemoryStream(checked((int)file.Length));
    await source.CopyToAsync(buffer, cancellationToken);
    return buffer.ToArray();
}

static string? SafePhotoPath(string dataRoot, string relativePath)
{
    if (string.IsNullOrWhiteSpace(relativePath)) return null;
    var root = Path.GetFullPath(dataRoot + Path.DirectorySeparatorChar);
    var full = Path.GetFullPath(Path.Combine(dataRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
}

static void TryDeletePhoto(string dataRoot, string relativePath)
{
    try
    {
        var path = SafePhotoPath(dataRoot, relativePath);
        if (path is not null && File.Exists(path)) File.Delete(path);
    }
    catch { /* Файловая очистка не должна отменять уже выполненное удаление из БД. */ }
}

static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Unprotect(byte[] encrypted)
    {
        var inputPointer = Marshal.AllocHGlobal(encrypted.Length);
        try
        {
            Marshal.Copy(encrypted, 0, inputPointer, encrypted.Length);
            var input = new DataBlob { Length = encrypted.Length, Data = inputPointer };
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, 0, out var output))
                throw new CryptographicException(Marshal.GetLastWin32Error());

            try
            {
                var clear = new byte[output.Length];
                Marshal.Copy(output.Data, clear, 0, output.Length);
                return clear;
            }
            finally
            {
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputPointer);
        }
    }
}

record RegisterRequest(string Login, string Password, string DisplayName);
record LoginRequest(string Login, string Password);
record UpdateDisplayNameRequest(string? DisplayName);
record UserDto(int Id, string Login, string DisplayName);
record AuthResponse(UserDto User, string Token);
record MessageDto(string Message);
record HealthDto(bool ApiConfigured, bool DatabaseConnected, string Message);
record CategoryDto(int Id, string Name);
record AttractionDto(int Id, string Name, string Description, string History, double Latitude, double Longitude, string Category, string City, string Country);
record VisitWriteRequest(int AttractionId, string VisitDate, int Rating, string? Impressions, string? Notes);
record VisitSavedDto(int Id, string Message);
record VisitDto(int Id, int AttractionId, string AttractionName, string City, string Country, string VisitDate, short Rating, string Impressions, string Notes, long PhotoCount, int? UserMapPlaceId = null)
{
    public long PhotoCount { get; set; } = PhotoCount;
}
record PhotoDto(int Id, string FilePath, string Caption);
record SummaryDto(long VisitCount, long DistinctAttractions, long Cities, long Countries);
record MapMarkerDto(int Id, string Name, double Latitude, double Longitude, bool Visited, bool Wished,
    int? UserPlaceId = null, string? VisitDate = null, short? Rating = null, string? Impressions = null, string? Notes = null);
record MapPlaceCreateRequest(string? Name, double Latitude, double Longitude, string? Status, string? VisitDate,
    int? Rating, string? Impressions, string? Notes);
record MapPlaceUpdateRequest(string? Name, string? Status, string? VisitDate, int? Rating, string? Impressions, string? Notes);
record MapPlaceSavedDto(int Id, string Message);
record ItemsResponse<T>(IReadOnlyList<T> Items);
record IntItemsResponse(IReadOnlyList<int> Items);

public partial class Program { }
