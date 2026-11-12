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

    [Fact]
    public void Constructor_Accepts_A_Missing_Secret_For_Local_Development()
    {
        var store = new SessionStore(CreateConfiguration(null));

        Assert.NotNull(store);
    }
}

public sealed class ImageContentValidatorTests
{
    private const string ValidPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAYAAACddGYaAAAAE0lEQVR4nGPUCKj4zwAFTAxIAAAsaAHzuDKVEgAAAABJRU5ErkJggg==";

    private const string OversizedPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAALuEAAAABCAIAAABzf/p+AAAAOklEQVR4nO3BMQEAAADCoPVPbQ0PoAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAJwOMpAABE8uH8AAAAABJRU5ErkJggg==";

    private const string TwoFrameGifBase64 =
        "R0lGODlhAgACAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAAAgACAAAIBgABCAQQEAAh+QQBCgABACwAAAAAAgACAIEAAAAAAAAAAAAAAAAIBgABCAQQEAA7";

    [Fact]
    public void Fully_Decodes_A_Valid_Jpeg_And_Reports_Pixel_Dimensions()
    {
        var jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIeJBweHx7/2wBDAQUFBQcGBw4ICA4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqcG1qc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDyyiiivzo/ss//2Q==");

        var result = ImageContentValidator.Validate(jpeg, ".jpg");

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal(1, result.FrameCount);
    }

    [Fact]
    public void Fully_Decodes_A_Valid_Png_And_Reports_Pixel_Dimensions()
    {
        var result = ImageContentValidator.Validate(Convert.FromBase64String(ValidPngBase64), ".png");

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(3, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal(1, result.FrameCount);
    }

    [Fact]
    public void Rejects_A_Truncated_Png_That_Has_A_Valid_Header()
    {
        var complete = Convert.FromBase64String(ValidPngBase64);
        var truncated = complete[..50];

        var result = ImageContentValidator.Validate(truncated, ".png");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Rejects_An_Image_Whose_Actual_Format_Does_Not_Match_Its_Extension()
    {
        var png = Convert.FromBase64String(ValidPngBase64);

        var result = ImageContentValidator.Validate(png, ".jpg");

        Assert.False(result.IsValid);
        Assert.Contains("Расширение", result.Error);
    }

    [Fact]
    public void Rejects_An_Image_That_Exceeds_The_Pixel_Dimension_Limit_Before_Decoding()
    {
        var oversizedPng = Convert.FromBase64String(OversizedPngBase64);

        var result = ImageContentValidator.Validate(oversizedPng, ".png");

        Assert.False(result.IsValid);
        Assert.Contains("Размер изображения", result.Error);
    }

    [Fact]
    public void Counts_And_Fully_Decodes_Every_Frame_Of_An_Animated_Gif()
    {
        var gif = Convert.FromBase64String(TwoFrameGifBase64);

        var result = ImageContentValidator.Validate(gif, ".gif");

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(2, result.FrameCount);
        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".txt")]
    [InlineData(".svg")]
    public void Rejects_Unsupported_Or_Empty_Extensions(string extension)
    {
        var png = Convert.FromBase64String(ValidPngBase64);

        var result = ImageContentValidator.Validate(png, extension);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Cancellation_Is_Propagated_Before_Decoding()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ImageContentValidator.Validate(Convert.FromBase64String(ValidPngBase64), ".png", cancellation.Token));
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
