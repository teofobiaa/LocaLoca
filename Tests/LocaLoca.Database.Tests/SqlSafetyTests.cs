using System.Text.RegularExpressions;
using Xunit;

namespace LocaLoca.Database.Tests;

public sealed class SqlSafetyTests
{
    [Fact]
    public void All_Database_Sql_Files_Are_Included_In_The_Test_Build()
    {
        var databaseDirectory = Path.Combine(AppContext.BaseDirectory, "Database");
        Assert.True(Directory.Exists(databaseDirectory), "Database SQL assets were not copied to the test output.");

        var files = Directory.GetFiles(databaseDirectory, "*.sql", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var relativePaths = files
            .Select(path => Path.GetRelativePath(databaseDirectory, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("schema/01_schema.sql", relativePaths);
        Assert.Contains("seed/02_seed.sql", relativePaths);
        Assert.Contains("migrations/03_migrate_wishlist.sql", relativePaths);
        Assert.Contains("migrations/04_migrate_user_map_places.sql", relativePaths);
        Assert.Contains("migrations/07_migrate_photo_storage.sql", relativePaths);
        Assert.Contains("security/05_create_app_role.sql", relativePaths);
        Assert.Contains("security/06_update_runtime_permissions.sql", relativePaths);
    }

    [Fact]
    public async Task Sql_Files_Do_Not_Contain_Remote_Database_Connection_Strings_Or_Psql_Switches()
    {
        var databaseDirectory = Path.Combine(AppContext.BaseDirectory, "Database");
        var files = Directory.GetFiles(databaseDirectory, "*.sql", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var sql = await File.ReadAllTextAsync(file);
            Assert.DoesNotMatch(
                new Regex(@"(?i)\b(?:postgres|postgresql)://[^\s'"";]+"),
                sql);
            Assert.DoesNotMatch(
                new Regex(@"(?m)^\s*\\(?:connect|c|!|include|i)\b"),
                sql);
            Assert.DoesNotMatch(
                new Regex(@"(?is)\bCOPY\b[\s\S]{0,300}\bPROGRAM\b"),
                sql);
        }
    }
}
