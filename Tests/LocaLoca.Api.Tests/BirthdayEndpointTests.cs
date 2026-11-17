using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LocaLoca.Api.Tests;

[Collection(PostgreSqlApiCollection.Name)]
public sealed class BirthdayEndpointTests
{
    private readonly PostgreSqlApiFixture _database;

    public BirthdayEndpointTests(PostgreSqlApiFixture database) => _database = database;

    [Fact]
    public async Task Full_Date_Of_Birth_Is_Available_Only_Through_Owner_Private_Profile_Route()
    {
        using var factory = _database.CreateFactory(rateLimitClientIp: IPAddress.Parse("192.0.2.101"));
        using var client = factory.CreateClient();
        await RegisterAsync(client);

        using var update = await client.PutAsJsonAsync("/api/me/profile/birthday", new { dateOfBirth = "2001-06-15" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.True(update.Headers.CacheControl?.NoStore == true);

        using var privateResponse = await client.GetAsync("/api/me/profile/birthday");
        Assert.Equal(HttpStatusCode.OK, privateResponse.StatusCode);
        Assert.True(privateResponse.Headers.CacheControl?.NoStore == true);
        using var privateJson = JsonDocument.Parse(await privateResponse.Content.ReadAsStringAsync());
        Assert.Equal("2001-06-15", privateJson.RootElement.GetProperty("dateOfBirth").GetString());

        var profileJson = await client.GetStringAsync("/api/me/profile");
        Assert.DoesNotContain("dateOfBirth", profileJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2001-06-15", profileJson, StringComparison.Ordinal);

        using var invalidUpdate = await client.PutAsJsonAsync("/api/me/profile/birthday", new { dateOfBirth = "2035-01-01" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidUpdate.StatusCode);
    }

    [Fact]
    public async Task Manual_Birthday_Contacts_Api_Is_Retired()
    {
        using var factory = _database.CreateFactory(rateLimitClientIp: IPAddress.Parse("192.0.2.102"));
        using var client = factory.CreateClient();
        await RegisterAsync(client);

        using var list = await client.GetAsync("/api/me/birthday-contacts");
        using var create = await client.PostAsJsonAsync("/api/me/birthday-contacts",
            new { displayName = "Not a profile user", birthDate = "1999-04-06" });
        using var update = await client.PutAsJsonAsync("/api/me/birthday-contacts/1",
            new { displayName = "Not a profile user", birthDate = "1999-04-06" });
        using var delete = await client.DeleteAsync("/api/me/birthday-contacts/1");
        using var calendar = await client.GetAsync("/api/me/birthday-contacts/1/calendar.ics");

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, calendar.StatusCode);
    }

    [Fact]
    public async Task Birthday_Visibility_Respects_Private_Friends_And_Registered_Audiences_Without_Exposing_Year()
    {
        using var factory = _database.CreateFactory(rateLimitClientIp: IPAddress.Parse("192.0.2.104"));
        using var owner = factory.CreateClient();
        using var friend = factory.CreateClient();
        using var registeredViewer = factory.CreateClient();
        await RegisterAsync(owner);
        await RegisterAsync(friend);
        await RegisterAsync(registeredViewer);

        var ownerId = await GetUserIdAsync(owner);
        var friendId = await GetUserIdAsync(friend);

        using var birthDate = await owner.PutAsJsonAsync("/api/me/profile/birthday",
            new { dateOfBirth = "1990-04-06" });
        Assert.Equal(HttpStatusCode.OK, birthDate.StatusCode);

        using var defaultsResponse = await owner.GetAsync("/api/me/profile/privacy");
        Assert.Equal(HttpStatusCode.OK, defaultsResponse.StatusCode);
        using var defaults = JsonDocument.Parse(await defaultsResponse.Content.ReadAsStringAsync());
        Assert.Equal("private", defaults.RootElement.GetProperty("dateOfBirthVisibility").GetString());

        using var privateProfileResponse = await registeredViewer.GetAsync($"/api/users/{ownerId}/profile");
        using (var privateProfile = JsonDocument.Parse(await privateProfileResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, privateProfileResponse.StatusCode);
            Assert.Equal(JsonValueKind.Null, privateProfile.RootElement.GetProperty("birthdayMonth").ValueKind);
            Assert.Equal(JsonValueKind.Null, privateProfile.RootElement.GetProperty("birthdayDay").ValueKind);
            Assert.False(privateProfile.RootElement.TryGetProperty("dateOfBirth", out _));
        }

        using var friendsOnly = await owner.PutAsJsonAsync("/api/me/profile/privacy", new
        {
            displayNameVisibility = "registered",
            avatarVisibility = "registered",
            residenceCountryVisibility = "private",
            residenceCityVisibility = "private",
            dateOfBirthVisibility = "friends"
        });
        Assert.Equal(HttpStatusCode.OK, friendsOnly.StatusCode);

        using var pendingRequest = await friend.PostAsync($"/api/users/{ownerId}/friend-request", content: null);
        Assert.Equal(HttpStatusCode.OK, pendingRequest.StatusCode);
        using var pendingProfileResponse = await friend.GetAsync($"/api/users/{ownerId}/profile");
        using (var pendingProfile = JsonDocument.Parse(await pendingProfileResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, pendingProfileResponse.StatusCode);
            Assert.Equal(JsonValueKind.Null, pendingProfile.RootElement.GetProperty("birthdayMonth").ValueKind);
            Assert.Equal(JsonValueKind.Null, pendingProfile.RootElement.GetProperty("birthdayDay").ValueKind);
        }

        using var accept = await owner.PostAsync($"/api/me/friend-requests/{friendId}/accept", content: null);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        using var friendProfileResponse = await friend.GetAsync($"/api/users/{ownerId}/profile");
        using (var friendProfile = JsonDocument.Parse(await friendProfileResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(4, friendProfile.RootElement.GetProperty("birthdayMonth").GetInt32());
            Assert.Equal(6, friendProfile.RootElement.GetProperty("birthdayDay").GetInt32());
            Assert.False(friendProfile.RootElement.TryGetProperty("dateOfBirth", out _));
            Assert.False(friendProfile.RootElement.TryGetProperty("birthYear", out _));
        }

        using var registeredVisibility = await owner.PutAsJsonAsync("/api/me/profile/privacy", new
        {
            displayNameVisibility = "registered",
            avatarVisibility = "registered",
            residenceCountryVisibility = "private",
            residenceCityVisibility = "private",
            dateOfBirthVisibility = "registered"
        });
        Assert.Equal(HttpStatusCode.OK, registeredVisibility.StatusCode);
        using var registeredProfileResponse = await registeredViewer.GetAsync($"/api/users/{ownerId}/profile");
        using (var registeredProfile = JsonDocument.Parse(await registeredProfileResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(4, registeredProfile.RootElement.GetProperty("birthdayMonth").GetInt32());
            Assert.Equal(6, registeredProfile.RootElement.GetProperty("birthdayDay").GetInt32());
            Assert.False(registeredProfile.RootElement.TryGetProperty("dateOfBirth", out _));
        }

        using var privateAgain = await owner.PutAsJsonAsync("/api/me/profile/privacy", new
        {
            displayNameVisibility = "registered",
            avatarVisibility = "registered",
            residenceCountryVisibility = "private",
            residenceCityVisibility = "private",
            dateOfBirthVisibility = "private"
        });
        Assert.Equal(HttpStatusCode.OK, privateAgain.StatusCode);

        using var hiddenFriendResponse = await friend.GetAsync($"/api/users/{ownerId}/profile");
        using (var hiddenFriend = JsonDocument.Parse(await hiddenFriendResponse.Content.ReadAsStringAsync()))
            Assert.Equal(JsonValueKind.Null, hiddenFriend.RootElement.GetProperty("birthdayMonth").ValueKind);

        using var ownerProfileResponse = await owner.GetAsync($"/api/users/{ownerId}/profile");
        using (var ownerProfile = JsonDocument.Parse(await ownerProfileResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(4, ownerProfile.RootElement.GetProperty("birthdayMonth").GetInt32());
            Assert.Equal(6, ownerProfile.RootElement.GetProperty("birthdayDay").GetInt32());
        }
    }

    [Fact]
    public async Task Age_Restrictions_Exclude_Unknown_Age_And_Respect_Both_Bounds()
    {
        using var factory = _database.CreateFactory(rateLimitClientIp: IPAddress.Parse("192.0.2.103"));
        using var client = factory.CreateClient();
        await RegisterAsync(client);

        using var residence = await client.PutAsJsonAsync("/api/me/profile/residence",
            new { residenceCountry = "API test country", residenceCity = "API test city" });
        Assert.Equal(HttpStatusCode.OK, residence.StatusCode);

        var minimumOnlyName = "Age minimum " + Guid.NewGuid().ToString("N")[..8];
        var maximumOnlyName = "Age maximum " + Guid.NewGuid().ToString("N")[..8];
        await InsertRestrictedAttractionAsync(minimumOnlyName, 18, null);
        await InsertRestrictedAttractionAsync(maximumOnlyName, null, 17);

        var withoutBirthDate = await ReadRecommendationNamesAsync(client);
        Assert.DoesNotContain(minimumOnlyName, withoutBirthDate);
        Assert.DoesNotContain(maximumOnlyName, withoutBirthDate);

        using var adultDate = await client.PutAsJsonAsync("/api/me/profile/birthday",
            new { dateOfBirth = "1990-01-01" });
        Assert.Equal(HttpStatusCode.OK, adultDate.StatusCode);
        var adultRecommendations = await ReadRecommendationNamesAsync(client);
        Assert.Contains(minimumOnlyName, adultRecommendations);
        Assert.DoesNotContain(maximumOnlyName, adultRecommendations);

        using var teenDate = await client.PutAsJsonAsync("/api/me/profile/birthday",
            new { dateOfBirth = "2012-01-01" });
        Assert.Equal(HttpStatusCode.OK, teenDate.StatusCode);
        var teenRecommendations = await ReadRecommendationNamesAsync(client);
        Assert.DoesNotContain(minimumOnlyName, teenRecommendations);
        Assert.Contains(maximumOnlyName, teenRecommendations);
    }

    private async Task InsertRestrictedAttractionAsync(string name, short? minimumAge, short? maximumAge)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"
            INSERT INTO public.attractions
                (name, description, history, latitude, longitude, category_id, city_id,
                 min_recommended_age, max_recommended_age)
            VALUES
                (@name, 'Age filter test', '', 11, 21,
                 (SELECT category_id FROM public.categories ORDER BY category_id LIMIT 1),
                 (SELECT ci.city_id FROM public.cities ci
                  JOIN public.countries co ON co.country_id = ci.country_id
                  WHERE ci.name = 'API test city' AND co.name = 'API test country'
                  ORDER BY ci.city_id LIMIT 1),
                 @minimum, @maximum)",
            connection);
        command.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, name);
        command.Parameters.Add(new NpgsqlParameter("minimum", NpgsqlDbType.Smallint)
        {
            Value = minimumAge is { } min ? min : DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("maximum", NpgsqlDbType.Smallint)
        {
            Value = maximumAge is { } max ? max : DBNull.Value
        });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ReadRecommendationNamesAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/me/recommendations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString() ?? string.Empty).ToList();
    }

    private static async Task<int> GetUserIdAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/me/profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetInt32();
    }

    private static async Task RegisterAsync(HttpClient client)
    {
        var login = "birthday_" + Guid.NewGuid().ToString("N")[..16];
        using var response = await client.PostAsJsonAsync("/api/auth/register",
            new { login, password = "BirthdayTest#2026", displayName = "Birthday Test" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
