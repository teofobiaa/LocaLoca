using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace LocaLoca.Database.Tests;

public sealed class MigrationManifestTests
{
    [Fact]
    public async Task Published_Database_Scripts_Match_Their_Pinned_Versions_And_Checksums()
    {
        var databaseDirectory = Path.Combine(AppContext.BaseDirectory, "Database");
        var manifestPath = Path.Combine(databaseDirectory, "migrations", "manifest.json");
        Assert.True(File.Exists(manifestPath), "The database version manifest was not copied to the test output.");

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var root = manifest.RootElement;
        Assert.Equal(1, root.GetProperty("manifestVersion").GetInt32());
        Assert.Equal(9, root.GetProperty("currentSchemaVersion").GetInt32());
        Assert.Equal(9, root.GetProperty("baseline").GetProperty("version").GetInt32());

        var schemaVersions = new[] { 3, 4, 7, 8, 9 };
        var actualVersions = root.GetProperty("schemaMigrations").EnumerateArray()
            .Select(x => x.GetProperty("version").GetInt32()).ToArray();
        Assert.Equal(schemaVersions, actualVersions);
        Assert.Equal(schemaVersions, root.GetProperty("baseline").GetProperty("includesSchemaMigrations")
            .EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(new[] { 5, 6 }, root.GetProperty("securityScripts").EnumerateArray()
            .Select(x => x.GetProperty("version").GetInt32()).ToArray());

        var entries = new List<JsonElement> { root.GetProperty("baseline"), root.GetProperty("seed"), root.GetProperty("trackingBootstrap") };
        entries.AddRange(root.GetProperty("schemaMigrations").EnumerateArray());
        entries.AddRange(root.GetProperty("securityScripts").EnumerateArray());
        foreach (var entry in entries)
        {
            var relativePath = entry.GetProperty("path").GetString()
                ?? throw new InvalidOperationException("A manifest entry has no path.");
            var expectedHash = entry.GetProperty("sha256").GetString();
            Assert.False(string.IsNullOrWhiteSpace(expectedHash), $"No SHA-256 is pinned for {relativePath}");
            var fullPath = Path.Combine(databaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"Manifest SQL file is missing: {relativePath}");
            var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullPath))).ToLowerInvariant();
            Assert.Equal(expectedHash, actualHash);
        }
    }
}
