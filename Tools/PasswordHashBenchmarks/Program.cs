using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

const byte[] password = "LocaLoca-password-hash-benchmark-only"u8.ToArray();
var samples = args.Length > 0 && int.TryParse(args[0], out var parsedSamples)
    ? Math.Clamp(parsedSamples, 5, 100)
    : 15;

Console.WriteLine("LocaLoca password KDF benchmark");
Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Logical processors: {Environment.ProcessorCount}");
Console.WriteLine($"Samples per case: {samples}");
Console.WriteLine("Run on the exact production API host class/container plan.");
Console.WriteLine();

foreach (var iterations in new[] { 100_000, 300_000, 600_000, 1_000_000 })
{
    await MeasureAsync(
        $"PBKDF2-HMAC-SHA256 / {iterations:N0} iterations",
        samples,
        () =>
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var derived = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, iterations, HashAlgorithmName.SHA256, 32);
            CryptographicOperations.ZeroMemory(derived);
            CryptographicOperations.ZeroMemory(salt);
            return Task.CompletedTask;
        });
}

Console.WriteLine();
Console.WriteLine("Argon2id candidates:");
await MeasureAsync("Argon2id / m=19 MiB, t=2, p=1", samples,
    () => DeriveArgon2Async(password, 19_456, 2));
await MeasureAsync("Argon2id / m=46 MiB, t=1, p=1", samples,
    () => DeriveArgon2Async(password, 47_104, 1));
await MeasureAsync("Argon2id / m=64 MiB, t=3, p=1", samples,
    () => DeriveArgon2Async(password, 65_536, 3));

Console.WriteLine();
Console.WriteLine("Concurrent derivation latency (two simultaneous requests):");
await MeasureConcurrentAsync("PBKDF2-HMAC-SHA256 / 600,000 iterations", 5,
    () => DerivePbkdf2Async(password, 600_000));
await MeasureConcurrentAsync("Argon2id / m=19 MiB, t=2, p=1", 5,
    () => DeriveArgon2Async(password, 19_456, 2));

static Task DerivePbkdf2Async(byte[] password, int iterations)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var derived = Rfc2898DeriveBytes.Pbkdf2(
        password, salt, iterations, HashAlgorithmName.SHA256, 32);
    CryptographicOperations.ZeroMemory(derived);
    CryptographicOperations.ZeroMemory(salt);
    return Task.CompletedTask;
}

static Task DeriveArgon2Async(byte[] password, int memoryKiB, int iterations)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
        .WithVersion(Argon2Parameters.Version13)
        .WithIterations(iterations)
        .WithMemoryAsKB(memoryKiB)
        .WithParallelism(1)
        .WithSalt(salt)
        .Build();

    var generator = new Argon2BytesGenerator();
    generator.Init(parameters);
    var derived = new byte[32];
    generator.GenerateBytes(password, derived);

    CryptographicOperations.ZeroMemory(derived);
    CryptographicOperations.ZeroMemory(salt);
    parameters.Clear();
    return Task.CompletedTask;
}

static async Task MeasureAsync(string name, int samples, Func<Task> action)
{
    await action(); // warm-up
    var timings = new double[samples];
    for (var i = 0; i < samples; i++)
    {
        var stopwatch = Stopwatch.StartNew();
        await action();
        stopwatch.Stop();
        timings[i] = stopwatch.Elapsed.TotalMilliseconds;
    }

    PrintSummary(name, timings);
}

static async Task MeasureConcurrentAsync(string name, int batches, Func<Task> action)
{
    var timings = new double[batches];
    for (var i = 0; i < batches; i++)
    {
        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(action)));
        stopwatch.Stop();
        timings[i] = stopwatch.Elapsed.TotalMilliseconds;
    }

    PrintSummary(name + " (batch of 2)", timings);
}

static void PrintSummary(string name, double[] timings)
{
    Array.Sort(timings);
    Console.WriteLine(
        $"{name}: p50={Percentile(timings, 0.50):N1} ms, p95={Percentile(timings, 0.95):N1} ms");
}

static double Percentile(double[] sorted, double percentile)
{
    var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
    return sorted[index];
}
