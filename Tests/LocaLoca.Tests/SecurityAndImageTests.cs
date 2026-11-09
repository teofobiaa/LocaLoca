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

public sealed class SessionStoreTests
{
    private static IConfiguration CreateConfiguration(string? secret) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LOCA_SESSION_SECRET"] = secret
            })
            .Build();

    [Fact]
    public void Create_And_TryGet_RoundTrip_The_User_Id()
    {
        var store = new SessionStore(CreateConfiguration("test-only-session-secret-value-longer-than-32"));
        var token = store.Create(42);

        Assert.True(store.TryGet(token, out var userId));
        Assert.Equal(42, userId);
    }

    [Fact]
    public void TryGet_Rejects_A_Tampered_Token()
    {
        var store = new SessionStore(CreateConfiguration("test-only-session-secret-value-longer-than-32"));
        var token = store.Create(42);
        var characters = token.ToCharArray();
        characters[^1] = characters[^1] == 'A' ? 'B' : 'A';

        Assert.False(store.TryGet(new string(characters), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public void TryGet_Rejects_Malformed_Tokens(string token)
    {
        var store = new SessionStore(CreateConfiguration("test-only-session-secret-value-longer-than-32"));

        Assert.False(store.TryGet(token, out _));
    }

    [Fact]
    public void Constructor_Rejects_A_Configured_Secret_That_Is_Too_Short()
    {
        Assert.Throws<InvalidOperationException>(
            () => new SessionStore(CreateConfiguration("short-test-secret")));
    }

    [Fact]
    public void Local_Development_Store_Generates_Its_Own_Secret()
    {
        var firstStore = new SessionStore(CreateConfiguration(null));
        var secondStore = new SessionStore(CreateConfiguration(null));
        var token = firstStore.Create(7);

        Assert.True(firstStore.TryGet(token, out var userId));
        Assert.Equal(7, userId);
        Assert.False(secondStore.TryGet(token, out _));
    }

    [Fact]
    public void TryGet_Rejects_An_Expired_Session()
    {
        const string secret = "test-only-session-secret-value-longer-than-32";
        var store = new SessionStore(CreateConfiguration(secret));
        var payload = Encoding.UTF8.GetBytes(
            $"42:{DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()}");
        var encodedPayload = Convert.ToBase64String(payload)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.ASCII.GetBytes(encodedPayload));
        var encodedSignature = Convert.ToBase64String(signature)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.False(store.TryGet($"{encodedPayload}.{encodedSignature}", out _));
    }

    [Fact]
    public void TryGet_Rejects_A_Token_Signed_With_A_Different_Secret()
    {
        var first = new SessionStore(CreateConfiguration("test-only-session-secret-value-longer-than-32"));
        var second = new SessionStore(CreateConfiguration("other-test-secret-value-longer-than-32"));

        Assert.False(second.TryGet(first.Create(42), out _));
    }

    [Fact]
    public void Create_Rejects_Non_Positive_User_Ids()
    {
        var store = new SessionStore(CreateConfiguration("test-only-session-secret-value-longer-than-32"));

        Assert.Throws<ArgumentOutOfRangeException>(() => store.Create(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Create(-1));
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
