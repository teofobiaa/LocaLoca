using System.Security.Cryptography;
using System.Text;
using LocaLoca.Api.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LocaLoca.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_Uses_A_Random_Salt_And_Never_Returns_The_Plaintext()
    {
        const string password = "unit-test-password";
        var first = PasswordHasher.Hash(password);
        var second = PasswordHasher.Hash(password);

        Assert.False(string.Equals(password, first, StringComparison.Ordinal),
            "A password hash must not equal the original password.");
        Assert.False(string.Equals(password, second, StringComparison.Ordinal),
            "A password hash must not equal the original password.");
        Assert.False(string.Equals(first, second, StringComparison.Ordinal),
            "Independent hashes must use independent random salts.");
        Assert.True(PasswordHasher.Verify(password, first));
        Assert.True(PasswordHasher.Verify(password, second));
    }

    [Fact]
    public void Verify_Rejects_A_Wrong_Password()
    {
        var hash = PasswordHasher.Hash("correct-test-password");

        Assert.False(PasswordHasher.Verify("different-test-password", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("100000.not-base64.also-not-base64")]
    [InlineData("9999999999.AA==.AA==")]
    [InlineData("100000.AA==.AA==")]
    public void Verify_Rejects_Malformed_Stored_Values(string storedHash)
    {
        Assert.False(PasswordHasher.Verify("any-test-password", storedHash));
    }

    [Fact]
    public void Hash_Rejects_An_Empty_Password()
    {
        Assert.Throws<ArgumentException>(() => PasswordHasher.Hash(string.Empty));
    }

    [Fact]
    public void Verify_Accepts_Legacy_Hashes_And_Requests_An_Upgrade()
    {
        const string password = "legacy-test-password";
        var legacyHash = CreateLegacyHash(password, 100_000);

        Assert.True(PasswordHasher.Verify(password, legacyHash));
        Assert.False(PasswordHasher.Verify("wrong-password", legacyHash));
        Assert.True(PasswordHasher.NeedsRehash(legacyHash));
    }

    [Fact]
    public void NeedsRehash_Detects_A_Work_Factor_Below_The_Configured_Target()
    {
        var hash = PasswordHasher.Hash("cost-test-password", 10_000);

        Assert.True(PasswordHasher.Verify("cost-test-password", hash));
        Assert.True(PasswordHasher.NeedsRehash(hash, 600_000));
        Assert.False(PasswordHasher.NeedsRehash(hash, 10_000));
    }

    [Fact]
    public void UpgradeIterations_Never_Downgrades_A_Stronger_Hash()
    {
        var strongerHash = PasswordHasher.Hash("stronger-test-password", 200_000);

        Assert.Equal(200_000, PasswordHasher.GetUpgradeIterations(strongerHash, 100_000));
    }

    [Theory]
    [InlineData("9999")]
    [InlineData("2000001")]
    [InlineData("not-a-number")]
    public void Configured_Iterations_Reject_Unsupported_Values(string value)
    {
        Assert.Throws<InvalidOperationException>(() => PasswordHasher.GetConfiguredIterations(value));
    }

    private static string CreateLegacyHash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return iterations.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
               Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }
}

public sealed class SessionStoreConfigurationTests
{
    private static IConfiguration CreateConfiguration(string? secret) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LOCA_SESSION_SECRET"] = secret
            })
            .Build();

    [Fact]
    public void Constructor_Rejects_A_Configured_Secret_That_Is_Too_Short()
    {
        Assert.Throws<InvalidOperationException>(
            () => new SessionStore(CreateConfiguration("short-test-secret")));
    }
}

public sealed class ImageContentValidatorTests
{
    [Fact]
    public void Accepts_Valid_Jpeg_Signature()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xD9];

        Assert.True(ImageContentValidator.HasValidImageSignature(jpeg, ".jpg"));
        Assert.True(ImageContentValidator.HasValidImageSignature(jpeg, ".jpeg"));
    }

    [Fact]
    public void Accepts_Valid_Png_Signature()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        Assert.True(ImageContentValidator.HasValidImageSignature(png, ".png"));
    }

    [Fact]
    public void Accepts_Valid_Webp_Gif_And_Bmp_Signatures()
    {
        byte[] webp = [0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50];
        byte[] gif = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61];
        byte[] bmp = [0x42, 0x4D, 0x00, 0x00];

        Assert.True(ImageContentValidator.HasValidImageSignature(webp, ".webp"));
        Assert.True(ImageContentValidator.HasValidImageSignature(gif, ".gif"));
        Assert.True(ImageContentValidator.HasValidImageSignature(bmp, ".bmp"));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".webp")]
    [InlineData(".gif")]
    [InlineData(".bmp")]
    [InlineData(".txt")]
    public void Rejects_Bytes_That_Do_Not_Match_The_Extension(string extension)
    {
        byte[] invalid = [0x00, 0x01, 0x02, 0x03];

        Assert.False(ImageContentValidator.HasValidImageSignature(invalid, extension));
    }

    [Fact]
    public void Maps_Supported_Extensions_To_Content_Types()
    {
        Assert.Equal("image/jpeg", ImageContentValidator.GetContentType("photo.jpg"));
        Assert.Equal("image/png", ImageContentValidator.GetContentType("photo.png"));
        Assert.Equal("image/webp", ImageContentValidator.GetContentType("photo.webp"));
        Assert.Equal("image/gif", ImageContentValidator.GetContentType("photo.gif"));
        Assert.Equal("image/bmp", ImageContentValidator.GetContentType("photo.bmp"));
    }
}
