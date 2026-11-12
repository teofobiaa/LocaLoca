using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace LocaLoca.Api;

public static class ConnectionStringConfiguration
{
    public static string? ReadUserConnectionString(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
    
            // Backward compatibility for existing installations. The installer
            // migrates this legacy plaintext format to the DPAPI format on upgrade.
            if (root.TryGetProperty("connectionString", out var legacy))
                return legacy.GetString();
    
            if (!root.TryGetProperty("encryptedPassword", out var encryptedPassword))
                return null;
    
            var encryptedBytes = Convert.FromBase64String(encryptedPassword.GetString() ?? string.Empty);
            var passwordBytes = Dpapi.Unprotect(encryptedBytes);
            try
            {
                var password = Encoding.UTF8.GetString(passwordBytes);
                var connection = new NpgsqlConnectionStringBuilder
                {
                    Host = root.GetProperty("host").GetString() ?? throw new InvalidDataException("Missing database host."),
                    Port = root.GetProperty("port").GetInt32(),
                    Database = root.GetProperty("database").GetString() ?? "postgres",
                    Username = root.GetProperty("username").GetString() ?? throw new InvalidDataException("Missing database username."),
                    Password = password,
                    SslMode = SslMode.Require,
                    Pooling = true,
                    Timeout = 15,
                    CommandTimeout = 30,
                    MaxPoolSize = 5
                };
                return connection.ConnectionString;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
            or CryptographicException or FormatException or InvalidOperationException or ArgumentException)
        {
            // Do not silently fall back to an unconfigured connection when a
            // DPAPI secret cannot be decrypted (for example, under another user).
            throw new InvalidOperationException(
                "Не удалось расшифровать пароль Supabase. Запусти установщик под той же учётной записью Windows.",
                ex);
        }
    }

    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Length;
            public IntPtr Data;
        }
    
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved,
            IntPtr prompt, int flags, out DataBlob output);
    
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr memory);
    
        public static byte[] Unprotect(byte[] encrypted)
        {
            var inputPointer = Marshal.AllocHGlobal(encrypted.Length);
            try
            {
                Marshal.Copy(encrypted, 0, inputPointer, encrypted.Length);
                var input = new DataBlob { Length = encrypted.Length, Data = inputPointer };
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, 0, out var output))
                    throw new CryptographicException(Marshal.GetLastWin32Error());
    
                try
                {
                    var clear = new byte[output.Length];
                    Marshal.Copy(output.Data, clear, 0, output.Length);
                    return clear;
                }
                finally
                {
                    if (output.Data != IntPtr.Zero) LocalFree(output.Data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inputPointer);
            }
        }
    }
}
