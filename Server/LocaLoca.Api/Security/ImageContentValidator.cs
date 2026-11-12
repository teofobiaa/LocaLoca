using SkiaSharp;

namespace LocaLoca.Api.Security;

public static class ImageContentValidator
{
    // Limits are based on decoded pixels rather than compressed upload size.
    public const int MaximumDimension = 12_000;
    public const int MaximumFrameCount = 100;
    public const long MaximumTotalFramePixels = 25_000_000;
    public const long MaximumDecodedBufferBytes = 128L * 1024 * 1024;

    public static ImageValidationResult Validate(
        byte[] bytes,
        string extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        cancellationToken.ThrowIfCancellationRequested();

        if (bytes.Length == 0)
            return Invalid("Файл изображения пуст.");

        if (!TryGetExpectedFormat(extension, out var expectedFormat))
            return Invalid("Поддерживаются JPG, PNG, WEBP, BMP и GIF.");

        try
        {
            using var encodedData = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(encodedData);
            if (codec is null)
                return Invalid("Файл не является корректным изображением или повреждён.");

            if (codec.EncodedFormat != expectedFormat)
                return Invalid("Расширение файла не соответствует фактическому формату изображения.");

            var width = codec.Info.Width;
            var height = codec.Info.Height;
            if (width <= 0 || height <= 0 ||
                width > MaximumDimension || height > MaximumDimension)
            {
                return Invalid($"Размер изображения не должен превышать {MaximumDimension:N0} пикселей по каждой стороне.");
            }

            // Skia reports zero for a single-frame image.
            var frameCount = codec.FrameCount == 0 ? 1 : codec.FrameCount;
            if (frameCount < 1 || frameCount > MaximumFrameCount)
                return Invalid($"Изображение не должно содержать более {MaximumFrameCount} кадров.");

            var totalFramePixels = (long)width * height * frameCount;
            if (totalFramePixels > MaximumTotalFramePixels)
                return Invalid($"Суммарный размер всех кадров не должен превышать {MaximumTotalFramePixels:N0} пикселей.");

            // Decode into a predictable 32-bit format. One bitmap is reused for every
            // frame, keeping decoded memory bounded by a single frame.
            var outputInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(outputInfo);

            if (bitmap.IsEmpty || bitmap.GetPixels() == IntPtr.Zero || bitmap.RowBytes <= 0)
                return Invalid("Не удалось выделить память для декодирования изображения.");

            var decodedBufferBytes = (long)bitmap.RowBytes * height;
            if (decodedBufferBytes <= 0 || decodedBufferBytes > MaximumDecodedBufferBytes)
                return Invalid("Изображение требует слишком много памяти после декодирования.");

            for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var decodeResult = frameCount == 1
                    ? codec.GetPixels(bitmap.Info, bitmap.GetPixels())
                    : codec.GetPixels(
                        bitmap.Info,
                        bitmap.GetPixels(),
                        bitmap.RowBytes,
                        new SKCodecOptions(frameIndex));

                if (decodeResult != SKCodecResult.Success)
                    return Invalid($"Кадр {frameIndex + 1} не удалось полностью декодировать: {decodeResult}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ImageValidationResult(true, null, width, height, frameCount);
        }
        catch (Exception ex) when (
            ex is not OutOfMemoryException &&
            ex is not OperationCanceledException)
        {
            // Malformed or truncated user-controlled data must not surface as an API 500.
            return Invalid("Изображение повреждено или не может быть полностью декодировано.");
        }
    }

    public static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };

    private static bool TryGetExpectedFormat(string extension, out SKEncodedImageFormat format)
    {
        format = extension?.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            ".gif" => SKEncodedImageFormat.Gif,
            ".bmp" => SKEncodedImageFormat.Bmp,
            _ => SKEncodedImageFormat.Ico
        };

        return extension?.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp";
    }

    private static ImageValidationResult Invalid(string error) =>
        new(false, error, 0, 0, 0);
}

public sealed record ImageValidationResult(
    bool IsValid,
    string? Error,
    int Width,
    int Height,
    int FrameCount);
