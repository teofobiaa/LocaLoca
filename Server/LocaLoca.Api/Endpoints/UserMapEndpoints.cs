using Npgsql;
using NpgsqlTypes;
using LocaLoca.Api;
using LocaLoca.Api.Security;
using static LocaLoca.Api.ApiEndpointHelpers;
namespace LocaLoca.Api.Endpoints;

public static class UserMapEndpoints
{
    public static void MapUserMapEndpoints(this WebApplication app, string? connectionString, string dataRoot)
    {
        const int DefaultMapPageSize = 500;
        const int MaxMapPageSize = 1000;
        
        app.MapGet("/api/me/map-markers", async Task<IResult> (
            HttpContext context, SessionStore sessions,
            int? page, int? pageSize,
            double? south, double? west, double? north, double? east,
            CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
        
            var currentPage = page ?? 1;
            var currentPageSize = pageSize ?? DefaultMapPageSize;
            if (currentPage < 1 || currentPageSize is < 1 or > MaxMapPageSize)
                return Results.BadRequest(new ApiErrorResponse($"Параметры пагинации карты должны задавать страницу от 1 и размер от 1 до {MaxMapPageSize}."));
        
            var hasAnyBounds = south.HasValue || west.HasValue || north.HasValue || east.HasValue;
            var hasAllBounds = south.HasValue && west.HasValue && north.HasValue && east.HasValue;
            if (hasAnyBounds && !hasAllBounds)
                return Results.BadRequest(new ApiErrorResponse("Для области карты необходимо передать south, west, north и east."));
        
            var hasBounds = hasAllBounds;
            if (hasBounds &&
                (!double.IsFinite(south!.Value) || !double.IsFinite(west!.Value) ||
                 !double.IsFinite(north!.Value) || !double.IsFinite(east!.Value) ||
                 south.Value < -90 || north.Value > 90 || south.Value > north.Value ||
                 west.Value < -180 || west.Value > 180 || east.Value < -180 || east.Value > 180))
                return Results.BadRequest(new ApiErrorResponse("Границы видимой области карты содержат некорректные координаты."));
        
            var offset = ((long)currentPage - 1) * currentPageSize;
            var items = new List<MapMarkerDto>();
            await using var connection = await OpenAsync(connectionString, cancellationToken);
        
            var crossesAntimeridian = hasBounds && west!.Value > east!.Value;

            void AddMarkerParameters(NpgsqlCommand command)
            {
                command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
                if (!hasBounds) return;

                command.Parameters.AddWithValue("south", NpgsqlDbType.Numeric, Convert.ToDecimal(south!.Value));
                command.Parameters.AddWithValue("north", NpgsqlDbType.Numeric, Convert.ToDecimal(north!.Value));
                command.Parameters.AddWithValue("west", NpgsqlDbType.Numeric, Convert.ToDecimal(west!.Value));
                command.Parameters.AddWithValue("east", NpgsqlDbType.Numeric, Convert.ToDecimal(east!.Value));
            }

            var countSql = MapMarkerSqlBuilder.BuildCountSql(hasBounds, crossesAntimeridian);
            await using var countCommand = new NpgsqlCommand(countSql, connection);
            AddMarkerParameters(countCommand);
            var totalCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));

            var sql = MapMarkerSqlBuilder.BuildPageSql(hasBounds, crossesAntimeridian);
            await using var command = new NpgsqlCommand(sql, connection);
            AddMarkerParameters(command);
            command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, currentPageSize);
            command.Parameters.AddWithValue("offset", NpgsqlDbType.Bigint, offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add(new MapMarkerDto(reader.GetInt32(0), reader.GetString(1),
                    (double)reader.GetDecimal(2), (double)reader.GetDecimal(3), reader.GetBoolean(4), reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetDateTime(7).ToString("yyyy-MM-dd"),
                    reader.IsDBNull(8) ? null : reader.GetInt16(8), reader.GetString(9), reader.GetString(10)));
        
            return Results.Ok(new PagedItemsResponse<MapMarkerDto>(
                items, currentPage, currentPageSize, totalCount, (long)currentPage * currentPageSize < totalCount));
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
        
            // Avoid decoding uploads when the map place is foreign or no longer exists.
            await using (var preflightConnection = await OpenAsync(connectionString, cancellationToken))
            await using (var preflightOwnership = new NpgsqlCommand(
                "SELECT 1 FROM user_map_places WHERE user_map_place_id=@p AND user_id=@u", preflightConnection))
            {
                preflightOwnership.Parameters.AddWithValue("p", NpgsqlDbType.Integer, placeId);
                preflightOwnership.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
                if (await preflightOwnership.ExecuteScalarAsync(cancellationToken) is null)
                    return Results.NotFound(new ApiErrorResponse("Место не найдено."));
            }
        
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
                var imageValidation = ImageContentValidator.Validate(data, extension, cancellationToken);
                if (!imageValidation.IsValid)
                    return Results.BadRequest(new ApiErrorResponse(imageValidation.Error ?? "Изображение не прошло проверку."));
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
    }
}
