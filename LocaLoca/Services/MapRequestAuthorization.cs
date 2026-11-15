namespace LocaLoca.Services;

internal static class MapRequestAuthorization
{
    public static string? CreateHeader(Uri apiBaseAddress, string? token, Uri requestUri)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        ArgumentNullException.ThrowIfNull(requestUri);

        if (string.IsNullOrWhiteSpace(token) ||
            !apiBaseAddress.IsAbsoluteUri ||
            !requestUri.IsAbsoluteUri ||
            !string.Equals(
                apiBaseAddress.GetLeftPart(UriPartial.Authority),
                requestUri.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!apiBaseAddress.IsBaseOf(requestUri)) return null;

        var relativeUri = apiBaseAddress.MakeRelativeUri(requestUri);
        if (relativeUri.IsAbsoluteUri) return null;

        // MakeRelativeUri returns a relative Uri, so AbsolutePath is not available.
        // Keep its escaped path representation and reject encoded separators rather
        // than decoding them into a different route before applying the allowlist.
        var relativePath = relativeUri.OriginalString;
        var queryIndex = relativePath.IndexOfAny(new[] { '?', '#' });
        if (queryIndex >= 0)
            relativePath = relativePath[..queryIndex];

        if (relativePath.Contains('%'))
            return null;

        var path = relativePath.TrimStart('/');
        if (path.Any(character =>
                !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '/' or '-')))
        {
            return null;
        }

        var segments = path.Split('/');
        var isMapMarkers = segments.Length == 3 &&
            IsSegment(segments[0], "api") && IsSegment(segments[1], "me") &&
            IsSegment(segments[2], "map-markers");
        var isMapPlacesCollection = segments.Length == 3 &&
            IsSegment(segments[0], "api") && IsSegment(segments[1], "me") &&
            IsSegment(segments[2], "map-places");
        var isMapPlace = segments.Length == 4 &&
            IsSegment(segments[0], "api") && IsSegment(segments[1], "me") &&
            IsSegment(segments[2], "map-places") && IsPositiveId(segments[3]);
        var isMapPlacePhotos = segments.Length == 5 &&
            IsSegment(segments[0], "api") && IsSegment(segments[1], "me") &&
            IsSegment(segments[2], "map-places") && IsPositiveId(segments[3]) &&
            IsSegment(segments[4], "photos");
        var isMapPlacePhoto = segments.Length == 4 &&
            IsSegment(segments[0], "api") && IsSegment(segments[1], "me") &&
            IsSegment(segments[2], "map-place-photos") && IsPositiveId(segments[3]);

        return isMapMarkers || isMapPlacesCollection || isMapPlace || isMapPlacePhotos || isMapPlacePhoto
            ? $"Bearer {token}"
            : null;
    }

    private static bool IsSegment(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsPositiveId(string value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0;
}
