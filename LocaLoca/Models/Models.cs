using Microsoft.UI.Xaml.Media.Imaging;

namespace LocaLoca.Models;

public sealed record HealthDto(bool ApiConfigured, bool DatabaseConnected, string Message);
public sealed record UserDto(int Id, string Login, string DisplayName, string? ResidenceCountry = null, string? ResidenceCity = null, bool HasAvatar = false);
public sealed record UserSearchResultDto(
    int UserId, string Login, string? DisplayName, bool? HasAvatar, string RelationshipStatus);
public sealed record FriendRequestDto(
    int UserId, string Login, string? DisplayName, bool? HasAvatar, bool IsIncoming, DateTime CreatedAt);
public sealed record UserRelationshipDto(
    int UserId, string Login, string? DisplayName, bool? HasAvatar);

public sealed class FriendListItemViewModel
{
    public int UserId { get; set; }
    public string Login { get; set; }
    public string? DisplayName { get; set; }
    public string Initials { get; set; }
    public BitmapImage? AvatarSource { get; set; }

    public string DisplayText => string.IsNullOrWhiteSpace(DisplayName) ? Login : DisplayName;
    public string LoginLabel => "@" + Login;

    public FriendListItemViewModel(
        int userId, string login, string? displayName, string initials, BitmapImage? avatarSource)
    {
        UserId = userId;
        Login = login;
        DisplayName = displayName;
        Initials = initials;
        AvatarSource = avatarSource;
    }
}
public sealed record UserBlockDto(int UserId, string Login);
public sealed record UserProfileViewDto(
    int Id, string? DisplayName, string? ResidenceCountry, string? ResidenceCity, bool? HasAvatar);
public sealed record UpdateDisplayNameRequest(string DisplayName);
public sealed record UpdateResidenceRequest(string? ResidenceCountry, string? ResidenceCity);
public sealed record UpdateBirthDateRequest(DateOnly? DateOfBirth);
public sealed record PrivateBirthDateDto(DateOnly? DateOfBirth);
public sealed record BirthdayContactWriteRequest(string DisplayName, DateOnly BirthDate);
public sealed class BirthdayContactDto
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public DateOnly BirthDate { get; set; }
    public DateOnly NextBirthdayDate { get; set; }
    public int DaysUntilBirthday { get; set; }
    public int AgeOnNextBirthday { get; set; }

    public string ReminderText => DaysUntilBirthday switch
    {
        0 => "Сегодня день рождения",
        1 => "Завтра день рождения",
        _ => $"Через {DaysUntilBirthday} дн."
    };

    public string BirthdayDetails => $"{NextBirthdayDate:dd.MM.yyyy} · исполнится {AgeOnNextBirthday}";
}
public sealed record ProfilePrivacyDto(
    string DisplayNameVisibility,
    string AvatarVisibility,
    string ResidenceCountryVisibility,
    string ResidenceCityVisibility);
public sealed record UpdateProfilePrivacyRequest(
    string DisplayNameVisibility,
    string AvatarVisibility,
    string ResidenceCountryVisibility,
    string ResidenceCityVisibility);
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
public sealed record PagedItemsResponse<T>(List<T> Items, int Page, int PageSize, long TotalCount, bool HasMore);
public sealed record IntItemsResponse(List<int> Items);

public sealed record CategoryChoice(int? Id, string Name)
{
    public override string ToString() => Name;
}
