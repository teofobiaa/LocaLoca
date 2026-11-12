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
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDABQODxIPDRQSEBIXFRQYHjIhHhwcHj0sLiQySUBMS0dARkVQWnNiUFVtVkVGZIhlbXd7gYKBTmCNl4x9lnN+gXz/2wBDARUXFx4aHjshITt8U0ZTfHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHx8fHz/wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAb/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFAEBAAAAAAAAAAAAAAAAAAAAAP/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/AIwAH//Z");

        var result = ImageContentValidator.Validate(jpeg, ".jpg");

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(1, result.Width);
        Assert.Equal(1, result.Height);
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
