using Npgsql;
using NpgsqlTypes;
using SkiaSharp;
using static LocaLoca.Api.ApiEndpointHelpers;

namespace LocaLoca.Api.Endpoints;

public static class AvatarEndpoints
{
    public const long MaximumUploadBytes = 5L * 1024 * 1024;
    private const int MaximumStoredImageBytes = 1024 * 1024;
    private const int MaximumStoredThumbnailBytes = 128 * 1024;
    private const int MaximumAvatarDimension = 512;
    private const int ThumbnailDimension = 96;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"
    };

    public static void MapAvatarEndpoints(this WebApplication app, string? connectionString)
    {
        app.MapGet("/api/me/avatar", async Task<IResult> (
            HttpContext context, SessionStore sessions, string? size, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();

            if (size is not null &&
                !size.Equals("full", StringComparison.OrdinalIgnoreCase) &&
                !size.Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new ApiErrorResponse("Размер аватара должен быть full или thumbnail."));

            var useThumbnail = string.Equals(size, "thumbnail", StringComparison.OrdinalIgnoreCase);
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                SELECT image_data, thumbnail_data, content_type
                FROM public.user_avatars
                WHERE user_id = @user", connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return Results.NotFound(new ApiErrorResponse("Аватар не задан."));

            var data = reader.GetFieldValue<byte[]>(useThumbnail ? 1 : 0);
            var contentType = reader.GetString(2);
            context.Response.Headers.CacheControl = "private, max-age=300";
            return Results.File(data, contentType);
        });

        app.MapPut("/api/me/avatar", async Task<IResult> (
            HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();

            var form = await context.Request.ReadFormAsync(cancellationToken);
            if (form.Files.Count != 1 || !string.Equals(form.Files[0].Name, "file", StringComparison.Ordinal))
                return Results.BadRequest(new ApiErrorResponse("Выбери один файл аватара."));

            var file = form.Files[0];
            if (file.Length <= 0)
                return Results.BadRequest(new ApiErrorResponse("Файл аватара пуст."));
            if (file.Length > MaximumUploadBytes)
                return Results.Json(
                    new ApiErrorResponse("Размер загружаемого аватара не должен превышать 5 МиБ."),
                    statusCode: StatusCodes.Status413PayloadTooLarge);

            var extension = Path.GetExtension(file.FileName);
            if (!AllowedExtensions.Contains(extension))
                return Results.BadRequest(new ApiErrorResponse("Поддерживаются JPG, PNG, WEBP, BMP и GIF."));

            var bytes = await ReadPhotoDataAsync(file, cancellationToken);
            var validation = ImageContentValidator.Validate(bytes, extension, cancellationToken);
            if (!validation.IsValid)
                return Results.BadRequest(new ApiErrorResponse(validation.Error ?? "Изображение не прошло проверку."));

            var processed = AvatarImageProcessor.Create(bytes);
            if (processed is null)
                return Results.BadRequest(new ApiErrorResponse("Не удалось обработать изображение аватара."));
            if (processed.Value.Image.Length > MaximumStoredImageBytes ||
                processed.Value.Thumbnail.Length > MaximumStoredThumbnailBytes)
                return Results.BadRequest(new ApiErrorResponse("После обработки изображение превышает допустимую квоту хранения."));

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                INSERT INTO public.user_avatars(user_id, image_data, thumbnail_data, content_type, updated_at)
                VALUES (@user, @image, @thumbnail, 'image/jpeg', now())
                ON CONFLICT (user_id) DO UPDATE SET
                    image_data = EXCLUDED.image_data,
                    thumbnail_data = EXCLUDED.thumbnail_data,
                    content_type = EXCLUDED.content_type,
                    updated_at = now()", connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            command.Parameters.AddWithValue("image", NpgsqlDbType.Bytea, processed.Value.Image);
            command.Parameters.AddWithValue("thumbnail", NpgsqlDbType.Bytea, processed.Value.Thumbnail);
            await command.ExecuteNonQueryAsync(cancellationToken);

            return Results.Ok(new MessageDto("Аватар сохранён."));
        });

        app.MapDelete("/api/me/avatar", async Task<IResult> (
            HttpContext context, SessionStore sessions, CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(
                "DELETE FROM public.user_avatars WHERE user_id = @user", connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            await command.ExecuteNonQueryAsync(cancellationToken);

            return Results.Ok(new MessageDto("Аватар удалён."));
        });
    }
}

internal static class AvatarImageProcessor
{
    public static (byte[] Image, byte[] Thumbnail)? Create(byte[] bytes)
    {
        using var source = SKBitmap.Decode(bytes);
        if (source is null || source.Width <= 0 || source.Height <= 0)
            return null;

        var scale = Math.Min(1d, (double)AvatarEndpoints.MaximumUploadBytes / Math.Max(source.Width, source.Height));
        scale = Math.Min(scale, 512d / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var optimized = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(optimized))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(
                source,
                new SKRect(0, 0, source.Width, source.Height),
                new SKRect(0, 0, width, height),
                paint);
            canvas.Flush();
        }

        using var fullData = optimized.Encode(SKEncodedImageFormat.Jpeg, 88);
        if (fullData is null) return null;
        var fullBytes = fullData.ToArray();

        var side = Math.Min(width, height);
        var left = (width - side) / 2f;
        var top = (height - side) / 2f;
        using var thumbnail = new SKBitmap(new SKImageInfo(
            96, 96, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(thumbnail))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(
                optimized,
                new SKRect(left, top, left + side, top + side),
                new SKRect(0, 0, 96, 96),
                paint);
            canvas.Flush();
        }

        using var thumbnailData = thumbnail.Encode(SKEncodedImageFormat.Jpeg, 85);
        if (thumbnailData is null) return null;
        return (fullBytes, thumbnailData.ToArray());
    }
}
