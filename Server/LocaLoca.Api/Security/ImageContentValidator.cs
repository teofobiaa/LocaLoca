using System.Text;

namespace LocaLoca.Api.Security;

public static class ImageContentValidator
{
    public static bool HasValidImageSignature(byte[] bytes, string extension)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" =>
                bytes.Length >= 3 &&
                bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,

            ".png" =>
                bytes.Length >= 8 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 &&
                bytes[2] == 0x4E && bytes[3] == 0x47 &&
                bytes[4] == 0x0D && bytes[5] == 0x0A &&
                bytes[6] == 0x1A && bytes[7] == 0x0A,

            ".webp" =>
                bytes.Length >= 12 &&
                Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" &&
                Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP",

            ".gif" =>
                bytes.Length >= 6 &&
                Encoding.ASCII.GetString(bytes, 0, 3) == "GIF" &&
                Encoding.ASCII.GetString(bytes, 3, 3) is "87a" or "89a",

            ".bmp" =>
                bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D,

            _ => false
        };
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
}
