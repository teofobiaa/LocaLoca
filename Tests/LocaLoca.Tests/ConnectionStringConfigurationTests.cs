using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocaLoca.Api;
using Npgsql;
using Xunit;

namespace LocaLoca.Tests;

public sealed class ConnectionStringConfigurationTests
{
    [Fact]
    public void ReadUserConnectionString_ReturnsNull_WhenConfigurationFileIsMissing()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "server-config.json");

            Assert.Null(ConnectionStringConfiguration.ReadUserConnectionString(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadUserConnectionString_ReturnsNull_WhenNoLegacyOrEncryptedPasswordExists()
    {
        WithConfiguration(new { version = 2, host = "db.example.test" }, path =>
        {
            Assert.Null(ConnectionStringConfiguration.ReadUserConnectionString(path));
        });
    }

    [Fact]
    public void ReadUserConnectionString_PreservesLegacyPlaintextConfiguration()
    {
        const string legacyConnectionString =
            "Host=db.example.test;Port=5432;Database=postgres;Username=localoca_app.test;Password=legacy-test-password;SSL Mode=Require";

        WithConfiguration(new { connectionString = legacyConnectionString }, path =>
        {
            Assert.Equal(
                legacyConnectionString,
                ConnectionStringConfiguration.ReadUserConnectionString(path));
        });
    }

    [Fact]
    public void ReadUserConnectionString_WrapsMalformedJson_WithoutEchoingSecretMaterial()
    {
        const string sentinel = "must-never-appear-in-an-error-message";
        WithConfigurationText("{\"password\":\"" + sentinel + "\",\"encryptedPassword\":", path =>
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => ConnectionStringConfiguration.ReadUserConnectionString(path));

            Assert.IsType<JsonException>(exception.InnerException);
            Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
            Assert.Contains("Не удалось расшифровать пароль Supabase", exception.Message);
        });
    }

    [Fact]
    public void ReadUserConnectionString_RejectsMalformedBase64_WithoutEchoingSecretMaterial()
    {
        const string sentinel = "must-never-appear-in-an-error-message";
        WithConfiguration(new
        {
            version = 2,
            host = "db.example.test",
            port = 5432,
            database = "postgres",
            username = "localoca_app.test",
            encryptedPassword = "not-valid-base64",
            password = sentinel
        }, path =>
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => ConnectionStringConfiguration.ReadUserConnectionString(path));

            Assert.IsType<FormatException>(exception.InnerException);
            Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
        });
    }

    internal static void WithConfiguration<T>(T configuration, Action<string> action) =>
        WithConfigurationText(JsonSerializer.Serialize(configuration), action);

    private static void WithConfigurationText(string json, Action<string> action)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "server-config.json");
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            action(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "LocaLoca-dpapi-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

public sealed class WindowsDpapiConfigurationTests
{
    [WindowsFact]
    public void ReadUserConnectionString_DecryptsPassword_AndBuildsSecureConnectionString()
    {
        const string password = "test;password=with-special-characters";
        var encryptedPassword = ProtectPassword(password);

        ConnectionStringConfigurationTests.WithConfiguration(
            new
            {
                version = 2,
                host = "db.example.test",
                port = 6543,
                database = "postgres",
                username = "localoca_app.test",
                encryptedPassword
            },
            path =>
            {
                var connectionString = ConnectionStringConfiguration.ReadUserConnectionString(path);

                Assert.NotNull(connectionString);
                var builder = new NpgsqlConnectionStringBuilder(connectionString);
                Assert.Equal("db.example.test", builder.Host);
                Assert.Equal(6543, builder.Port);
                Assert.Equal("postgres", builder.Database);
                Assert.Equal("localoca_app.test", builder.Username);
                Assert.Equal(password, builder.Password);
                Assert.Equal(SslMode.Require, builder.SslMode);
                Assert.True(builder.Pooling);
            });
    }

    [WindowsFact]
    public void ReadUserConnectionString_ReportsMissingRequiredFields_WithoutLeakingDecryptedPassword()
    {
        const string password = "sensitive-test-password";
        var encryptedPassword = ProtectPassword(password);

        ConnectionStringConfigurationTests.WithConfiguration(
            new
            {
                version = 2,
                port = 5432,
                database = "postgres",
                username = "localoca_app.test",
                encryptedPassword
            },
            path =>
            {
                var exception = Assert.Throws<InvalidOperationException>(
                    () => ConnectionStringConfiguration.ReadUserConnectionString(path));

                Assert.IsType<KeyNotFoundException>(exception.InnerException);
                Assert.DoesNotContain(password, exception.ToString(), StringComparison.Ordinal);
            });
    }

    [WindowsFact]
    public void ReadUserConnectionString_RejectsMalformedDpapiCiphertext()
    {
        ConnectionStringConfigurationTests.WithConfiguration(
            new
            {
                version = 2,
                host = "db.example.test",
                port = 5432,
                database = "postgres",
                username = "localoca_app.test",
                encryptedPassword = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 })
            },
            path =>
            {
                var exception = Assert.Throws<InvalidOperationException>(
                    () => ConnectionStringConfiguration.ReadUserConnectionString(path));

                Assert.IsType<CryptographicException>(exception.InnerException);
                Assert.DoesNotContain("password", exception.ToString(), StringComparison.OrdinalIgnoreCase);
            });
    }

    private static string ProtectPassword(string password)
    {
        var clearBytes = Encoding.UTF8.GetBytes(password);
        byte[]? encryptedBytes = null;
        try
        {
            encryptedBytes = ConnectionStringConfiguration.Dpapi.Protect(clearBytes);
            return Convert.ToBase64String(encryptedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            if (encryptedBytes is not null)
                CryptographicOperations.ZeroMemory(encryptedBytes);
        }
    }
}

