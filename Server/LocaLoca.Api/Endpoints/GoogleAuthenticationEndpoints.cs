using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using LocaLoca.Api.Security;
using static LocaLoca.Api.ApiEndpointHelpers;

namespace LocaLoca.Api.Endpoints;

public sealed record GoogleChallengeResponse(string Nonce);
public sealed record GoogleSignInRequest(string IdToken);

public static class GoogleAuthenticationEndpoints
{
    public static void MapGoogleAuthenticationEndpoints(
        this WebApplication app,
        string? connectionString)
    {
        app.MapPost("/api/auth/google/challenge", async (
            GoogleIdTokenValidator validator,
            CancellationToken cancellationToken) =>
        {
            var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var cleanup = new NpgsqlCommand(
                "DELETE FROM public.google_oauth_challenges WHERE expires_at <= now()", connection);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);

            await using var insert = new NpgsqlCommand(@"
                INSERT INTO public.google_oauth_challenges(challenge_hash, expires_at)
                VALUES (@hash, now() + interval '5 minutes')", connection);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, hash);
            await insert.ExecuteNonQueryAsync(cancellationToken);

            return Results.Ok(new GoogleChallengeResponse(nonce));
        });

        app.MapPost("/api/auth/google", async Task<IResult> (
            GoogleSignInRequest request,
            GoogleIdTokenValidator validator,
            SessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.IdToken) || request.IdToken.Length > 16_384)
                return Results.Unauthorized();

            // The nonce is read only to validate the signed Google token. The
            // corresponding challenge is consumed atomically below, exactly once.
            var nonce = ReadNonceWithoutTrustingClaims(request.IdToken);
            if (nonce is null) return Results.Unauthorized();

            var identity = await validator.ValidateAsync(request.IdToken, nonce, cancellationToken);
            if (identity is null) return Results.Unauthorized();

            var challengeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            // Serialize first-time provisioning for the same Google subject. This
            // prevents two concurrent sign-ins from racing on the unique login.
            await using (var identityLock = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(@subject, 0))", connection, transaction))
            {
                identityLock.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, identity.Subject);
                await identityLock.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var consume = new NpgsqlCommand(@"
                DELETE FROM public.google_oauth_challenges
                WHERE challenge_hash = @hash AND expires_at > now()
                RETURNING challenge_hash", connection, transaction))
            {
                consume.Parameters.AddWithValue("hash", NpgsqlDbType.Char, challengeHash);
                if (await consume.ExecuteScalarAsync(cancellationToken) is null)
                    return Results.Unauthorized();
            }

            int userId;
            string login;
            string displayName;
            string? residenceCountry;
            string? residenceCity;
            bool hasAvatar;

            await using (var existing = new NpgsqlCommand(@"
                SELECT u.user_id, u.login, u.display_name, u.residence_country, u.residence_city,
                       EXISTS (SELECT 1 FROM public.user_avatars a WHERE a.user_id = u.user_id)
                FROM public.user_external_identities e
                JOIN public.users u ON u.user_id = e.user_id
                WHERE e.provider = 'google' AND e.provider_subject = @subject
                FOR UPDATE OF u", connection, transaction))
            {
                existing.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, identity.Subject);
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    userId = reader.GetInt32(0);
                    login = reader.GetString(1);
                    displayName = reader.GetString(2);
                    residenceCountry = reader.IsDBNull(3) ? null : reader.GetString(3);
                    residenceCity = reader.IsDBNull(4) ? null : reader.GetString(4);
                    hasAvatar = reader.GetBoolean(5);
                    await reader.CloseAsync();

                    await using var updateIdentity = new NpgsqlCommand(@"
                        UPDATE public.user_external_identities
                        SET email = @email, email_verified = true, last_used_at = now()
                        WHERE provider = 'google' AND provider_subject = @subject", connection, transaction);
                    updateIdentity.Parameters.AddWithValue("email", NpgsqlDbType.Varchar, identity.Email);
                    updateIdentity.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, identity.Subject);
                    await updateIdentity.ExecuteNonQueryAsync(cancellationToken);
                }
                else
                {
                    await reader.CloseAsync();
                    var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.Subject)))
                        .ToLowerInvariant()[..40];
                    login = "google_" + suffix;
                    displayName = identity.DisplayName;
                    residenceCountry = null;
                    residenceCity = null;
                    hasAvatar = false;

                    var randomPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
                    var passwordHash = PasswordHasher.Hash(randomPassword);
                    await using var createUser = new NpgsqlCommand(@"
                        INSERT INTO public.users(login, password_hash, display_name)
                        VALUES (@login, @password_hash, @display_name)
                        RETURNING user_id", connection, transaction);
                    createUser.Parameters.AddWithValue("login", NpgsqlDbType.Varchar, login);
                    createUser.Parameters.AddWithValue("password_hash", NpgsqlDbType.Varchar, passwordHash);
                    createUser.Parameters.AddWithValue("display_name", NpgsqlDbType.Varchar, displayName);
                    userId = Convert.ToInt32(await createUser.ExecuteScalarAsync(cancellationToken));

                    await using var createIdentity = new NpgsqlCommand(@"
                        INSERT INTO public.user_external_identities
                            (user_id, provider, provider_subject, email, email_verified)
                        VALUES (@user_id, 'google', @subject, @email, true)", connection, transaction);
                    createIdentity.Parameters.AddWithValue("user_id", NpgsqlDbType.Integer, userId);
                    createIdentity.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, identity.Subject);
                    createIdentity.Parameters.AddWithValue("email", NpgsqlDbType.Varchar, identity.Email);
                    await createIdentity.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            var user = new UserDto(userId, login, displayName, residenceCountry, residenceCity, hasAvatar);
            var token = await sessions.CreateAsync(userId, connectionString!, cancellationToken);
            return Results.Ok(new AuthResponse(user, token));
        });
    }

    private static string? ReadNonceWithoutTrustingClaims(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("nonce", out var nonce) &&
                   nonce.ValueKind == System.Text.Json.JsonValueKind.String
                ? nonce.GetString()
                : null;
        }
        catch (Exception exception) when (
            exception is FormatException or System.Text.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
