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
        using var factory = _database.CreateFactory();
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
    public async Task Birthday_Contacts_Are_Owner_Scoped_And_Calendar_Uid_Is_Stable()
    {
        using var factory = _database.CreateFactory();
        using var owner = factory.CreateClient();
        using var other = factory.CreateClient();
        await RegisterAsync(owner);
        await RegisterAsync(other);

        using var create = await owner.PostAsJsonAsync("/api/me/birthday-contacts",
            new { displayName = "Friend One", birthDate = "1999-04-06" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var createdJson = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var contactId = createdJson.RootElement.GetProperty("id").GetInt32();

        using var ownerListResponse = await owner.GetAsync("/api/me/birthday-contacts");
        Assert.Equal(HttpStatusCode.OK, ownerListResponse.StatusCode);
        Assert.True(ownerListResponse.Headers.CacheControl?.NoStore == true);
        using var ownerList = JsonDocument.Parse(await ownerListResponse.Content.ReadAsStringAsync());
        Assert.Single(ownerList.RootElement.GetProperty("items").EnumerateArray());

        using var otherListResponse = await other.GetAsync("/api/me/birthday-contacts");
        Assert.Equal(HttpStatusCode.OK, otherListResponse.StatusCode);
        using var otherList = JsonDocument.Parse(await otherListResponse.Content.ReadAsStringAsync());
        Assert.Empty(otherList.RootElement.GetProperty("items").EnumerateArray());

        using var deniedCalendar = await other.GetAsync($"/api/me/birthday-contacts/{contactId}/calendar.ics");
        Assert.Equal(HttpStatusCode.NotFound, deniedCalendar.StatusCode);

        using var firstCalendar = await owner.GetAsync($"/api/me/birthday-contacts/{contactId}/calendar.ics");
        using var secondCalendar = await owner.GetAsync($"/api/me/birthday-contacts/{contactId}/calendar.ics");
        Assert.Equal(HttpStatusCode.OK, firstCalendar.StatusCode);
        Assert.True(firstCalendar.Headers.CacheControl?.NoStore == true);
        var firstText = await firstCalendar.Content.ReadAsStringAsync();
        var secondText = await secondCalendar.Content.ReadAsStringAsync();
        var expectedUid = $"UID:localoca-birthday-contact-{contactId}@localoca";
        Assert.Contains(expectedUid, firstText, StringComparison.Ordinal);
        Assert.Contains(expectedUid, secondText, StringComparison.Ordinal);
        Assert.Contains("RRULE:FREQ=YEARLY", firstText, StringComparison.Ordinal);
        Assert.Contains("BEGIN:VALARM", firstText, StringComparison.Ordinal);
        Assert.Equal(1, firstText.Split(expectedUid, StringSplitOptions.None).Length - 1);

        using var deniedDelete = await other.DeleteAsync($"/api/me/birthday-contacts/{contactId}");
        Assert.Equal(HttpStatusCode.NotFound, deniedDelete.StatusCode);
        using var ownerListAfterDeniedDelete = await owner.GetAsync("/api/me/birthday-contacts");
        using var ownerListAfterDeniedDeleteJson = JsonDocument.Parse(await ownerListAfterDeniedDelete.Content.ReadAsStringAsync());
        Assert.Single(ownerListAfterDeniedDeleteJson.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Age_Restrictions_Exclude_Unknown_Age_And_Respect_Both_Bounds()
    {
        using var factory = _database.CreateFactory();
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
