using Npgsql;
using NpgsqlTypes;
using LocaLoca.Api;
using LocaLoca.Api.Security;
using static LocaLoca.Api.ApiEndpointHelpers;
namespace LocaLoca.Api.Endpoints;

public static class PersonalEndpoints
{
    public static void MapPersonalEndpoints(this WebApplication app, string? connectionString, string dataRoot)
    {
        const int DefaultPageSize = 50;
        const int MaxPageSize = 100;
        
        app.MapGet("/api/me/visits", async Task<IResult> (
            HttpContext context, SessionStore sessions, int? page, int? pageSize, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
        
            var currentPage = page ?? 1;
            var currentPageSize = pageSize ?? DefaultPageSize;
            if (currentPage < 1 || currentPageSize is < 1 or > MaxPageSize)
                return Results.BadRequest(new ApiErrorResponse($"Параметры пагинации должны задавать страницу от 1 и размер от 1 до {MaxPageSize}."));
        
            var offset = ((long)currentPage - 1) * currentPageSize;
            var items = new List<VisitDto>();
            await using var connection = await OpenAsync(connectionString, cancellationToken);
        
            await using var countCommand = new NpgsqlCommand(@"
        SELECT
            (SELECT count(*) FROM visits WHERE user_id = @user) +
            (SELECT count(*) FROM user_map_places WHERE user_id = @user AND status = 'visited')", connection);
            countCommand.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            var totalCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));
        
            await using var command = new NpgsqlCommand(@"
        SELECT visit_id, attraction_id, attraction_name, city, country, visit_date,
               rating, impressions, notes, photo_count, user_map_place_id
        FROM (
            SELECT v.visit_id, v.attraction_id, a.name AS attraction_name, ci.name AS city, co.name AS country,
                   v.visit_date, v.rating, v.impressions, v.notes,
                   (SELECT count(*) FROM photos p WHERE p.visit_id = v.visit_id) AS photo_count,
                   NULL::integer AS user_map_place_id
            FROM visits v
            JOIN attractions a ON a.attraction_id = v.attraction_id
            JOIN cities ci ON ci.city_id = a.city_id
            JOIN countries co ON co.country_id = ci.country_id
            WHERE v.user_id = @user
        
            UNION ALL
        
            SELECT -m.user_map_place_id AS visit_id, 0 AS attraction_id, m.name AS attraction_name,
                   'Личная точка' AS city, '' AS country, m.visit_date, m.rating, m.impressions, m.notes,
                   (SELECT count(*) FROM user_map_place_photos p WHERE p.user_map_place_id = m.user_map_place_id) AS photo_count,
                   m.user_map_place_id
            FROM user_map_places m
            WHERE m.user_id = @user AND m.status = 'visited'
        ) AS combined_visits
        ORDER BY visit_date DESC, visit_id DESC
        LIMIT @limit OFFSET @offset", connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, currentPageSize);
            command.Parameters.AddWithValue("offset", NpgsqlDbType.Bigint, offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add(new VisitDto(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetDateTime(5).ToString("yyyy-MM-dd"), reader.GetInt16(6), reader.GetString(7), reader.GetString(8),
                    reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetInt32(10)));
        
            return Results.Ok(new PagedItemsResponse<VisitDto>(
                items, currentPage, currentPageSize, totalCount, (long)currentPage * currentPageSize < totalCount));
        });
        
        
        app.MapGet("/api/me/wishlist", async Task<IResult> (
            HttpContext context, SessionStore sessions, int? page, int? pageSize, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
        
            var currentPage = page ?? 1;
            var currentPageSize = pageSize ?? DefaultPageSize;
            if (currentPage < 1 || currentPageSize is < 1 or > MaxPageSize)
                return Results.BadRequest(new ApiErrorResponse($"Параметры пагинации должны задавать страницу от 1 и размер от 1 до {MaxPageSize}."));
        
            var offset = ((long)currentPage - 1) * currentPageSize;
            var items = new List<int>();
            await using var connection = await OpenAsync(connectionString, cancellationToken);
        
            await using var countCommand = new NpgsqlCommand("SELECT count(*) FROM wishlist WHERE user_id = @u", connection);
            countCommand.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
            var totalCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));
        
            await using var command = new NpgsqlCommand(@"
        SELECT attraction_id
        FROM wishlist
        WHERE user_id = @u
        ORDER BY added_at DESC, attraction_id DESC
        LIMIT @limit OFFSET @offset", connection);
            command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
            command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, currentPageSize);
            command.Parameters.AddWithValue("offset", NpgsqlDbType.Bigint, offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) items.Add(reader.GetInt32(0));
        
            return Results.Ok(new PagedItemsResponse<int>(
                items, currentPage, currentPageSize, totalCount, (long)currentPage * currentPageSize < totalCount));
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
                var imageValidation = ImageContentValidator.Validate(data, extension, cancellationToken);
                if (!imageValidation.IsValid)
                    return Results.BadRequest(new ApiErrorResponse(imageValidation.Error ?? "Изображение не прошло проверку."));
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
        
            // Avoid parsing and decoding user-controlled bytes for foreign or missing visits.
            await using (var preflightConnection = await OpenAsync(connectionString, cancellationToken))
            await using (var preflightOwnership = new NpgsqlCommand(
                "SELECT 1 FROM visits WHERE visit_id = @v AND user_id = @u", preflightConnection))
            {
                preflightOwnership.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId);
                preflightOwnership.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
                if (await preflightOwnership.ExecuteScalarAsync(cancellationToken) is null)
                    return Results.NotFound(new ApiErrorResponse("Посещение не найдено."));
            }
        
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
            var imageValidation = ImageContentValidator.Validate(data, extension, cancellationToken);
            if (!imageValidation.IsValid)
                return Results.BadRequest(new ApiErrorResponse(imageValidation.Error ?? "Изображение не прошло проверку."));
        
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
    }
}
