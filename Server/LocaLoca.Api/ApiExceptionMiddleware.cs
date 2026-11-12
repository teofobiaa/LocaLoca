using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LocaLoca.Api;

public sealed record ApiErrorResponse(string Error)
{
    public static string MessageFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "Некорректный запрос. Проверьте переданные данные.",
        StatusCodes.Status401Unauthorized => "Требуется действующая авторизация.",
        StatusCodes.Status403Forbidden => "Недостаточно прав для выполнения операции.",
        StatusCodes.Status404NotFound => "Ресурс или маршрут не найден.",
        StatusCodes.Status409Conflict => "Операция конфликтует с текущим состоянием данных.",
        StatusCodes.Status413PayloadTooLarge => "Размер запроса превышает допустимый лимит.",
        StatusCodes.Status429TooManyRequests => "Слишком много запросов. Повторите попытку позже.",
        StatusCodes.Status503ServiceUnavailable => "Сервис временно недоступен. Повторите попытку позже.",
        _ => "Внутренняя ошибка API."
    };
}

public readonly record struct ApiErrorMapping(int StatusCode, string Message);

public sealed class DatabaseUnavailableException : Exception
{
    public DatabaseUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class ApiExceptionMiddleware
{
    private const long MaxMultipartBodyBytes = 22L * 1024 * 1024;

    private readonly RequestDelegate _next;
    private readonly ILogger<ApiExceptionMiddleware> _logger;

    public ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // A disconnected client is not an application or database failure.
            throw;
        }
        catch (PostgresException ex)
        {
            var mapping = MapPostgresError(ex.SqlState, ex.Message);
            _logger.LogWarning(ex, "PostgreSQL rejected an API operation with SQLSTATE {SqlState}.", ex.SqlState);
            await WriteErrorAsync(context, mapping.StatusCode, mapping.Message);
        }
        catch (DatabaseUnavailableException ex)
        {
            _logger.LogError(ex, "PostgreSQL is not configured or unavailable.");
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                ApiErrorResponse.MessageFor(StatusCodes.Status503ServiceUnavailable));
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(ex, "PostgreSQL connection or protocol failure.");
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                ApiErrorResponse.MessageFor(StatusCodes.Status503ServiceUnavailable));
        }
        catch (TimeoutException ex)
        {
            _logger.LogError(ex, "An API dependency operation timed out.");
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                ApiErrorResponse.MessageFor(StatusCodes.Status503ServiceUnavailable));
        }
        catch (BadHttpRequestException ex)
        {
            var statusCode = ex.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? StatusCodes.Status413PayloadTooLarge
                : StatusCodes.Status400BadRequest;
            await WriteErrorAsync(context, statusCode, ApiErrorResponse.MessageFor(statusCode));
        }
        catch (InvalidDataException)
        {
            var statusCode = (context.Request.ContentLength ?? 0) > MaxMultipartBodyBytes
                ? StatusCodes.Status413PayloadTooLarge
                : StatusCodes.Status400BadRequest;
            await WriteErrorAsync(context, statusCode, ApiErrorResponse.MessageFor(statusCode));
        }
        catch (JsonException ex)
        {
            _logger.LogInformation(ex, "An API request contained invalid JSON.");
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                ApiErrorResponse.MessageFor(StatusCodes.Status400BadRequest));
        }
        catch (Exception ex)
        {
            // InvalidOperationException and other unexpected failures are not evidence
            // of a database outage; preserve 500 instead of misreporting them as 503.
            _logger.LogError(ex, "Unhandled API exception.");
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
                ApiErrorResponse.MessageFor(StatusCodes.Status500InternalServerError));
        }
    }

    public static ApiErrorMapping MapPostgresError(string? sqlState, string? serverMessage = null)
    {
        // Serialization failures, deadlocks and unavailable row locks indicate
        // a concurrent transaction conflict; clients can retry after refreshing.
        if (sqlState is "40001" or "40P01" or "55P03")
            return new ApiErrorMapping(StatusCodes.Status409Conflict,
                ApiErrorResponse.MessageFor(StatusCodes.Status409Conflict));

        if (sqlState is "23505" or "23P01" or "23001")
            return new ApiErrorMapping(StatusCodes.Status409Conflict,
                ApiErrorResponse.MessageFor(StatusCodes.Status409Conflict));

        if (sqlState is "23503" or "23502" or "23514" ||
            sqlState?.StartsWith("22", StringComparison.Ordinal) == true ||
            (sqlState?.StartsWith("23", StringComparison.Ordinal) == true && sqlState != "23P01"))
            return new ApiErrorMapping(StatusCodes.Status400BadRequest,
                "Операция нарушает ограничения целостности данных или содержит недопустимое значение.");

        if (sqlState == "42P01")
        {
            if (serverMessage?.Contains("user_map_places", StringComparison.OrdinalIgnoreCase) == true ||
                serverMessage?.Contains("user_map_place_photos", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new ApiErrorMapping(StatusCodes.Status503ServiceUnavailable,
                    "Для личных точек карты не применена миграция базы данных. Требуется обновление схемы PostgreSQL.");
            }

            return new ApiErrorMapping(StatusCodes.Status503ServiceUnavailable,
                "Схема PostgreSQL не соответствует версии API. Требуется проверка миграций базы данных.");
        }

        if (sqlState is "42501" or "3D000" or "53300" or "57014" or "57P01" or "57P02" or "57P03" ||
            sqlState?.StartsWith("08", StringComparison.Ordinal) == true ||
            sqlState?.StartsWith("53", StringComparison.Ordinal) == true ||
            sqlState?.StartsWith("57", StringComparison.Ordinal) == true)
            return new ApiErrorMapping(StatusCodes.Status503ServiceUnavailable,
                ApiErrorResponse.MessageFor(StatusCodes.Status503ServiceUnavailable));

        return new ApiErrorMapping(StatusCodes.Status500InternalServerError,
            ApiErrorResponse.MessageFor(StatusCodes.Status500InternalServerError));
    }

    private async Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        if (context.Response.HasStarted)
        {
            _logger.LogError("Cannot write an API error response because the response has already started.");
            context.Abort();
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(
            new ApiErrorResponse(message),
            cancellationToken: context.RequestAborted);
    }
}
