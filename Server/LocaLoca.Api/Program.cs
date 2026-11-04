using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5181");
builder.Services.AddSingleton<SessionStore>();
var app = builder.Build();
var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocaLoca");
var localConfigPath = Path.Combine(dataRoot, "server-config.json");
var connectionString = Environment.GetEnvironmentVariable("LOCA_DB_CONNECTION")
    ?? ReadLocalConnectionString(localConfigPath)
    ?? builder.Configuration.GetConnectionString("LocaLoca");
var photosRoot = Path.Combine(dataRoot, "Photos");
Directory.CreateDirectory(photosRoot);

app.Use(async (context, next) =>
{
    try { await next(); }
    catch (NpgsqlException ex) when (ex.SqlState == "42P01" &&
        (ex.Message.Contains("user_map_places", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("user_map_place_photos", StringComparison.OrdinalIgnoreCase)))
    {
        app.Logger.LogError(ex, "The user map places migration has not been applied.");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "Для новых точек карты требуется миграция базы. Выполните database/04_migrate_user_map_places.sql от имени администратора PostgreSQL." });
        }
    }
    catch (Exception ex) when (ex is NpgsqlException || ex is InvalidOperationException)
    {
        app.Logger.LogError(ex, "PostgreSQL is unavailable or a database operation failed.");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "Не удалось обратиться к PostgreSQL. Проверьте, запущена ли служба и верны ли настройки подключения." });
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled local API error.");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "Внутренняя ошибка локального API." });
        }
    }
});

app.Use(async (context, next) =>
{
    connectionString = Environment.GetEnvironmentVariable("LOCA_DB_CONNECTION")
        ?? ReadLocalConnectionString(localConfigPath)
        ?? builder.Configuration.GetConnectionString("LocaLoca");
    await next();
});

app.MapGet("/health", async Task<IResult> () =>
{
    if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new HealthDto(false, false, "Не настроена строка подключения PostgreSQL."), statusCode: 503);
    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync();
        return Results.Ok(new HealthDto(true, true, "Локальный API и PostgreSQL доступны."));
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Health check failed.");
        return Results.Json(new HealthDto(true, false, "PostgreSQL недоступен. Проверьте службу и параметры подключения."), statusCode: 503);
    }
});

app.MapPost("/api/auth/register", async Task<IResult> (RegisterRequest request, SessionStore sessions) =>
{
    var login = (request.Login ?? string.Empty).Trim().ToLowerInvariant();
    var displayName = (request.DisplayName ?? string.Empty).Trim();
    if (login.Length is < 3 or > 50 || login.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
        return Results.BadRequest(new ErrorDto("Логин должен содержать 3–50 символов: буквы, цифры, точку, дефис или подчёркивание."));
    if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
        return Results.BadRequest(new ErrorDto("Пароль должен содержать не менее 6 символов."));
    if (displayName.Length > 100) return Results.BadRequest(new ErrorDto("Имя слишком длинное."));
    if (string.IsNullOrWhiteSpace(displayName)) displayName = login;

    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand(@"
        INSERT INTO users (login, password_hash, display_name)
        VALUES (@login, @hash, @display)
        ON CONFLICT (login) DO NOTHING
        RETURNING user_id", connection);
    command.Parameters.AddWithValue("login", NpgsqlDbType.Varchar, login);
    command.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, HashPassword(request.Password));
    command.Parameters.AddWithValue("display", NpgsqlDbType.Varchar, displayName);
    var result = await command.ExecuteScalarAsync();
    if (result is null) return Results.Conflict(new ErrorDto("Пользователь с таким логином уже существует."));
    var user = new UserDto(Convert.ToInt32(result), login, displayName);
    return Results.Ok(new AuthResponse(user, sessions.Create(user.Id)));
});

app.MapPost("/api/auth/login", async Task<IResult> (LoginRequest request, SessionStore sessions) =>
{
    var login = (request.Login ?? string.Empty).Trim().ToLowerInvariant();
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand(
        "SELECT user_id, password_hash, display_name FROM users WHERE login = @login", connection);
    command.Parameters.AddWithValue("login", NpgsqlDbType.Varchar, login);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync() || !VerifyPassword(request.Password ?? string.Empty, reader.GetString(1)))
        return Results.Unauthorized();
    var user = new UserDto(reader.GetInt32(0), login, reader.GetString(2));
    return Results.Ok(new AuthResponse(user, sessions.Create(user.Id)));
});

