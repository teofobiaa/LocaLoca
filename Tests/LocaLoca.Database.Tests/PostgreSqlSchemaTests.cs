using Npgsql;
using Xunit;

namespace LocaLoca.Database.Tests;

[Collection(PostgreSqlDatabaseCollection.Name)]
public sealed class PostgreSqlSchemaTests
{
    private readonly PostgreSqlDatabaseFixture _database;

    public PostgreSqlSchemaTests(PostgreSqlDatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Current_Schema_Creates_All_Account_Data_Tables_And_Photo_Columns()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(@"
            SELECT
                to_regclass('public.users') IS NOT NULL
                AND to_regclass('public.visits') IS NOT NULL
                AND to_regclass('public.photos') IS NOT NULL
                AND to_regclass('public.wishlist') IS NOT NULL
                AND to_regclass('public.user_map_places') IS NOT NULL
                AND to_regclass('public.user_map_place_photos') IS NOT NULL
                AND EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'photos'
                      AND column_name = 'image_data'
                      AND data_type = 'bytea'
                )
                AND EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'user_map_place_photos'
                      AND column_name = 'image_data'
                      AND data_type = 'bytea'
                )", connection);

        Assert.True((bool)(await command.ExecuteScalarAsync() ?? false));
    }

    [Fact]
    public async Task Same_Attraction_Can_Be_Visited_More_Than_Once()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var ids = await InsertUserAndAttractionAsync(connection);

        for (var index = 0; index < 2; index++)
        {
            await using var command = new NpgsqlCommand(@"
                INSERT INTO visits(user_id, attraction_id, visit_date, rating)
                VALUES (@user, @attraction, CURRENT_DATE - @offset, 5)",
                connection);
            command.Parameters.AddWithValue("user", ids.UserId);
            command.Parameters.AddWithValue("attraction", ids.AttractionId);
            command.Parameters.AddWithValue("offset", index);
            await command.ExecuteNonQueryAsync();
        }

        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM visits WHERE user_id = @user AND attraction_id = @attraction",
            connection);
        count.Parameters.AddWithValue("user", ids.UserId);
        count.Parameters.AddWithValue("attraction", ids.AttractionId);

        Assert.Equal(2L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Wishlist_Rejects_Duplicate_User_And_Attraction_Pairs()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var ids = await InsertUserAndAttractionAsync(connection);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO wishlist(user_id, attraction_id) VALUES (@user, @attraction)",
            connection))
        {
            insert.Parameters.AddWithValue("user", ids.UserId);
            insert.Parameters.AddWithValue("attraction", ids.AttractionId);
            await insert.ExecuteNonQueryAsync();
        }

        await using var duplicate = new NpgsqlCommand(
            "INSERT INTO wishlist(user_id, attraction_id) VALUES (@user, @attraction)",
            connection);
        duplicate.Parameters.AddWithValue("user", ids.UserId);
        duplicate.Parameters.AddWithValue("attraction", ids.AttractionId);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task Check_Constraints_Reject_Invalid_Ratings_And_Map_Coordinates()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var ids = await InsertUserAndAttractionAsync(connection);

        await using var invalidVisit = new NpgsqlCommand(@"
            INSERT INTO visits(user_id, attraction_id, visit_date, rating)
            VALUES (@user, @attraction, CURRENT_DATE, 6)",
            connection);
        invalidVisit.Parameters.AddWithValue("user", ids.UserId);
        invalidVisit.Parameters.AddWithValue("attraction", ids.AttractionId);

        var ratingException = await Assert.ThrowsAsync<PostgresException>(
            () => invalidVisit.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ratingException.SqlState);

        await using var invalidPoint = new NpgsqlCommand(@"
            INSERT INTO user_map_places(user_id, name, latitude, longitude, status)
            VALUES (@user, 'Invalid test point', 91, 0, 'planned')",
            connection);
        invalidPoint.Parameters.AddWithValue("user", ids.UserId);

        var coordinateException = await Assert.ThrowsAsync<PostgresException>(
            () => invalidPoint.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, coordinateException.SqlState);
    }

    [Fact]
    public async Task Photo_Constraint_Rejects_An_Unsupported_Content_Type()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var ids = await InsertUserAndAttractionAsync(connection);
        int visitId;

        await using (var visit = new NpgsqlCommand(@"
            INSERT INTO visits(user_id, attraction_id, visit_date, rating)
            VALUES (@user, @attraction, CURRENT_DATE, 5)
            RETURNING visit_id", connection))
        {
            visit.Parameters.AddWithValue("user", ids.UserId);
            visit.Parameters.AddWithValue("attraction", ids.AttractionId);
            visitId = Convert.ToInt32(await visit.ExecuteScalarAsync());
        }

        await using var invalidPhoto = new NpgsqlCommand(@"
            INSERT INTO photos(visit_id, file_path, caption, image_data, content_type)
            VALUES (@visit, 'Photos/test.svg', 'invalid MIME type', @data, 'image/svg+xml')",
            connection);
        invalidPhoto.Parameters.AddWithValue("visit", visitId);
        invalidPhoto.Parameters.AddWithValue("data", new byte[] { 0x3C, 0x73, 0x76, 0x67 });

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => invalidPhoto.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Deleting_A_User_Cascades_Their_Visits_Photos_Wishlist_And_Map_Places()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var ids = await InsertUserAndAttractionAsync(connection);
        int visitId;
        int placeId;

        await using (var visit = new NpgsqlCommand(@"
            INSERT INTO visits(user_id, attraction_id, visit_date, rating)
            VALUES (@user, @attraction, CURRENT_DATE, 5)
            RETURNING visit_id", connection))
        {
            visit.Parameters.AddWithValue("user", ids.UserId);
            visit.Parameters.AddWithValue("attraction", ids.AttractionId);
            visitId = Convert.ToInt32(await visit.ExecuteScalarAsync());
        }

        await using (var photo = new NpgsqlCommand(@"
            INSERT INTO photos(visit_id, file_path, caption, image_data, content_type)
            VALUES (@visit, 'Photos/test.jpg', 'test', @data, 'image/jpeg')", connection))
        {
            photo.Parameters.AddWithValue("visit", visitId);
            photo.Parameters.AddWithValue("data", new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            await photo.ExecuteNonQueryAsync();
        }

        await using (var wishlist = new NpgsqlCommand(
            "INSERT INTO wishlist(user_id, attraction_id) VALUES (@user, @attraction)",
            connection))
        {
            wishlist.Parameters.AddWithValue("user", ids.UserId);
            wishlist.Parameters.AddWithValue("attraction", ids.AttractionId);
            await wishlist.ExecuteNonQueryAsync();
        }

        await using (var place = new NpgsqlCommand(@"
            INSERT INTO user_map_places(user_id, name, latitude, longitude, status)
            VALUES (@user, 'Cascade test point', 10, 20, 'planned')
            RETURNING user_map_place_id", connection))
        {
            place.Parameters.AddWithValue("user", ids.UserId);
            placeId = Convert.ToInt32(await place.ExecuteScalarAsync());
        }

        await using (var mapPhoto = new NpgsqlCommand(@"
            INSERT INTO user_map_place_photos(user_map_place_id, file_path, caption, image_data, content_type)
            VALUES (@place, 'Photos/map-test.jpg', 'test', @data, 'image/jpeg')", connection))
        {
            mapPhoto.Parameters.AddWithValue("place", placeId);
            mapPhoto.Parameters.AddWithValue("data", new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            await mapPhoto.ExecuteNonQueryAsync();
        }

        await using (var deleteUser = new NpgsqlCommand(
            "DELETE FROM users WHERE user_id = @user",
            connection))
        {
            deleteUser.Parameters.AddWithValue("user", ids.UserId);
            Assert.Equal(1, await deleteUser.ExecuteNonQueryAsync());
        }

        await using var remaining = new NpgsqlCommand(@"
            SELECT
                (SELECT count(*) FROM visits WHERE user_id = @user) +
                (SELECT count(*) FROM wishlist WHERE user_id = @user) +
                (SELECT count(*) FROM user_map_places WHERE user_id = @user) +
                (SELECT count(*) FROM photos WHERE visit_id = @visit) +
                (SELECT count(*) FROM user_map_place_photos WHERE user_map_place_id = @place)",
            connection);
        remaining.Parameters.AddWithValue("user", ids.UserId);
        remaining.Parameters.AddWithValue("visit", visitId);
        remaining.Parameters.AddWithValue("place", placeId);

        Assert.Equal(0L, Convert.ToInt64(await remaining.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Documented_Migrations_Are_Idempotent_And_Grant_Only_Runtime_Permissions()
    {
        await using var connection = await _database.OpenConnectionAsync();

        await _database.ExecuteSqlFileAsync("security", "05_create_app_role.sql");

        await _database.ExecuteSqlFileAsync("migrations", "03_migrate_wishlist.sql");
        await _database.ExecuteSqlFileAsync("migrations", "03_migrate_wishlist.sql");
        await _database.ExecuteSqlFileAsync("migrations", "04_migrate_user_map_places.sql");
        await _database.ExecuteSqlFileAsync("migrations", "04_migrate_user_map_places.sql");
        await _database.ExecuteSqlFileAsync("migrations", "08_server_sessions.sql");
        await _database.ExecuteSqlFileAsync("migrations", "08_server_sessions.sql");
        await _database.ExecuteSqlFileAsync("security", "06_update_runtime_permissions.sql");
        await _database.ExecuteSqlFileAsync("security", "06_update_runtime_permissions.sql");
        await _database.ExecuteSqlFileAsync("migrations", "07_migrate_photo_storage.sql");
        await _database.ExecuteSqlFileAsync("migrations", "07_migrate_photo_storage.sql");

        await using var permissionCheck = new NpgsqlCommand(@"
            SELECT
                has_column_privilege('localoca_app', 'public.users', 'display_name', 'UPDATE')
                AND has_table_privilege('localoca_app', 'public.user_map_places', 'SELECT')
                AND has_table_privilege('localoca_app', 'public.user_map_places', 'INSERT')
                AND has_table_privilege('localoca_app', 'public.user_map_places', 'UPDATE')
                AND has_table_privilege('localoca_app', 'public.user_map_places', 'DELETE')
                AND has_table_privilege('localoca_app', 'public.user_map_place_photos', 'SELECT')
                AND has_table_privilege('localoca_app', 'public.user_map_place_photos', 'INSERT')
                AND has_table_privilege('localoca_app', 'public.user_map_place_photos', 'DELETE')
                AND has_column_privilege('localoca_app', 'public.photos', 'image_data', 'SELECT')
                AND has_column_privilege('localoca_app', 'public.user_map_place_photos', 'image_data', 'INSERT')
                AND NOT (SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls
                         FROM pg_roles WHERE rolname = 'localoca_app')
                AND NOT has_schema_privilege('localoca_app', 'public', 'CREATE')
                AND NOT has_table_privilege('localoca_app', 'public.users', 'DELETE')
                AND NOT pg_has_role('localoca_app', 'pg_read_all_data', 'MEMBER')",
            connection);

        Assert.True((bool)(await permissionCheck.ExecuteScalarAsync() ?? false));
    }

    [Fact]
    public async Task Schema_Contains_Expected_Foreign_Keys_And_Query_Indexes()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(@"
            SELECT
                EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conrelid = 'public.cities'::regclass
                          AND confrelid = 'public.countries'::regclass AND contype = 'f')
                AND EXISTS (SELECT 1 FROM pg_constraint
                            WHERE conrelid = 'public.attractions'::regclass
                              AND confrelid = 'public.categories'::regclass AND contype = 'f')
                AND EXISTS (SELECT 1 FROM pg_constraint
                            WHERE conrelid = 'public.visits'::regclass
                              AND confrelid = 'public.users'::regclass AND contype = 'f'
                              AND confdeltype = 'c')
                AND EXISTS (SELECT 1 FROM pg_constraint
                            WHERE conrelid = 'public.photos'::regclass
                              AND confrelid = 'public.visits'::regclass AND contype = 'f'
                              AND confdeltype = 'c')
                AND EXISTS (SELECT 1 FROM pg_constraint
                            WHERE conrelid = 'public.user_map_place_photos'::regclass
                              AND confrelid = 'public.user_map_places'::regclass AND contype = 'f'
                              AND confdeltype = 'c')
                AND to_regclass('public.ix_attractions_city') IS NOT NULL
                AND to_regclass('public.ix_attractions_category') IS NOT NULL
                AND to_regclass('public.ix_visits_user_date') IS NOT NULL
                AND to_regclass('public.ix_visits_attraction') IS NOT NULL
                AND to_regclass('public.ix_photos_visit') IS NOT NULL
                AND to_regclass('public.ix_wishlist_attraction') IS NOT NULL
                AND to_regclass('public.ix_user_map_places_user') IS NOT NULL
                AND to_regclass('public.ix_user_map_place_photos') IS NOT NULL",
            connection);

        Assert.True((bool)(await command.ExecuteScalarAsync() ?? false));
    }

    [Fact]
    public async Task Foreign_Keys_Reject_References_To_Missing_Parents()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(@"
            INSERT INTO visits(user_id, attraction_id, visit_date, rating)
            VALUES (-1, -1, CURRENT_DATE, 5)", connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    private static async Task<(int UserId, int AttractionId)> InsertUserAndAttractionAsync(
        NpgsqlConnection connection)
    {
        var suffix = Guid.NewGuid().ToString("N");

        await using var userCommand = new NpgsqlCommand(@"
            INSERT INTO users(login, password_hash, display_name)
            VALUES (@login, 'test-hash-not-a-credential', 'Database test user')
            RETURNING user_id",
            connection);
        userCommand.Parameters.AddWithValue("login", "database-test-" + suffix);
        var userId = Convert.ToInt32(await userCommand.ExecuteScalarAsync());

        int categoryId;
        await using (var categoryCommand = new NpgsqlCommand(
            "INSERT INTO categories(name) VALUES (@name) RETURNING category_id",
            connection))
        {
            categoryCommand.Parameters.AddWithValue("name", "Category " + suffix);
            categoryId = Convert.ToInt32(await categoryCommand.ExecuteScalarAsync());
        }

        int countryId;
        await using (var countryCommand = new NpgsqlCommand(
            "INSERT INTO countries(name) VALUES (@name) RETURNING country_id",
            connection))
        {
            countryCommand.Parameters.AddWithValue("name", "Country " + suffix);
            countryId = Convert.ToInt32(await countryCommand.ExecuteScalarAsync());
        }

        int cityId;
        await using (var cityCommand = new NpgsqlCommand(@"
            INSERT INTO cities(country_id, name) VALUES (@country, @name)
            RETURNING city_id",
            connection))
        {
            cityCommand.Parameters.AddWithValue("country", countryId);
            cityCommand.Parameters.AddWithValue("name", "City " + suffix);
            cityId = Convert.ToInt32(await cityCommand.ExecuteScalarAsync());
        }

        await using var attractionCommand = new NpgsqlCommand(@"
            INSERT INTO attractions(name, description, latitude, longitude, category_id, city_id)
            VALUES (@name, '', 10, 20, @category, @city)
            RETURNING attraction_id",
            connection);
        attractionCommand.Parameters.AddWithValue("name", "Attraction " + suffix);
        attractionCommand.Parameters.AddWithValue("category", categoryId);
        attractionCommand.Parameters.AddWithValue("city", cityId);
        var attractionId = Convert.ToInt32(await attractionCommand.ExecuteScalarAsync());

        return (userId, attractionId);
    }
}
