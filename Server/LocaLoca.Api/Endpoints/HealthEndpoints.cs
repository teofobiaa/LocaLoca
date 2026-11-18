using Npgsql;
using LocaLoca.Api;
namespace LocaLoca.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app, string? connectionString, string? connectionConfigurationError)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        
        // Desktop clients use /ready to verify the database connection and application-role permissions.
        app.MapGet("/ready", async Task<IResult> (CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new HealthDto(false, false,
                    connectionConfigurationError ?? "Не настроена строка подключения Supabase PostgreSQL. Запусти tools\\Install-CloudDependencies.ps1.", false, false),
                    statusCode: 503);
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                const string photoSchemaCheck = @"
        SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='user_sessions')
            AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='photos' AND column_name='image_data')
            AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='photos' AND column_name='content_type')
            AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='user_map_place_photos' AND column_name='image_data')
            AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='user_map_place_photos' AND column_name='content_type')";
                await using (var schemaCommand = new NpgsqlCommand(photoSchemaCheck, connection))
                {
                    var photoSchemaReady = Equals(await schemaCommand.ExecuteScalarAsync(cancellationToken), true);
                    if (!photoSchemaReady)
                        return Results.Json(new HealthDto(true, true,
                            "Не настроено хранение фотографий в PostgreSQL. Не применена миграция 007: database/migrations/07_migrate_photo_storage.sql.", false, true),
                            statusCode: 503);
                }
        
                const string residenceSchemaCheck = @"
        SELECT
            EXISTS (SELECT 1 FROM information_schema.columns
                    WHERE table_schema='public' AND table_name='users' AND column_name='residence_country'
                      AND data_type='character varying' AND character_maximum_length=100 AND is_nullable='YES')
            AND EXISTS (SELECT 1 FROM information_schema.columns
                        WHERE table_schema='public' AND table_name='users' AND column_name='residence_city'
                          AND data_type='character varying' AND character_maximum_length=100 AND is_nullable='YES')
            AND EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conrelid='public.users'::regclass AND conname='ck_users_residence_city_requires_country')";
                await using (var residenceCommand = new NpgsqlCommand(residenceSchemaCheck, connection))
                {
                    var residenceSchemaReady = Equals(await residenceCommand.ExecuteScalarAsync(cancellationToken), true);
                    if (!residenceSchemaReady)
                        return Results.Json(new HealthDto(true, true,
                            "Не применена миграция 011: database/migrations/11_user_residence_location.sql.", false, true),
                            statusCode: 503);
                }


                const string rateLimitSchemaCheck = @"
        SELECT EXISTS (
            SELECT 1 FROM information_schema.tables
            WHERE table_schema='public' AND table_name='auth_rate_limit_counters'
        )";
                await using (var rateLimitSchemaCommand = new NpgsqlCommand(rateLimitSchemaCheck, connection))
                {
                    var rateLimitSchemaReady = Equals(await rateLimitSchemaCommand.ExecuteScalarAsync(cancellationToken), true);
                    if (!rateLimitSchemaReady)
                        return Results.Json(new HealthDto(true, true,
                            "Не применена миграция 012: database/migrations/12_distributed_auth_rate_limits.sql.", false, true),
                            statusCode: 503);
                }

                const string avatarSchemaCheck = @"
        SELECT
            to_regclass('public.user_avatars') IS NOT NULL
            AND (
                SELECT count(*) = 5
                FROM pg_attribute
                WHERE attrelid = to_regclass('public.user_avatars')
                  AND attnum > 0
                  AND NOT attisdropped
                  AND attname IN ('user_id', 'image_data', 'thumbnail_data', 'content_type', 'updated_at')
            )";
                await using (var avatarSchemaCommand = new NpgsqlCommand(avatarSchemaCheck, connection))
                {
                    var avatarSchemaReady = Equals(await avatarSchemaCommand.ExecuteScalarAsync(cancellationToken), true);
                    if (!avatarSchemaReady)
                        return Results.Json(new HealthDto(true, true,
                            "Не применена миграция 013 или таблица public.user_avatars имеет неполную структуру: database/migrations/13_user_avatars.sql.", false, true),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                const string privacySchemaCheck = @"
        SELECT
            to_regclass('public.user_profile_visibility') IS NOT NULL
            AND to_regclass('public.user_friendships') IS NOT NULL
            AND to_regclass('public.user_blocks') IS NOT NULL
            AND EXISTS (SELECT 1 FROM pg_trigger
                        WHERE tgrelid = to_regclass('public.users')
                          AND tgname = 'trg_users_default_profile_visibility' AND NOT tgisinternal)
            AND (SELECT count(*) = 5
                 FROM pg_attribute
                 WHERE attrelid = to_regclass('public.user_profile_visibility')
                   AND attnum > 0 AND NOT attisdropped
                   AND attname IN ('user_id', 'display_name_visibility', 'avatar_visibility',
                                   'residence_country_visibility', 'residence_city_visibility'))";
                await using (var privacySchemaCommand = new NpgsqlCommand(privacySchemaCheck, connection))
                {
                    var privacySchemaReady = Equals(await privacySchemaCommand.ExecuteScalarAsync(cancellationToken), true);
                    if (!privacySchemaReady)
                        return Results.Json(new HealthDto(true, true,
                            "Не применена миграция 015: database/migrations/15_profile_privacy_and_relationships.sql.", false, true),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                const string birthdaySchemaCheck = @"
        SELECT
            EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='users'
                    AND column_name='date_of_birth' AND data_type='date' AND is_nullable='YES')
            AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='attractions'
                        AND column_name='min_recommended_age' AND data_type='smallint')
            AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='attractions'
                        AND column_name='max_recommended_age' AND data_type='smallint')
            AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.users'::regclass
                        AND conname='ck_users_date_of_birth_range')
            AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.attractions'::regclass
                        AND conname='ck_attractions_recommended_age_range')";
                await using (var birthdaySchemaCommand = new NpgsqlCommand(birthdaySchemaCheck, connection))
                {
                    var birthdaySchemaReady = Equals(await birthdaySchemaCommand.ExecuteScalarAsync(cancellationToken), true);
                    if (!birthdaySchemaReady)
                        return Results.Json(new HealthDto(true, true,
                            "Не применена миграция 014: database/migrations/14_user_birthdays_and_age_filtering.sql.", false, true),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                }


                const string birthdayVisibilitySchemaCheck = @"
        SELECT
            EXISTS (SELECT 1 FROM information_schema.columns
                    WHERE table_schema='public' AND table_name='user_profile_visibility'
                      AND column_name='date_of_birth_visibility'
                      AND data_type='character varying' AND character_maximum_length=20
                      AND is_nullable='NO' AND column_default LIKE '%private%')
            AND EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conrelid='public.user_profile_visibility'::regclass
                          AND conname='ck_user_profile_visibility_date_of_birth_visibility'
                          AND convalidated)
            AND to_regclass('public.birthday_contacts') IS NULL";
                await using (var command = new NpgsqlCommand(birthdayVisibilitySchemaCheck, connection))
                {
                    var ready = Equals(await command.ExecuteScalarAsync(cancellationToken), true);
                    if (!ready)
                        return Results.Json(new HealthDto(true, true,
                            "Не применена миграция 022: database/migrations/22_profile_birthday_visibility_and_remove_contacts.sql.", false, true),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                const string loginPrefixIndexCheck = @"
        SELECT EXISTS (SELECT 1 FROM pg_index i
                       JOIN pg_class idx ON idx.oid=i.indexrelid
                       JOIN pg_namespace ns ON ns.oid=idx.relnamespace
                       WHERE ns.nspname='public' AND idx.relname='ix_users_login_prefix'
                         AND i.indisvalid AND i.indisready)";
                await using (var command = new NpgsqlCommand(loginPrefixIndexCheck, connection))
                {
                    var ready = Equals(await command.ExecuteScalarAsync(cancellationToken), true);
                    if (!ready)
                        return Results.Json(new HealthDto(true, true,
                            "Не применена миграция 021: database/migrations/21_user_login_prefix_search.sql.", false, true),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                const string attractionUniquenessCheck = @"
        SELECT EXISTS (SELECT 1 FROM pg_constraint
                       WHERE conrelid='public.attractions'::regclass
                         AND conname='uq_attractions_city_name'
                         AND contype='u' AND convalidated)";
                await using (var command = new NpgsqlCommand(attractionUniquenessCheck, connection))
                {
                    var ready = Equals(await command.ExecuteScalarAsync(cancellationToken), true);
                    if (!ready)
                        return Results.Json(new HealthDto(true, true,
                            "Не применена миграция 023: database/migrations/23_attraction_name_unique_per_city.sql.", false, true),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                const string avatarPermissionCheck = @"
        SELECT
            has_table_privilege(current_user, 'public.user_avatars', 'SELECT')
            AND has_table_privilege(current_user, 'public.user_avatars', 'INSERT')
            AND has_table_privilege(current_user, 'public.user_avatars', 'UPDATE')
            AND has_table_privilege(current_user, 'public.user_avatars', 'DELETE')";
                await using (var avatarPermissionCommand = new NpgsqlCommand(avatarPermissionCheck, connection))
                {
                    var avatarPermissionsReady = Equals(await avatarPermissionCommand.ExecuteScalarAsync(cancellationToken), true);
                    if (!avatarPermissionsReady)
                        return Results.Json(new HealthDto(true, true,
                            "Подключение к PostgreSQL есть, но runtime-роли не хватает прав на public.user_avatars (SELECT, INSERT, UPDATE, DELETE). Выполните database/security/06_update_runtime_permissions.sql.", true, false),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                const string permissionCheck = @"
        SELECT
            has_table_privilege(current_user, 'public.user_sessions', 'SELECT')
            AND has_table_privilege(current_user, 'public.user_sessions', 'INSERT')
            AND has_table_privilege(current_user, 'public.user_sessions', 'UPDATE')
            AND has_table_privilege(current_user, 'public.user_sessions', 'DELETE')
            AND has_table_privilege(current_user, 'public.auth_rate_limit_counters', 'SELECT')
            AND has_table_privilege(current_user, 'public.auth_rate_limit_counters', 'INSERT')
            AND has_table_privilege(current_user, 'public.auth_rate_limit_counters', 'UPDATE')
            AND has_table_privilege(current_user, 'public.auth_rate_limit_counters', 'DELETE')
            AND has_table_privilege(current_user, 'public.user_profile_visibility', 'SELECT')
            AND has_table_privilege(current_user, 'public.user_profile_visibility', 'INSERT')
            AND has_table_privilege(current_user, 'public.user_profile_visibility', 'UPDATE')
            AND has_table_privilege(current_user, 'public.user_profile_visibility', 'DELETE')
            AND has_table_privilege(current_user, 'public.user_friendships', 'SELECT')
            AND has_table_privilege(current_user, 'public.user_friendships', 'INSERT')
            AND has_table_privilege(current_user, 'public.user_friendships', 'UPDATE')
            AND has_table_privilege(current_user, 'public.user_friendships', 'DELETE')
            AND has_table_privilege(current_user, 'public.user_blocks', 'SELECT')
            AND has_table_privilege(current_user, 'public.user_blocks', 'INSERT')
            AND has_table_privilege(current_user, 'public.user_blocks', 'UPDATE')
            AND has_table_privilege(current_user, 'public.user_blocks', 'DELETE')
            AND has_table_privilege(current_user, 'public.users', 'SELECT')
            AND has_table_privilege(current_user, 'public.users', 'INSERT')
            AND has_column_privilege(current_user, 'public.users', 'password_hash', 'UPDATE')
            AND has_column_privilege(current_user, 'public.users', 'display_name', 'UPDATE')
            AND has_column_privilege(current_user, 'public.users', 'residence_country', 'UPDATE')
            AND has_column_privilege(current_user, 'public.users', 'residence_city', 'UPDATE')
            AND has_column_privilege(current_user, 'public.users', 'date_of_birth', 'UPDATE')
            AND has_table_privilege(current_user, 'public.categories', 'SELECT')
            AND has_table_privilege(current_user, 'public.countries', 'SELECT')
            AND has_table_privilege(current_user, 'public.cities', 'SELECT')
            AND has_table_privilege(current_user, 'public.attractions', 'SELECT')
            AND has_table_privilege(current_user, 'public.visits', 'SELECT')
            AND has_table_privilege(current_user, 'public.visits', 'INSERT')
            AND has_table_privilege(current_user, 'public.visits', 'UPDATE')
            AND has_table_privilege(current_user, 'public.visits', 'DELETE')
            AND has_table_privilege(current_user, 'public.photos', 'SELECT')
            AND has_table_privilege(current_user, 'public.photos', 'INSERT')
            AND has_table_privilege(current_user, 'public.photos', 'DELETE')
            AND has_column_privilege(current_user, 'public.photos', 'image_data', 'SELECT')
            AND has_column_privilege(current_user, 'public.photos', 'image_data', 'INSERT')
            AND has_column_privilege(current_user, 'public.photos', 'content_type', 'SELECT')
            AND has_column_privilege(current_user, 'public.photos', 'content_type', 'INSERT')
            AND has_table_privilege(current_user, 'public.wishlist', 'SELECT')
            AND has_table_privilege(current_user, 'public.wishlist', 'INSERT')
            AND has_table_privilege(current_user, 'public.wishlist', 'DELETE')
            AND has_table_privilege(current_user, 'public.user_map_places', 'SELECT')
            AND has_table_privilege(current_user, 'public.user_map_places', 'INSERT')
            AND has_table_privilege(current_user, 'public.user_map_places', 'UPDATE')
            AND has_table_privilege(current_user, 'public.user_map_places', 'DELETE')
            AND has_table_privilege(current_user, 'public.user_map_place_photos', 'SELECT')
            AND has_table_privilege(current_user, 'public.user_map_place_photos', 'INSERT')
            AND has_table_privilege(current_user, 'public.user_map_place_photos', 'DELETE')
            AND has_column_privilege(current_user, 'public.user_map_place_photos', 'image_data', 'SELECT')
            AND has_column_privilege(current_user, 'public.user_map_place_photos', 'image_data', 'INSERT')
            AND has_column_privilege(current_user, 'public.user_map_place_photos', 'content_type', 'SELECT')
            AND has_column_privilege(current_user, 'public.user_map_place_photos', 'content_type', 'INSERT')
            AND has_sequence_privilege(current_user, 'public.users_user_id_seq', 'USAGE')
            AND has_sequence_privilege(current_user, 'public.visits_visit_id_seq', 'USAGE')
            AND has_sequence_privilege(current_user, 'public.photos_photo_id_seq', 'USAGE')
            AND has_sequence_privilege(current_user, 'public.user_map_places_user_map_place_id_seq', 'USAGE')
            AND has_sequence_privilege(current_user, 'public.user_map_place_photos_photo_id_seq', 'USAGE')
";
                await using var command = new NpgsqlCommand(permissionCheck, connection);
                var hasPermissions = Equals(await command.ExecuteScalarAsync(cancellationToken), true);
                if (!hasPermissions)
                    return Results.Json(new HealthDto(true, true,
                        "Подключение к PostgreSQL есть, но runtime-роли не хватает прав для LocaLoca. Выполните database/security/06_update_runtime_permissions.sql после применения миграций.", true, false),
                        statusCode: 503);
                return Results.Ok(new HealthDto(true, true, "Supabase PostgreSQL подключена; права роли LocaLoca проверены."));
            }
            catch (PostgresException ex)
            {
                var mapping = ApiExceptionMiddleware.MapPostgresError(ex.SqlState, ex.Message);
                app.Logger.LogWarning(ex, "Readiness check failed with PostgreSQL SQLSTATE {SqlState}.", ex.SqlState);
                return Results.Json(new HealthDto(true, false, mapping.Message, false, false), statusCode: mapping.StatusCode);
            }
            catch (Exception ex) when (ex is NpgsqlException or DatabaseUnavailableException or TimeoutException)
            {
                app.Logger.LogWarning(ex, "PostgreSQL is unavailable during readiness check.");
                return Results.Json(new HealthDto(true, false,
                    ApiErrorResponse.MessageFor(StatusCodes.Status503ServiceUnavailable), false, false),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}
