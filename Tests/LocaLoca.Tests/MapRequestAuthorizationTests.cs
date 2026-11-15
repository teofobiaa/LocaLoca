using LocaLoca.Services;
using Xunit;

namespace LocaLoca.Tests;

public sealed class MapRequestAuthorizationTests
{
    private static readonly Uri ApiBaseAddress = new("https://localoca-api.example/");
    private const string Token = "opaque-unit-test-token";

    [Theory]
    [InlineData("https://localoca-api.example/api/me/map-markers?page=1", true)]
    [InlineData("https://localoca-api.example/api/me/map-places", true)]
    [InlineData("https://localoca-api.example/api/me/map-places/42", true)]
    [InlineData("https://localoca-api.example/api/me/map-places/42/photos", true)]
    [InlineData("https://localoca-api.example/api/me/map-place-photos/9", true)]
    [InlineData("https://localoca-api.example/api/me/profile", false)]
    [InlineData("https://localoca-api.example/api/auth/logout", false)]
    [InlineData("https://attacker.example/api/me/map-markers", false)]
    [InlineData("http://localoca-api.example/api/me/map-markers", false)]
    [InlineData("https://localoca-api.example/api/me/map-places/42/../../auth/logout", false)]
    [InlineData("https://localoca-api.example/api/me/map-markers/extra", false)]
    [InlineData("https://localoca-api.example/api/me/map-places/not-an-id", false)]
    public void Adds_Bearer_Header_Only_To_Allowed_Map_Requests(string requestUrl, bool expected)
    {
        var header = MapRequestAuthorization.CreateHeader(ApiBaseAddress, Token, new Uri(requestUrl));

        if (expected)
            Assert.Equal($"Bearer {Token}", header);
        else
            Assert.Null(header);
    }

    [Fact]
    public void Handles_Base_Address_With_A_Path_And_A_Query_String()
    {
        var apiBaseAddress = new Uri("https://localoca-api.example/desktop/");
        var requestUri = new Uri("https://localoca-api.example/desktop/api/me/map-markers?west=10&east=20");

        var header = MapRequestAuthorization.CreateHeader(apiBaseAddress, Token, requestUri);

        Assert.Equal($"Bearer {Token}", header);
    }

    [Fact]
    public void Rejects_Encoded_Path_Separators()
    {
        var requestUri = new Uri("https://localoca-api.example/api/me/map-markers%2F..%2Fauth%2Flogout");

        var header = MapRequestAuthorization.CreateHeader(ApiBaseAddress, Token, requestUri);

        Assert.Null(header);
    }

    [Fact]
    public void Does_Not_Create_A_Header_When_The_Session_Is_Missing()
    {
        var header = MapRequestAuthorization.CreateHeader(
            ApiBaseAddress, null, new Uri("https://localoca-api.example/api/me/map-markers"));

        Assert.Null(header);
    }
}
