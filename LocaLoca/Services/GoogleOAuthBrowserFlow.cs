using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.System;

namespace LocaLoca.Services;

/// <summary>
/// Runs the installed-app OAuth authorization-code flow in the system browser.
/// The desktop app is a public client: it uses PKCE and never embeds a client secret.
/// </summary>
internal static class GoogleOAuthBrowserFlow
{
    public static async Task<string> AcquireIdTokenAsync(
        HttpClient apiHttp,
        string clientId,
        CancellationToken cancellationToken)
    {
        using var challengeRequest = new HttpRequestMessage(HttpMethod.Post, "api/auth/google/challenge")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        using var challengeResponse = await apiHttp.SendAsync(challengeRequest, cancellationToken);
        await EnsureSuccessAsync(challengeResponse, cancellationToken);
        using var challengeJson = JsonDocument.Parse(
            await challengeResponse.Content.ReadAsStringAsync(cancellationToken));
        var nonce = challengeJson.RootElement.GetProperty("nonce").GetString();
        if (string.IsNullOrWhiteSpace(nonce))
            throw new InvalidOperationException("Сервер не выдал nonce для Google OAuth.");

        // TcpListener avoids HttpListener/HTTP.sys URL ACL requirements on Windows.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}/";

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var authorizationUri = "https://accounts.google.com/o/oauth2/v2/auth?" +
            "client_id=" + Uri.EscapeDataString(clientId) +
            "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
            "&response_type=code" +
            "&scope=" + Uri.EscapeDataString("openid email profile") +
            "&code_challenge=" + Uri.EscapeDataString(codeChallenge) +
            "&code_challenge_method=S256" +
            "&nonce=" + Uri.EscapeDataString(nonce) +
            "&state=" + Uri.EscapeDataString(state);

        try
        {
            var launched = await Launcher.LaunchUriAsync(new Uri(authorizationUri));
            if (!launched)
                throw new InvalidOperationException("Не удалось открыть системный браузер для входа через Google.");

            using var callbackClient = await listener.AcceptTcpClientAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
            var remote = (IPEndPoint?)callbackClient.Client.RemoteEndPoint;
            if (remote is null || !IPAddress.IsLoopback(remote.Address))
                throw new InvalidOperationException("Получен OAuth callback не с loopback-интерфейса.");

            var stream = callbackClient.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken);
            string? headerLine;
            do
            {
                headerLine = await reader.ReadLineAsync(cancellationToken);
            } while (!string.IsNullOrEmpty(headerLine));

            if (string.IsNullOrWhiteSpace(requestLine))
                throw new InvalidOperationException("Получен пустой OAuth callback.");

            var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestParts.Length < 2 || requestParts[0] != "GET" ||
                !Uri.TryCreate(new Uri("http://127.0.0.1"), requestParts[1], out var callbackUri))
                throw new InvalidOperationException("Получен некорректный OAuth callback.");

            var query = ParseQuery(callbackUri.Query);
            var callbackState = query.GetValueOrDefault("state");
            if (string.IsNullOrEmpty(callbackState) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(callbackState), Encoding.UTF8.GetBytes(state)))
            {
                await WriteCallbackPageAsync(stream, "Не удалось подтвердить запрос. Закрой эту вкладку.");
                throw new InvalidOperationException("Проверка OAuth state не пройдена.");
            }

            var error = query.GetValueOrDefault("error");
            if (!string.IsNullOrWhiteSpace(error))
            {
                await WriteCallbackPageAsync(stream, "Вход через Google отменён. Можно закрыть вкладку.");
                throw new InvalidOperationException("Google не завершил авторизацию: " + error);
            }

            var code = query.GetValueOrDefault("code");
            if (string.IsNullOrWhiteSpace(code))
            {
                await WriteCallbackPageAsync(stream, "Google не вернул код авторизации. Закрой эту вкладку.");
                throw new InvalidOperationException("Google не вернул authorization code.");
            }

            await WriteCallbackPageAsync(stream, "Вход выполнен. Вернись в Loca Loca.");

            using var tokenHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var tokenResponse = await tokenHttp.PostAsync(
                "https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["code"] = code,
                    ["code_verifier"] = verifier,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = redirectUri
                }),
                cancellationToken);
            var tokenBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                var errorCode = "unknown";
                string? errorDescription = null;

                try
                {
                    using var errorJson = JsonDocument.Parse(tokenBody);
                    if (errorJson.RootElement.TryGetProperty("error", out var errorElement) &&
                        errorElement.ValueKind == JsonValueKind.String)
                    {
                        errorCode = errorElement.GetString() ?? errorCode;
                    }

                    if (errorJson.RootElement.TryGetProperty("error_description", out var descriptionElement) &&
                        descriptionElement.ValueKind == JsonValueKind.String)
                    {
                        errorDescription = descriptionElement.GetString();
                    }
                }
                catch (JsonException)
                {
                    // Google may return a non-JSON error response; retain a bounded diagnostic.
                    errorDescription = string.IsNullOrWhiteSpace(tokenBody)
                        ? null
                        : tokenBody[..Math.Min(tokenBody.Length, 1000)];
                }

                var details = string.IsNullOrWhiteSpace(errorDescription)
                    ? string.Empty
                    : $" Описание: {errorDescription}";

                throw new InvalidOperationException(
                    $"Google отклонил обмен authorization code на токен. " +
                    $"HTTP {(int)tokenResponse.StatusCode}; код: {errorCode}.{details}");
            }

            using var tokenJson = JsonDocument.Parse(tokenBody);
            if (!tokenJson.RootElement.TryGetProperty("id_token", out var idTokenElement) ||
                idTokenElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(idTokenElement.GetString()))
                throw new InvalidOperationException("Google не вернул ID Token.");

            return idTokenElement.GetString()!;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Время ожидания ответа Google истекло. Повтори вход.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf('=');
            var key = separator < 0 ? item : item[..separator];
            var value = separator < 0 ? string.Empty : item[(separator + 1)..];
            values[WebUtility.UrlDecode(key)] = WebUtility.UrlDecode(value);
        }
        return values;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(body)
                ? $"Ошибка API при подготовке Google OAuth: {(int)response.StatusCode}."
                : $"Не удалось подготовить Google OAuth: {body}");
    }

    private static async Task WriteCallbackPageAsync(NetworkStream stream, string message)
    {
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Loca Loca</title></head>" +
            "<body style=\"font:16px Segoe UI,sans-serif;max-width:560px;margin:12vh auto;padding:24px\">" +
            "<h2>Loca Loca</h2><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " +
            bytes.Length + "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
