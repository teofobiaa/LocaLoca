using Npgsql;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using LocaLoca.Api;
using LocaLoca.Api.Security;
using LocaLoca.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);
var passwordHashIterations = PasswordHasher.GetConfiguredIterations(
    builder.Configuration["LOCA_PASSWORD_PBKDF2_ITERATIONS"]);
// Local development keeps the loopback endpoint. Render supplies PORT; in the
// container the API must listen on all interfaces so the platform can reach it.
var renderPort = builder.Configuration["PORT"];
if (!string.IsNullOrWhiteSpace(renderPort))
{
    var sessionSecret = builder.Configuration["LOCA_SESSION_SECRET"];
    if (string.IsNullOrWhiteSpace(sessionSecret) || sessionSecret.Length < 32)
        throw new InvalidOperationException("LOCA_SESSION_SECRET must contain at least 32 characters for cloud hosting.");
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
}
else
{
    builder.WebHost.UseUrls("http://127.0.0.1:5181");
}
builder.Services.Configure<FormOptions>(options =>
{
    // 20 MiB is the application photo limit; leave room for multipart headers and fields.
    options.MultipartBodyLengthLimit = 22 * 1024 * 1024;
});
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("auth-register", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("auth-logout", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
var app = builder.Build();
app.UsePerformanceDiagnostics(builder.Configuration);

// Empty framework responses (401/403/404/429, etc.) use the same JSON
// error contract as explicit validation failures and exception responses.
app.UseStatusCodePages(async statusCodeContext =>
{
    var response = statusCodeContext.HttpContext.Response;
    await response.WriteAsJsonAsync(
        new ApiErrorResponse(ApiErrorResponse.MessageFor(response.StatusCode)));
});
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseRateLimiter();

var dataRoot = builder.Configuration["LOCA_DATA_ROOT"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocaLoca");
var userConfigPath = Path.Combine(dataRoot, "server-config.json");
// Prefer server environment variables. Do not attempt to decrypt a Windows
// DPAPI config in Linux containers when the cloud connection string is set.
var connectionString = builder.Configuration["LOCA_DB_CONNECTION"]
    ?? builder.Configuration["ConnectionStrings:LocaLoca"]
    ?? builder.Configuration.GetConnectionString("LocaLoca")
    ?? ConnectionStringConfiguration.ReadUserConnectionString(userConfigPath);
string? connectionConfigurationError = null;

// PostgreSQL is shared in Supabase. Keep 127.0.0.1 only for the local
// WinUI -> ASP.NET Core API connection; reject a local PostgreSQL endpoint.
if (!string.IsNullOrWhiteSpace(connectionString) &&
    !connectionString.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        var connectionBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        var configuredHost = (connectionBuilder.Host ?? string.Empty).Trim().Trim('[', ']');
        var isLoopbackHost =
            configuredHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            configuredHost.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
            configuredHost.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);

        if (isLoopbackHost && !builder.Environment.IsEnvironment("Testing"))
        {
            connectionString = null;
            connectionConfigurationError =
                "Локальная PostgreSQL больше не используется. Настрой подключение к Supabase через tools\\Install-CloudDependencies.ps1.";
        }
        else
        {
            // Testcontainers uses an ephemeral loopback-mapped PostgreSQL port.
            // This exception is available only inside the isolated Testing host.
            // Preserve the caller's pooling setting. Npgsql enables pooling by
            // default, while tests and diagnostics may explicitly disable it.
            // Keep each desktop API process' pool small for Supabase Pooler.
            connectionBuilder.MaxPoolSize = Math.Min(connectionBuilder.MaxPoolSize, 5);
            connectionString = connectionBuilder.ConnectionString;
        }
    }
    catch (ArgumentException)
    {
        connectionString = null;
        connectionConfigurationError = "Строка подключения Supabase PostgreSQL имеет неверный формат.";
    }
}

app.Use(async (context, next) =>
{
    // Liveness/readiness endpoints must remain reachable when PostgreSQL is down.
    // /ready performs its own database check; neither endpoint requires a user session.
    var requestPath = context.Request.Path.Value;
    if (string.Equals(requestPath, "/health", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(requestPath, "/ready", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var header=context.Request.Headers["Authorization"].ToString();
    if(header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(connectionString)){
        var token=header[7..].Trim();
        var sessions=context.RequestServices.GetRequiredService<SessionStore>();
        var userId=await sessions.ValidateAsync(token,connectionString,context.RequestAborted);
        if(userId.HasValue)context.Items["LocaLoca.UserId"]=userId.Value;
    }
    await next();
});
var photosRoot = Path.Combine(dataRoot, "Photos");
Directory.CreateDirectory(photosRoot);

app.MapHealthEndpoints(connectionString, connectionConfigurationError);
app.MapAuthenticationEndpoints(connectionString, passwordHashIterations);
app.MapCatalogEndpoints(connectionString);
app.MapPersonalEndpoints(connectionString, dataRoot);
app.MapUserMapEndpoints(connectionString, dataRoot);

app.Run();

public partial class Program { }
