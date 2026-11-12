using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Routing;

namespace LocaLoca.Api;

/// <summary>
/// Records request and PostgreSQL command timings without logging SQL text or
/// parameter values. Histograms are available to System.Diagnostics.Metrics
/// listeners; slow operations are also written to the application log.
/// </summary>
public static class PerformanceDiagnosticsExtensions
{
    public const string MeterName = "LocaLoca.Api.Performance";
    public const string HttpRequestDurationName = "loca.http.request.duration";
    public const string DbCommandDurationName = "loca.db.command.duration";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> HttpRequestDuration =
        Meter.CreateHistogram<double>(HttpRequestDurationName, "ms", "HTTP request duration.");
    private static readonly Histogram<double> DbCommandDuration =
        Meter.CreateHistogram<double>(DbCommandDurationName, "ms", "PostgreSQL command duration.");

    public static void UsePerformanceDiagnostics(
        this WebApplication app,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configuration);

        var logger = app.Logger;
        var slowHttpThresholdMs = GetThreshold(
            configuration, "Performance:SlowHttpRequestThresholdMs", 500);
        var slowSqlThresholdMs = GetThreshold(
            configuration, "Performance:SlowSqlCommandThresholdMs", 100);

        // Npgsql 10 emits command activities through the built-in "Npgsql"
        // ActivitySource. Listening here avoids wrapping every NpgsqlCommand
        // call site and includes reader execution/disposal time.
        var databaseListener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(
                source.Name, "Npgsql", StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
                RecordDatabaseActivity(activity, logger, slowSqlThresholdMs)
        };

        ActivitySource.AddActivityListener(databaseListener);
        app.Lifetime.ApplicationStopped.Register(databaseListener.Dispose);

        app.Use(async (context, next) =>
        {
            var startedAt = Stopwatch.GetTimestamp();

            try
            {
                await next();
            }
            finally
            {
                var durationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                    ?? "unmatched";
                var method = context.Request.Method;
                var statusCode = context.Response.StatusCode;

                HttpRequestDuration.Record(
                    durationMs,
                    new KeyValuePair<string, object?>("http.request.method", method),
                    new KeyValuePair<string, object?>("http.route", route),
                    new KeyValuePair<string, object?>("http.response.status_code", statusCode));

                if (durationMs >= slowHttpThresholdMs)
                {
                    logger.LogWarning(
                        "Slow HTTP request {HttpMethod} {HttpRoute} returned {StatusCode} in {DurationMs:F1} ms.",
                        method, route, statusCode, durationMs);
                }
                else
                {
                    logger.LogDebug(
                        "HTTP request {HttpMethod} {HttpRoute} returned {StatusCode} in {DurationMs:F1} ms.",
                        method, route, statusCode, durationMs);
                }
            }
        });
    }

    private static void RecordDatabaseActivity(
        Activity activity,
        ILogger logger,
        double slowSqlThresholdMs)
    {
        if (activity.GetTagItem("db.query.text") is not string sql ||
            string.IsNullOrWhiteSpace(sql))
        {
            return;
        }

        var durationMs = activity.Duration.TotalMilliseconds;
        var operation = activity.GetTagItem("db.operation.name")?.ToString()
            ?? GetSqlOperation(sql);
        var failed = activity.Status == ActivityStatusCode.Error;

        // Keep metric dimensions low-cardinality. A per-statement fingerprint
        // is included only in logs, never as a metric tag.
        DbCommandDuration.Record(
            durationMs,
            new KeyValuePair<string, object?>("db.operation", operation),
            new KeyValuePair<string, object?>("error", failed));

        if (durationMs >= slowSqlThresholdMs)
        {
            logger.LogWarning(
                "Slow PostgreSQL command {StatementId} ({Operation}) completed in {DurationMs:F1} ms. Error: {Error}.",
                GetStatementFingerprint(sql), operation, durationMs, failed);
        }
        else if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "PostgreSQL command {StatementId} ({Operation}) completed in {DurationMs:F1} ms. Error: {Error}.",
                GetStatementFingerprint(sql), operation, durationMs, failed);
        }
    }

    private static double GetThreshold(
        IConfiguration configuration,
        string key,
        double defaultValue)
    {
        var configuredValue = configuration.GetValue<double?>(key);
        return configuredValue is >= 0 ? configuredValue.Value : defaultValue;
    }

    private static string GetSqlOperation(string sql)
    {
        var statement = sql.AsSpan().TrimStart();
        var length = 0;

        while (length < statement.Length &&
               !char.IsWhiteSpace(statement[length]) &&
               statement[length] != '(')
        {
            length++;
        }

        return length == 0 ? "UNKNOWN" : statement[..length].ToString().ToUpperInvariant();
    }

    private static string GetStatementFingerprint(string sql)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sql));
        return Convert.ToHexString(hash.AsSpan(0, 6));
    }
}