public sealed class LocalSessionSecretPersistenceTests
{
    [WindowsFact]
    public void LocalSessionSecret_IsProtected_AndReusedAfterRestart()
    {
        var directory = ConnectionStringConfigurationTests.CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "session-secret.dpapi");
            var first = ConnectionStringConfiguration.ReadOrCreateLocalSessionSecret(path);
            var encryptedOnDisk = File.ReadAllBytes(path);
            var clearBytes = Convert.FromBase64String(first);

            try
            {
                var second = ConnectionStringConfiguration.ReadOrCreateLocalSessionSecret(path);

                Assert.Equal(first, second);
                Assert.True(first.Length >= 32);
                Assert.False(encryptedOnDisk.SequenceEqual(clearBytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptedOnDisk);
                CryptographicOperations.ZeroMemory(clearBytes);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void LocalSessionSecret_RejectsCorruptedDpapiFile()
    {
        var directory = ConnectionStringConfigurationTests.CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "session-secret.dpapi");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });

            Assert.Throws<CryptographicException>(
                () => ConnectionStringConfiguration.ReadOrCreateLocalSessionSecret(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void LocalSessionSecret_RejectsDecryptedKeysThatAreTooShort()
    {
        var directory = ConnectionStringConfigurationTests.CreateTemporaryDirectory();
        byte[]? clearBytes = null;
        byte[]? encryptedBytes = null;
        try
        {
            var path = Path.Combine(directory, "session-secret.dpapi");
            clearBytes = Encoding.UTF8.GetBytes("short");
            encryptedBytes = ConnectionStringConfiguration.Dpapi.Protect(clearBytes);
            File.WriteAllBytes(path, encryptedBytes);

            Assert.Throws<InvalidDataException>(
                () => ConnectionStringConfiguration.ReadOrCreateLocalSessionSecret(path));
        }
        finally
        {
            if (clearBytes is not null) CryptographicOperations.ZeroMemory(clearBytes);
            if (encryptedBytes is not null) CryptographicOperations.ZeroMemory(encryptedBytes);
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Requires Windows DPAPI (CurrentUser); executed by the dedicated Windows CI job.";
    }
}
