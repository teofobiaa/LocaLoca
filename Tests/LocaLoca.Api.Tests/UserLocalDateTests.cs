using System.Globalization;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LocaLoca.Api.Tests;

public sealed class UserLocalDateTests
{
    [Theory]
    [InlineData("Europe/Moscow", "2026-01-02")]
    [InlineData("Pacific/Kiritimati", "2026-01-02")]
    public void GetUserToday_Uses_The_Callers_Local_Calendar_Date(string timeZone, string expectedDate)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Time-Zone"] = timeZone;
        var utcNow = DateTimeOffset.Parse(
            "2026-01-01T23:30:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);

        var today = Api.ApiEndpointHelpers.GetUserToday(context, utcNow);

        Assert.Equal(DateOnly.Parse(expectedDate, CultureInfo.InvariantCulture), today);
        Assert.True(DateOnly.Parse(expectedDate, CultureInfo.InvariantCulture) <= today);
        Assert.False(DateOnly.Parse("2026-01-03", CultureInfo.InvariantCulture) <= today);
    }

    [Fact]
    public void GetUserToday_Falls_Back_To_Utc_For_Missing_Or_Invalid_Timezone()
    {
        var context = new DefaultHttpContext();
        var utcNow = DateTimeOffset.Parse(
            "2026-01-01T23:30:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);

        Assert.Equal(new DateOnly(2026, 1, 1), Api.ApiEndpointHelpers.GetUserToday(context, utcNow));

        context.Request.Headers["X-Time-Zone"] = "Not/A_Real_Time_Zone";
        Assert.Equal(new DateOnly(2026, 1, 1), Api.ApiEndpointHelpers.GetUserToday(context, utcNow));
    }
}