app.MapGet("/api/catalog/categories", async Task<IResult> () =>
{
    var items = new List<CategoryDto>();
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand("SELECT category_id, name FROM categories ORDER BY name", connection);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) items.Add(new CategoryDto(reader.GetInt32(0), reader.GetString(1)));
    return Results.Ok(new ItemsResponse<CategoryDto>(items));
});

app.MapGet("/api/catalog", async Task<IResult> (string? search, int? categoryId) =>
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
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.AddWithValue("q", NpgsqlDbType.Text, search?.Trim() ?? string.Empty);
    command.Parameters.AddWithValue("cat", NpgsqlDbType.Integer, (object?)categoryId ?? DBNull.Value);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        items.Add(new AttractionDto(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            (double)reader.GetDecimal(4), (double)reader.GetDecimal(5), reader.GetString(6), reader.GetString(7), reader.GetString(8)));
    return Results.Ok(new ItemsResponse<AttractionDto>(items));
});

app.MapGet("/api/me/visits", async Task<IResult> (HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var items = new List<VisitDto>();
    await using var connection = await OpenAsync(connectionString);
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
    await using (var reader = await command.ExecuteReaderAsync())
        while (await reader.ReadAsync())
            items.Add(new VisitDto(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetDateTime(5).ToString("yyyy-MM-dd"), reader.GetInt16(6), reader.GetString(7), reader.GetString(8), reader.GetInt64(9)));

    await using (var schemaCheck = new NpgsqlCommand("SELECT to_regclass('public.user_map_places') IS NOT NULL", connection))
    {
        if (Equals(await schemaCheck.ExecuteScalarAsync(), true))
        {
            await using var customCommand = new NpgsqlCommand(@"SELECT user_map_place_id, name, visit_date, rating, impressions, notes,
                (SELECT count(*) FROM user_map_place_photos p WHERE p.user_map_place_id=m.user_map_place_id)
                FROM user_map_places m WHERE user_id=@user AND status='visited'", connection);
            customCommand.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            await using var customReader = await customCommand.ExecuteReaderAsync();
            while (await customReader.ReadAsync())
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

app.MapGet("/api/me/wishlist", async Task<IResult> (HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var items = new List<int>();
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand("SELECT attraction_id FROM wishlist WHERE user_id = @u ORDER BY added_at DESC", connection);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) items.Add(reader.GetInt32(0));
    return Results.Ok(new IntItemsResponse(items));
});

app.MapPost("/api/me/wishlist/{attractionId:int}", async Task<IResult> (int attractionId, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand(@"INSERT INTO wishlist(user_id, attraction_id) VALUES (@u, @a)
        ON CONFLICT (user_id, attraction_id) DO NOTHING", connection);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    command.Parameters.AddWithValue("a", NpgsqlDbType.Integer, attractionId);
    await command.ExecuteNonQueryAsync();
    return Results.Ok(new MessageDto("Добавлено в список «Хочу посетить»."));
});

app.MapDelete("/api/me/wishlist/{attractionId:int}", async Task<IResult> (int attractionId, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand("DELETE FROM wishlist WHERE user_id = @u AND attraction_id = @a", connection);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    command.Parameters.AddWithValue("a", NpgsqlDbType.Integer, attractionId);
    await command.ExecuteNonQueryAsync();
    return Results.Ok(new MessageDto("Удалено из списка."));
});

app.MapPost("/api/me/visits/save", async Task<IResult> (HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var form = await context.Request.ReadFormAsync();
    if (!int.TryParse(form["attractionId"], out var attractionId) || attractionId <= 0)
        return Results.BadRequest(new ErrorDto("Выберите достопримечательность."));
    if (!int.TryParse(form["rating"], out var rating) || rating is < 1 or > 5)
        return Results.BadRequest(new ErrorDto("Оценка должна быть от 1 до 5."));
    if (!DateOnly.TryParse(form["visitDate"], out var visitDate) || visitDate > DateOnly.FromDateTime(DateTime.Today))
        return Results.BadRequest(new ErrorDto("Укажите корректную дату посещения, не позднее сегодняшнего дня."));
    int? visitId = int.TryParse(form["visitId"], out var parsedVisitId) && parsedVisitId > 0 ? parsedVisitId : null;
    var files = form.Files.GetFiles("photos");
    if (files.Count > 10) return Results.BadRequest(new ErrorDto("За один раз можно добавить не более 10 фотографий."));
    if (files.Any(f => f.Length == 0 || f.Length > 20 * 1024 * 1024) || files.Sum(f => f.Length) > 20 * 1024 * 1024)
        return Results.BadRequest(new ErrorDto("Общий размер фотографий за одно сохранение не должен превышать 20 МБ."));
    var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
    if (files.Any(f => !allowedExtensions.Contains(Path.GetExtension(f.FileName).ToLowerInvariant())))
        return Results.BadRequest(new ErrorDto("Поддерживаются JPG, PNG, WEBP, BMP и GIF."));

    var writtenFiles = new List<string>();
    try
    {
        await using var connection = await OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        int savedVisitId;
        if (visitId is null)
        {
            await using var insert = new NpgsqlCommand(@"INSERT INTO visits(user_id, attraction_id, visit_date, rating, impressions, notes)
                VALUES (@u, @a, @d, @r, @i, @n) RETURNING visit_id", connection, transaction);
            AddVisitParameters(insert, userId, new VisitWriteRequest(attractionId, visitDate.ToString("yyyy-MM-dd"), rating,
                form["impressions"].ToString(), form["notes"].ToString()), visitDate);
            savedVisitId = Convert.ToInt32(await insert.ExecuteScalarAsync());
        }
        else
        {
            await using var update = new NpgsqlCommand(@"UPDATE visits SET attraction_id=@a, visit_date=@d, rating=@r,
                impressions=@i, notes=@n WHERE visit_id=@v AND user_id=@u", connection, transaction);
            AddVisitParameters(update, userId, new VisitWriteRequest(attractionId, visitDate.ToString("yyyy-MM-dd"), rating,
                form["impressions"].ToString(), form["notes"].ToString()), visitDate);
            update.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId.Value);
            if (await update.ExecuteNonQueryAsync() == 0) return Results.NotFound(new ErrorDto("Посещение не найдено."));
            savedVisitId = visitId.Value;
        }

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var fileName = $"{Guid.NewGuid():N}{extension}";
            var relativePath = $"Photos/{fileName}";
            var fullPath = Path.Combine(photosRoot, fileName);
            writtenFiles.Add(relativePath); // Удалится даже частично записанный файл при откате.
            await using (var stream = File.Create(fullPath)) await file.CopyToAsync(stream);
            var caption = Path.GetFileNameWithoutExtension(file.FileName);
            if (caption.Length > 200) caption = caption[..200];
            await using var photoInsert = new NpgsqlCommand(
                "INSERT INTO photos(visit_id, file_path, caption) VALUES (@v, @p, @c)", connection, transaction);
            photoInsert.Parameters.AddWithValue("v", NpgsqlDbType.Integer, savedVisitId);
            photoInsert.Parameters.AddWithValue("p", NpgsqlDbType.Varchar, relativePath);
            photoInsert.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, caption);
            await photoInsert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return Results.Ok(new VisitSavedDto(savedVisitId, "Посещение и фотографии сохранены."));
    }
    catch
    {
        foreach (var relativePath in writtenFiles) TryDeletePhoto(dataRoot, relativePath);
        throw;
    }
});

app.MapPost("/api/me/visits", async Task<IResult> (VisitWriteRequest request, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    return await SaveVisitAsync(connectionString!, request, userId, null);
});

app.MapPut("/api/me/visits/{visitId:int}", async Task<IResult> (int visitId, VisitWriteRequest request, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    return await SaveVisitAsync(connectionString!, request, userId, visitId);
});

app.MapDelete("/api/me/visits/{visitId:int}", async Task<IResult> (int visitId, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var storedPaths = new List<string>();
    await using (var connection = await OpenAsync(connectionString))
    await using (var transaction = await connection.BeginTransactionAsync())
    {
        await using var photosCommand = new NpgsqlCommand(@"SELECT p.file_path FROM photos p
            JOIN visits v ON v.visit_id = p.visit_id WHERE v.visit_id = @v AND v.user_id = @u", connection, transaction);
        photosCommand.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId);
        photosCommand.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
        await using (var reader = await photosCommand.ExecuteReaderAsync())
            while (await reader.ReadAsync()) storedPaths.Add(reader.GetString(0));
        await using var delete = new NpgsqlCommand("DELETE FROM visits WHERE visit_id = @v AND user_id = @u", connection, transaction);
        delete.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId);
        delete.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
        if (await delete.ExecuteNonQueryAsync() == 0) return Results.NotFound(new ErrorDto("Посещение не найдено."));
        await transaction.CommitAsync();
    }
    foreach (var path in storedPaths) TryDeletePhoto(dataRoot, path);
    return Results.Ok(new MessageDto("Посещение удалено."));
});

app.MapPost("/api/me/visits/{visitId:int}/photos", async Task<IResult> (int visitId, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest(new ErrorDto("Файл не выбран."));
    if (file.Length > 20 * 1024 * 1024) return Results.BadRequest(new ErrorDto("Размер фотографии не должен превышать 20 МБ."));
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    var allowed = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
    if (!allowed.Contains(extension)) return Results.BadRequest(new ErrorDto("Поддерживаются JPG, PNG, WEBP, BMP и GIF."));

    await using var connection = await OpenAsync(connectionString);
    await using var ownership = new NpgsqlCommand("SELECT 1 FROM visits WHERE visit_id = @v AND user_id = @u", connection);
    ownership.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId);
    ownership.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    if (await ownership.ExecuteScalarAsync() is null) return Results.NotFound(new ErrorDto("Посещение не найдено."));

    var fileName = $"{Guid.NewGuid():N}{extension}";
    var fullPath = Path.Combine(photosRoot, fileName);
    await using (var stream = File.Create(fullPath)) await file.CopyToAsync(stream);
    var relativePath = $"Photos/{fileName}";
    try
    {
        await using var insert = new NpgsqlCommand("INSERT INTO photos(visit_id, file_path, caption) VALUES (@v, @p, @c) RETURNING photo_id", connection);
        insert.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId);
        insert.Parameters.AddWithValue("p", NpgsqlDbType.Varchar, relativePath);
        insert.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, (form["caption"].ToString() ?? string.Empty)[..Math.Min(200, form["caption"].ToString().Length)]);
        var id = Convert.ToInt32(await insert.ExecuteScalarAsync());
        return Results.Ok(new PhotoDto(id, relativePath, form["caption"].ToString()));
    }
    catch { TryDeletePhoto(dataRoot, relativePath); throw; }
});

app.MapGet("/api/me/visits/{visitId:int}/photos", async Task<IResult> (int visitId, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var items = new List<PhotoDto>();
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand(@"SELECT p.photo_id, p.file_path, p.caption FROM photos p
        JOIN visits v ON v.visit_id = p.visit_id WHERE v.visit_id = @v AND v.user_id = @u ORDER BY p.added_at", connection);
    command.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) items.Add(new PhotoDto(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
    return Results.Ok(new ItemsResponse<PhotoDto>(items));
});

app.MapGet("/api/me/photos/{photoId:int}", async Task<IResult> (int photoId, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand(@"SELECT p.file_path FROM photos p
        JOIN visits v ON v.visit_id = p.visit_id WHERE p.photo_id = @p AND v.user_id = @u", connection);
    command.Parameters.AddWithValue("p", NpgsqlDbType.Integer, photoId);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    var relativePath = await command.ExecuteScalarAsync() as string;
    if (relativePath is null) return Results.NotFound();
    var fullPath = SafePhotoPath(dataRoot, relativePath);
    if (fullPath is null || !File.Exists(fullPath)) return Results.NotFound();
    return Results.File(fullPath, ContentTypeFor(fullPath));
});

app.MapDelete("/api/me/photos/{photoId:int}", async Task<IResult> (int photoId, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand(@"DELETE FROM photos p USING visits v
        WHERE p.visit_id = v.visit_id AND p.photo_id = @p AND v.user_id = @u RETURNING p.file_path", connection);
    command.Parameters.AddWithValue("p", NpgsqlDbType.Integer, photoId);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    var path = await command.ExecuteScalarAsync() as string;
    if (path is null) return Results.NotFound();
    TryDeletePhoto(dataRoot, path);
    return Results.Ok(new MessageDto("Фотография удалена."));
});

app.MapGet("/api/me/summary", async Task<IResult> (HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand(@"
SELECT count(DISTINCT v.visit_id), count(DISTINCT v.attraction_id),
       count(DISTINCT ci.city_id), count(DISTINCT co.country_id)
FROM visits v
JOIN attractions a ON a.attraction_id = v.attraction_id
JOIN cities ci ON ci.city_id = a.city_id
JOIN countries co ON co.country_id = ci.country_id
WHERE v.user_id = @u", connection);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    var visitCount = reader.GetInt64(0);
    var distinctAttractions = reader.GetInt64(1);
    var cities = reader.GetInt64(2);
    var countries = reader.GetInt64(3);
    await reader.DisposeAsync();
    long customVisits = 0;
    await using (var schemaCheck = new NpgsqlCommand("SELECT to_regclass('public.user_map_places') IS NOT NULL", connection))
    {
        if (Equals(await schemaCheck.ExecuteScalarAsync(), true))
        {
            await using var customCount = new NpgsqlCommand(@"SELECT count(*) FROM user_map_places
                WHERE user_id=@u AND status='visited'", connection);
            customCount.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
            customVisits = Convert.ToInt64(await customCount.ExecuteScalarAsync());
        }
    }
    return Results.Ok(new SummaryDto(visitCount + customVisits, distinctAttractions + customVisits, cities, countries));
});

app.MapGet("/api/me/map-markers", async Task<IResult> (HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var items = new List<MapMarkerDto>();
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand(@"
SELECT a.attraction_id, a.name, a.latitude, a.longitude,
       EXISTS(SELECT 1 FROM visits v WHERE v.user_id = @u AND v.attraction_id = a.attraction_id),
       EXISTS(SELECT 1 FROM wishlist w WHERE w.user_id = @u AND w.attraction_id = a.attraction_id)
FROM attractions a ORDER BY a.name", connection);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    await using (var reader = await command.ExecuteReaderAsync())
        while (await reader.ReadAsync()) items.Add(new MapMarkerDto(reader.GetInt32(0), reader.GetString(1),
            (double)reader.GetDecimal(2), (double)reader.GetDecimal(3), reader.GetBoolean(4), reader.GetBoolean(5)));

    await using var customCommand = new NpgsqlCommand(@"
SELECT user_map_place_id, name, latitude, longitude, status, visit_date, rating, impressions, notes
FROM user_map_places WHERE user_id = @u ORDER BY created_at", connection);
    customCommand.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    await using var customReader = await customCommand.ExecuteReaderAsync();
    while (await customReader.ReadAsync())
        items.Add(new MapMarkerDto(customReader.GetInt32(0), customReader.GetString(1),
            (double)customReader.GetDecimal(2), (double)customReader.GetDecimal(3),
            customReader.GetString(4) == "visited", customReader.GetString(4) == "planned",
            customReader.GetInt32(0), customReader.IsDBNull(5) ? null : customReader.GetDateTime(5).ToString("yyyy-MM-dd"),
            customReader.IsDBNull(6) ? null : customReader.GetInt16(6), customReader.GetString(7), customReader.GetString(8)));
    return Results.Ok(new ItemsResponse<MapMarkerDto>(items));
});

app.MapPost("/api/me/map-places", async Task<IResult> (MapPlaceCreateRequest request, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var name = (request.Name ?? string.Empty).Trim();
    if (name.Length is < 1 or > 150) return Results.BadRequest(new ErrorDto("Название должно содержать от 1 до 150 символов."));
    if (!double.IsFinite(request.Latitude) || request.Latitude is < -90 or > 90 ||
        !double.IsFinite(request.Longitude) || request.Longitude is < -180 or > 180)
        return Results.BadRequest(new ErrorDto("Укажите корректные координаты места."));
    var status = string.IsNullOrWhiteSpace(request.Status) ? "planned" : request.Status.Trim().ToLowerInvariant();
    if (status is not ("planned" or "visited")) return Results.BadRequest(new ErrorDto("Выберите статус места."));
    DateOnly? visitDate = null;
    int? rating = null;
    if (status == "visited")
    {
        if (!DateOnly.TryParse(request.VisitDate, out var parsedDate) || parsedDate > DateOnly.FromDateTime(DateTime.Today))
            return Results.BadRequest(new ErrorDto("Укажите корректную дату посещения, не позднее сегодняшнего дня."));
        if (request.Rating is < 1 or > 5) return Results.BadRequest(new ErrorDto("Оценка должна быть от 1 до 5."));
        visitDate = parsedDate;
        rating = request.Rating;
    }

    await using var connection = await OpenAsync(connectionString);
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
    var id = Convert.ToInt32(await command.ExecuteScalarAsync());
    return Results.Ok(new MapPlaceSavedDto(id, status == "visited" ? "Посещение сохранено." : "Место добавлено в планы."));
});

app.MapPut("/api/me/map-places/{placeId:int}", async Task<IResult> (int placeId, MapPlaceUpdateRequest request, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var status = (request.Status ?? string.Empty).Trim().ToLowerInvariant();
    if (status is not ("planned" or "visited")) return Results.BadRequest(new ErrorDto("Выберите статус места."));
    DateOnly? visitDate = null;
    int? rating = null;
    if (status == "visited")
    {
        if (!DateOnly.TryParse(request.VisitDate, out var parsedDate) || parsedDate > DateOnly.FromDateTime(DateTime.Today))
            return Results.BadRequest(new ErrorDto("Укажите корректную дату посещения, не позднее сегодняшнего дня."));
        if (request.Rating is < 1 or > 5) return Results.BadRequest(new ErrorDto("Оценка должна быть от 1 до 5."));
        visitDate = parsedDate;
        rating = request.Rating;
    }
    var name = (request.Name ?? string.Empty).Trim();
    if (name.Length is < 1 or > 150) return Results.BadRequest(new ErrorDto("Название должно содержать от 1 до 150 символов."));

    await using var connection = await OpenAsync(connectionString);
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
    if (await command.ExecuteNonQueryAsync() == 0) return Results.NotFound(new ErrorDto("Место не найдено."));
    return Results.Ok(new MessageDto("Место обновлено."));
});

app.MapDelete("/api/me/map-places/{placeId:int}", async Task<IResult> (int placeId, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var photoPaths = new List<string>();
    await using (var connection = await OpenAsync(connectionString))
    await using (var transaction = await connection.BeginTransactionAsync())
    {
        await using var photos = new NpgsqlCommand(@"SELECT p.file_path FROM user_map_place_photos p
            JOIN user_map_places m ON m.user_map_place_id=p.user_map_place_id
            WHERE m.user_map_place_id=@p AND m.user_id=@u", connection, transaction);
        photos.Parameters.AddWithValue("p", NpgsqlDbType.Integer, placeId);
        photos.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
        await using (var reader = await photos.ExecuteReaderAsync())
            while (await reader.ReadAsync()) photoPaths.Add(reader.GetString(0));
        await using var delete = new NpgsqlCommand("DELETE FROM user_map_places WHERE user_map_place_id=@p AND user_id=@u", connection, transaction);
        delete.Parameters.AddWithValue("p", NpgsqlDbType.Integer, placeId);
        delete.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
        if (await delete.ExecuteNonQueryAsync() == 0) return Results.NotFound(new ErrorDto("Место не найдено."));
        await transaction.CommitAsync();
    }
    foreach (var path in photoPaths) TryDeletePhoto(dataRoot, path);
    return Results.Ok(new MessageDto("Место удалено."));
});

app.MapGet("/api/me/map-places/{placeId:int}/photos", async Task<IResult> (int placeId, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var items = new List<PhotoDto>();
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand(@"SELECT p.photo_id, p.file_path, p.caption FROM user_map_place_photos p
        JOIN user_map_places m ON m.user_map_place_id=p.user_map_place_id
        WHERE m.user_map_place_id=@p AND m.user_id=@u ORDER BY p.added_at", connection);
    command.Parameters.AddWithValue("p", NpgsqlDbType.Integer, placeId);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) items.Add(new PhotoDto(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
    return Results.Ok(new ItemsResponse<PhotoDto>(items));
});

app.MapPost("/api/me/map-places/{placeId:int}/photos", async Task<IResult> (int placeId, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    var form = await context.Request.ReadFormAsync();
    var files = form.Files.GetFiles("photos");
    if (files.Count is < 1 or > 10) return Results.BadRequest(new ErrorDto("Выберите от 1 до 10 фотографий."));
    if (files.Any(f => f.Length == 0 || f.Length > 20 * 1024 * 1024) || files.Sum(f => f.Length) > 20 * 1024 * 1024)
        return Results.BadRequest(new ErrorDto("Общий размер фотографий не должен превышать 20 МБ."));
    var allowed = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
    if (files.Any(f => !allowed.Contains(Path.GetExtension(f.FileName).ToLowerInvariant())))
        return Results.BadRequest(new ErrorDto("Поддерживаются JPG, PNG, WEBP, BMP и GIF."));

    var writtenPaths = new List<string>();
    try
    {
        await using var connection = await OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await using var ownership = new NpgsqlCommand("SELECT 1 FROM user_map_places WHERE user_map_place_id=@p AND user_id=@u", connection, transaction);
        ownership.Parameters.AddWithValue("p", NpgsqlDbType.Integer, placeId);
        ownership.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
        if (await ownership.ExecuteScalarAsync() is null) return Results.NotFound(new ErrorDto("Место не найдено."));

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var fileName = $"{Guid.NewGuid():N}{extension}";
            var relativePath = $"Photos/{fileName}";
            writtenPaths.Add(relativePath);
            await using (var stream = File.Create(Path.Combine(photosRoot, fileName))) await file.CopyToAsync(stream);
            var caption = Path.GetFileNameWithoutExtension(file.FileName);
            if (caption.Length > 200) caption = caption[..200];
            await using var insert = new NpgsqlCommand(@"INSERT INTO user_map_place_photos(user_map_place_id, file_path, caption)
                VALUES (@p, @f, @c)", connection, transaction);
            insert.Parameters.AddWithValue("p", NpgsqlDbType.Integer, placeId);
            insert.Parameters.AddWithValue("f", NpgsqlDbType.Varchar, relativePath);
            insert.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, caption);
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return Results.Ok(new MessageDto("Фотографии сохранены."));
    }
    catch
    {
        foreach (var path in writtenPaths) TryDeletePhoto(dataRoot, path);
        throw;
    }
});

app.MapGet("/api/me/map-place-photos/{photoId:int}", async Task<IResult> (int photoId, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand(@"SELECT p.file_path FROM user_map_place_photos p
        JOIN user_map_places m ON m.user_map_place_id=p.user_map_place_id
        WHERE p.photo_id=@p AND m.user_id=@u", connection);
    command.Parameters.AddWithValue("p", NpgsqlDbType.Integer, photoId);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    var relativePath = await command.ExecuteScalarAsync() as string;
    if (relativePath is null) return Results.NotFound();
    var fullPath = SafePhotoPath(dataRoot, relativePath);
    if (fullPath is null || !File.Exists(fullPath)) return Results.NotFound();
    return Results.File(fullPath, ContentTypeFor(fullPath));
});

app.MapDelete("/api/me/map-place-photos/{photoId:int}", async Task<IResult> (int photoId, HttpContext context, SessionStore sessions) =>
{
    if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
    await using var connection = await OpenAsync(connectionString);
    await using var command = new NpgsqlCommand(@"DELETE FROM user_map_place_photos p USING user_map_places m
        WHERE p.user_map_place_id=m.user_map_place_id AND p.photo_id=@p AND m.user_id=@u RETURNING p.file_path", connection);
    command.Parameters.AddWithValue("p", NpgsqlDbType.Integer, photoId);
    command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
    var path = await command.ExecuteScalarAsync() as string;
    if (path is null) return Results.NotFound();
    TryDeletePhoto(dataRoot, path);
    return Results.Ok(new MessageDto("Фотография удалена."));
});

app.MapGet("/map", IResult () =>
{
    var path = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "map.html");
    return File.Exists(path) ? Results.Content(File.ReadAllText(path), "text/html; charset=utf-8") : Results.NotFound();
});

app.Run();

static string? ReadLocalConnectionString(string configPath)
{
    try
    {
        if (!File.Exists(configPath)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        return document.RootElement.TryGetProperty("connectionString", out var value)
            ? value.GetString()
            : null;
    }
    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
    {
        return null;
    }
}

static async Task<NpgsqlConnection> OpenAsync(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Connection string not configured.");
    var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    return connection;
}

static async Task<IResult> SaveVisitAsync(string connectionString, VisitWriteRequest request, int userId, int? visitId)
{
    if (request.AttractionId <= 0) return Results.BadRequest(new ErrorDto("Выберите достопримечательность."));
    if (!DateOnly.TryParse(request.VisitDate, out var date) || date > DateOnly.FromDateTime(DateTime.Today))
        return Results.BadRequest(new ErrorDto("Укажите корректную дату посещения, не позднее сегодняшнего дня."));
    if (request.Rating is < 1 or > 5) return Results.BadRequest(new ErrorDto("Оценка должна быть от 1 до 5."));
    int id;
    await using (var connection = await OpenAsync(connectionString))
    await using (var transaction = await connection.BeginTransactionAsync())
    {
        if (visitId is null)
        {
            await using var insert = new NpgsqlCommand(@"INSERT INTO visits(user_id, attraction_id, visit_date, rating, impressions, notes)
                VALUES (@u, @a, @d, @r, @i, @n) RETURNING visit_id", connection, transaction);
            AddVisitParameters(insert, userId, request, date);
            id = Convert.ToInt32(await insert.ExecuteScalarAsync());
        }
        else
        {
            await using var update = new NpgsqlCommand(@"UPDATE visits SET attraction_id=@a, visit_date=@d, rating=@r,
                impressions=@i, notes=@n WHERE visit_id=@v AND user_id=@u", connection, transaction);
            AddVisitParameters(update, userId, request, date);
            update.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId.Value);
            if (await update.ExecuteNonQueryAsync() == 0) return Results.NotFound(new ErrorDto("Посещение не найдено."));
            id = visitId.Value;
        }
        await transaction.CommitAsync();
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
    userId = 0;
    var header = context.Request.Headers["Authorization"].ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
    return sessions.TryGet(header[7..].Trim(), out userId);
}

static string HashPassword(string password)
{
    const int iterations = 100_000;
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
    return $"{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
}

static bool VerifyPassword(string password, string stored)
{
    try
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations) || iterations is < 10_000 or > 2_000_000) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
    catch (FormatException) { return false; }
    catch (ArgumentException) { return false; }
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

static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", ".bmp" => "image/bmp", _ => "image/jpeg"
};

sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, int> _sessions = new();
    public string Create(int userId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _sessions[token] = userId;
        return token;
    }
    public bool TryGet(string token, out int userId) => _sessions.TryGetValue(token, out userId);
}

record RegisterRequest(string Login, string Password, string DisplayName);
record LoginRequest(string Login, string Password);
record UserDto(int Id, string Login, string DisplayName);
record AuthResponse(UserDto User, string Token);
record ErrorDto(string Error);
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
