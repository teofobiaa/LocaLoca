using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace LocaLoca.Services;

/// <summary>
/// Persists the desktop session token as a DPAPI CurrentUser-protected blob.
/// The stored credential is bound to the API origin that issued it.
/// </summary>
internal static class WindowsProtectedSessionTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocaLoca",
        "session-token.dpapi");

    public static string? Load(Uri apiBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        var path = FilePath;
        if (!File.Exists(path)) return null;

        var encrypted = File.ReadAllBytes(path);
        byte[]? clear = null;
        try
        {
            clear = Dpapi.Unprotect(encrypted);
            var credential = JsonSerializer.Deserialize<StoredCredential>(clear, JsonOptions);
            if (credential is null ||
                string.IsNullOrWhiteSpace(credential.Token) ||
                !string.Equals(credential.ApiOrigin, GetOrigin(apiBaseAddress), StringComparison.OrdinalIgnoreCase))
            {
                Delete();
                return null;
            }

            return credential.Token;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            // A token protected under another Windows account, or a damaged
            // credential file, must never be treated as an authenticated session.
            try { Delete(); }
            catch (Exception deleteException) when (deleteException is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not remove an invalid saved session: {deleteException.Message}");
            }

            Debug.WriteLine($"The saved Loca Loca session could not be restored: {exception.Message}");
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (clear is not null)
                CryptographicOperations.ZeroMemory(clear);
        }
    }

    public static void Save(Uri apiBaseAddress, string token)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var path = FilePath;
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The Windows session file has no parent directory.");
        Directory.CreateDirectory(directory);

        var clear = JsonSerializer.SerializeToUtf8Bytes(
            new StoredCredential(GetOrigin(apiBaseAddress), token),
            JsonOptions);
        var encrypted = Dpapi.Protect(clear);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, encrypted);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static void Delete()
    {
        var path = FilePath;
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string GetOrigin(Uri apiBaseAddress) =>
        apiBaseAddress.GetLeftPart(UriPartial.Authority).TrimEnd('/');

    private sealed record StoredCredential(string ApiOrigin, string Token);

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
        private static extern bool CryptProtectData(
            ref DataBlob input,
            string description,
            IntPtr entropy,
            IntPtr reserved,
            IntPtr prompt,
            int flags,
            out DataBlob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob input,
            IntPtr description,
            IntPtr entropy,
            IntPtr reserved,
            IntPtr prompt,
            int flags,
            out DataBlob output);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static byte[] Protect(byte[] clear)
        {
            ArgumentNullException.ThrowIfNull(clear);
            if (clear.Length == 0)
                throw new ArgumentException("The protected session cannot be empty.", nameof(clear));

            var inputPointer = Marshal.AllocHGlobal(clear.Length);
            try
            {
                Marshal.Copy(clear, 0, inputPointer, clear.Length);
                var input = new DataBlob { Length = clear.Length, Data = inputPointer };
                if (!CryptProtectData(
                    ref input,
                    "LocaLoca desktop session",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    out var output))
                {
                    throw new CryptographicException(Marshal.GetLastWin32Error());
                }

                try
                {
                    var encrypted = new byte[output.Length];
                    Marshal.Copy(output.Data, encrypted, 0, output.Length);
                    return encrypted;
                }
                finally
                {
                    if (output.Data != IntPtr.Zero)
                        LocalFree(output.Data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inputPointer);
            }
        }

        public static byte[] Unprotect(byte[] encrypted)
        {
            ArgumentNullException.ThrowIfNull(encrypted);
            if (encrypted.Length == 0)
                throw new CryptographicException("The saved Windows session is empty.");

            var inputPointer = Marshal.AllocHGlobal(encrypted.Length);
            try
            {
                Marshal.Copy(encrypted, 0, inputPointer, encrypted.Length);
                var input = new DataBlob { Length = encrypted.Length, Data = inputPointer };
                if (!CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    out var output))
                {
                    throw new CryptographicException(Marshal.GetLastWin32Error());
                }

                try
                {
                    var clear = new byte[output.Length];
                    Marshal.Copy(output.Data, clear, 0, output.Length);
                    return clear;
                }
                finally
                {
                    if (output.Data != IntPtr.Zero)
                        LocalFree(output.Data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inputPointer);
            }
        }
    }
}
