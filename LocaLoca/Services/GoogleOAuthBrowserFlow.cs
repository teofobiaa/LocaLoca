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

        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();

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

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var launched = await Launcher.LaunchUriAsync(new Uri(authorizationUri));
        if (!launched)
            throw new InvalidOperationException("Не удалось открыть системный браузер для входа через Google.");

        HttpListenerContext callback;
        try
        {
            callback = await listener.GetContextAsync().WaitAsync(
                TimeSpan.FromMinutes(3), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Время ожидания ответа Google истекло. Повтори вход.");
        }

        using (callback)
        {
            if (callback.Request.RemoteEndPoint is null ||
                !IPAddress.IsLoopback(callback.Request.RemoteEndPoint.Address))
                throw new InvalidOperationException("Получен OAuth callback не с loopback-интерфейса.");

            var callbackState = callback.Request.QueryString["state"];
            if (string.IsNullOrEmpty(callbackState) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(callbackState), Encoding.UTF8.GetBytes(state)))
            {
                await WriteCallbackPageAsync(callback, "Не удалось подтвердить запрос. Закрой эту вкладку.");
                throw new InvalidOperationException("Проверка OAuth state не пройдена.");
            }

            var error = callback.Request.QueryString["error"];
            if (!string.IsNullOrWhiteSpace(error))
            {
                await WriteCallbackPageAsync(callback, "Вход через Google отменён. Можно закрыть вкладку.");
                throw new InvalidOperationException("Google не завершил авторизацию: " + error);
            }

            var code = callback.Request.QueryString["code"];
            if (string.IsNullOrWhiteSpace(code))
            {
                await WriteCallbackPageAsync(callback, "Google не вернул код авторизации. Закрой эту вкладку.");
                throw new InvalidOperationException("Google не вернул authorization code.");
            }

            await WriteCallbackPageAsync(callback, "Вход выполнен. Вернись в Loca Loca.");

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
                throw new InvalidOperationException("Google отклонил обмен authorization code на токен.");

            using var tokenJson = JsonDocument.Parse(tokenBody);
            if (!tokenJson.RootElement.TryGetProperty("id_token", out var idTokenElement) ||
                idTokenElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(idTokenElement.GetString()))
                throw new InvalidOperationException("Google не вернул ID Token.");

            return idTokenElement.GetString()!;
        }
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

    private static async Task WriteCallbackPageAsync(HttpListenerContext context, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Loca Loca</title></head>" +
            "<body style=\"font:16px Segoe UI,sans-serif;max-width:560px;margin:12vh auto;padding:24px\">" +
            "<h2>Loca Loca</h2><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>");
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
