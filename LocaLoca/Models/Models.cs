namespace LocaLoca.Models;

public sealed record HealthDto(bool ApiConfigured, bool DatabaseConnected, string Message);
public sealed record UserDto(int Id, string Login, string DisplayName);
public sealed record AuthResponse(UserDto User, string Token);
public sealed record ErrorDto(string Error);
public sealed record MessageDto(string Message);
public sealed record CategoryDto(int Id, string Name);
public sealed class AttractionDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string History { get; set; } = string.Empty;

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public string Category { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;
}
public sealed record VisitWriteRequest(int AttractionId, string VisitDate, int Rating, string? Impressions, string? Notes);
public sealed class VisitDto
{
    public int Id { get; set; }
    public int AttractionId { get; set; }

    public string AttractionName { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string VisitDate { get; set; } = string.Empty;

    public short Rating { get; set; }
    public string Impressions { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public long PhotoCount { get; set; }
    public int? UserMapPlaceId { get; set; }

    public string RatingText => Rating.ToString();
    public string LocationText => UserMapPlaceId.HasValue ? "Личная точка" : $"{City} · {Country}";
}

public sealed record PhotoDto(int Id, string FilePath, string Caption);
public sealed record MapPlaceUpdateRequest(string Name, string Status, string VisitDate, int Rating, string Impressions, string Notes);
public sealed record SummaryDto(long VisitCount, long DistinctAttractions, long Cities, long Countries);
public sealed record MapMarkerDto(int Id, string Name, double Latitude, double Longitude, bool Visited, bool Wished);
public sealed record ItemsResponse<T>(List<T> Items);
public sealed record IntItemsResponse(List<int> Items);

public sealed record CategoryChoice(int? Id, string Name)
{
    public override string ToString() => Name;
}
