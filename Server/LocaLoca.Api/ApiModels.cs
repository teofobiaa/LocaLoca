namespace LocaLoca.Api;

record RegisterRequest(string Login, string Password, string DisplayName);
record LoginRequest(string Login, string Password);
record UpdateDisplayNameRequest(string? DisplayName);
record UpdateResidenceRequest(string? ResidenceCountry, string? ResidenceCity);
record UserDto(int Id, string Login, string DisplayName, string? ResidenceCountry = null, string? ResidenceCity = null);
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
record PagedItemsResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount, bool HasMore);
record IntItemsResponse(IReadOnlyList<int> Items);
