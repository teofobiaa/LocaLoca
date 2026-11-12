using System.Text.Json;
using LocaLoca.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocaLoca.Api.Tests;

public sealed class ApiErrorMappingTests
{
    [Theory]
    [InlineData("23505", 409)]
    [InlineData("23P01", 409)]
    [InlineData("40001", 409)]
    [InlineData("40P01", 409)]
    [InlineData("55P03", 409)]
    [InlineData("23503", 400)]
    [InlineData("23502", 400)]
    [InlineData("23514", 400)]
    [InlineData("22P02", 400)]
    [InlineData("42P01", 503)]
    [InlineData("42501", 503)]
    [InlineData("08006", 503)]
    [InlineData("53300", 503)]
    [InlineData("57P01", 503)]
    [InlineData("42601", 500)]
    [InlineData("XX000", 500)]
    public void PostgreSql_SQLSTATE_Is_Mapped_To_The_Correct_Http_Status(string sqlState, int expectedStatus)
    {
        var mapping = ApiExceptionMiddleware.MapPostgresError(sqlState);

        Assert.Equal(expectedStatus, mapping.StatusCode);
    }

    [Fact]
    public void Missing_Map_Place_Migration_Is_A_Service_Configuration_Error()
    {
        var mapping = ApiExceptionMiddleware.MapPostgresError(
            "42P01",
            "relation user_map_places does not exist");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, mapping.StatusCode);
        Assert.Contains("миграция", mapping.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(413)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void Standard_Status_Codes_Have_A_Non_Empty_Error_Message(int statusCode)
    {
        Assert.False(string.IsNullOrWhiteSpace(ApiErrorResponse.MessageFor(statusCode)));
    }

    [Fact]
    public async Task Unexpected_InvalidOperation_Is_500_And_Does_Not_Expose_Exception_Details()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new ApiExceptionMiddleware(
            _ => throw new InvalidOperationException("Password=must-not-leak"),
            NullLogger<ApiExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body);
        var error = document.RootElement.GetProperty("error").GetString();
        Assert.Contains("Внутренняя ошибка", error);
        Assert.DoesNotContain("must-not-leak", error);
    }

    [Fact]
    public async Task Oversized_Bad_Http_Request_Is_413()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new ApiExceptionMiddleware(
            _ => throw new BadHttpRequestException("Request body too large", StatusCodes.Status413PayloadTooLarge),
            NullLogger<ApiExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
    }

    [Fact]
    public async Task Database_Unavailable_Exception_Is_503()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new ApiExceptionMiddleware(
            _ => throw new DatabaseUnavailableException("connection string is not configured"),
            NullLogger<ApiExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }
}
