using System.Globalization;
using System.Security.Cryptography;

namespace LocaLoca.Api.Security;

public static class PasswordHasher
{
    // Keep the deployed cost until the production API host has been benchmarked.
    // Set LOCA_PASSWORD_PBKDF2_ITERATIONS only after reviewing target-host results.
    public const int DefaultIterations = 100_000;
    public const int MinimumAcceptedIterations = 10_000;
    public const int MaximumAcceptedIterations = 2_000_000;

    private const string CurrentScheme = "pbkdf2-sha256";
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ValidateIterations(iterations);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        // A self-describing format allows multiple algorithms and costs to coexist.
        return CurrentScheme + "$" +
               iterations.ToString(CultureInfo.InvariantCulture) + "$" +
               Convert.ToBase64String(salt) + "$" +
               Convert.ToBase64String(hash);
    }

    public static bool Verify(string password, string? storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        if (!TryParse(storedHash, out var iterations, out var salt, out var expected, out _))
            return false;

        try
        {
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool NeedsRehash(string? storedHash, int targetIterations = DefaultIterations)
    {
        ValidateIterations(targetIterations);

        if (!TryParse(storedHash, out var iterations, out _, out _, out var isLegacyFormat))
            return false;

        return isLegacyFormat || iterations < targetIterations;
    }

    public static int GetUpgradeIterations(string? storedHash, int targetIterations = DefaultIterations)
    {
        ValidateIterations(targetIterations);

        // Never downgrade an already stronger PBKDF2 record.
        return TryParse(storedHash, out var storedIterations, out _, out _, out _)
            ? Math.Max(targetIterations, storedIterations)
            : targetIterations;
    }

    public static int GetConfiguredIterations(string? configuredValue)
    {
        if (configuredValue is null)
            return DefaultIterations;

        if (!int.TryParse(configuredValue, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations))
            throw new InvalidOperationException(
                "LOCA_PASSWORD_PBKDF2_ITERATIONS must be an integer between 10000 and 2000000.");

        try
        {
            ValidateIterations(iterations);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException(
                "LOCA_PASSWORD_PBKDF2_ITERATIONS must be an integer between 10000 and 2000000.",
                exception);
        }

        return iterations;
    }

    public static void ValidateIterations(int iterations)
    {
        if (iterations is < MinimumAcceptedIterations or > MaximumAcceptedIterations)
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                "PBKDF2 iterations must be between 10000 and 2000000.");
    }

    private static bool TryParse(
        string? storedHash,
        out int iterations,
        out byte[] salt,
        out byte[] expected,
        out bool isLegacyFormat)
    {
        iterations = 0;
        salt = [];
        expected = [];
        isLegacyFormat = false;

        if (string.IsNullOrWhiteSpace(storedHash))
            return false;

        try
        {
            string[] parts;
            if (storedHash.Contains('.') && storedHash.IndexOf((char)36) < 0)
            {
                // Legacy format: iterations.salt.hash
                parts = storedHash.Split('.');
                isLegacyFormat = true;
                if (parts.Length != 3 ||
                    !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out iterations))
                    return false;

                salt = Convert.FromBase64String(parts[1]);
                expected = Convert.FromBase64String(parts[2]);
            }
            else
            {
                // Current format: pbkdf2-sha256$iterations$salt$hash
                parts = storedHash.Split((char)36);
                if (parts.Length != 4 ||
                    !string.Equals(parts[0], CurrentScheme, StringComparison.Ordinal) ||
                    !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations))
                    return false;

                salt = Convert.FromBase64String(parts[2]);
                expected = Convert.FromBase64String(parts[3]);
            }

            return iterations is >= MinimumAcceptedIterations and <= MaximumAcceptedIterations
                && salt.Length == SaltSize
                && expected.Length == HashSize;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
