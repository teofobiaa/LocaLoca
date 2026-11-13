using System.Diagnostics;
using System.Net;
using Npgsql;
using NpgsqlTypes;

namespace LocaLoca.Api.Security;

/// <summary>
/// Stores authentication rate-limit counters in PostgreSQL so every API instance
/// enforces the same per-IP windows.
/// </summary>
public sealed class DistributedAuthRateLimitStore(ILogger<DistributedAuthRateLimitStore> logger)
{
    private const int CleanupIntervalSeconds = 300;
    private long _nextCleanupTimestamp;

    private const string IncrementCounterSql = """
        WITH current_window AS (
            SELECT to_timestamp(
                (floor(extract(epoch FROM statement_timestamp()) / @window_seconds) * @window_seconds)::double precision
            ) AS window_started_at
        )
        INSERT INTO public.auth_rate_limit_counters
            (policy_key, client_ip, window_started_at, request_count, expires_at)
        SELECT
            @policy_key,
            @client_ip,
            current_window.window_started_at,
            1,
            current_window.window_started_at + (@window_seconds * INTERVAL '1 second')
        FROM current_window
        ON CONFLICT (policy_key, client_ip) DO UPDATE
        SET
            window_started_at = GREATEST(auth_rate_limit_counters.window_started_at, EXCLUDED.window_started_at),
            request_count = CASE
                WHEN auth_rate_limit_counters.window_started_at < EXCLUDED.window_started_at THEN 1
                ELSE auth_rate_limit_counters.request_count + 1
            END,
            expires_at = GREATEST(auth_rate_limit_counters.expires_at, EXCLUDED.expires_at)
        RETURNING request_count;
        """;

    public async Task<bool> TryAcquireAsync(
        string connectionString,
        string policyKey,
        IPAddress clientIp,
        int permitLimit,
        int windowSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyKey);
        ArgumentNullException.ThrowIfNull(clientIp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSeconds);

        if (policyKey.Length > 32)
            throw new ArgumentOutOfRangeException(nameof(policyKey), "Policy keys must not exceed 32 characters.");

        if (clientIp.IsIPv4MappedToIPv6)
            clientIp = clientIp.MapToIPv4();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(IncrementCounterSql, connection);
        command.Parameters.AddWithValue("policy_key", NpgsqlDbType.Varchar, policyKey);
        command.Parameters.AddWithValue("client_ip", NpgsqlDbType.Inet, clientIp);
        command.Parameters.AddWithValue("window_seconds", NpgsqlDbType.Integer, windowSeconds);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
            throw new InvalidOperationException("The PostgreSQL rate-limit counter did not return a count.");

        var requestCount = Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        await TryDeleteExpiredCountersAsync(connection, cancellationToken);

        return requestCount <= permitLimit;
    }

    private async Task TryDeleteExpiredCountersAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var now = Stopwatch.GetTimestamp();
        var nextCleanup = Volatile.Read(ref _nextCleanupTimestamp);
        var intervalTicks = (long)(Stopwatch.Frequency * (double)CleanupIntervalSeconds);

        if (now < nextCleanup ||
            Interlocked.CompareExchange(ref _nextCleanupTimestamp, now + intervalTicks, nextCleanup) != nextCleanup)
        {
            return;
        }

        try
        {
            await using var cleanup = new NpgsqlCommand(
                "DELETE FROM public.auth_rate_limit_counters WHERE expires_at <= statement_timestamp()",
                connection)
            {
                CommandTimeout = 3
            };
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (NpgsqlException exception)
        {
            logger.LogWarning(exception, "Could not remove expired authentication rate-limit counters.");
        }
    }
}
