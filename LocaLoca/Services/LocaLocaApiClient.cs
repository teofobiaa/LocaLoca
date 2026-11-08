using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LocaLoca.Models;
using Windows.Storage;

namespace LocaLoca.Services;

public sealed class LocaLocaApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly bool _useRemoteApi;
    private Process? _apiProcess;
    private string? _token;
    private bool _ownsApiProcess;

    public LocaLocaApiClient()
    {
        var configuredBaseUrl = Environment.GetEnvironmentVariable("LOCA_API_BASE_URL");
        if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var remoteUri) ||
                remoteUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("LOCA_API_BASE_URL должен содержать корректный HTTPS-адрес облачного API.");

            _http = new HttpClient { BaseAddress = new Uri(remoteUri.ToString().TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(90) };
            _useRemoteApi = true;
        }
        else
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri("http://127.0.0.1:5181/"),
                Timeout = TimeSpan.FromSeconds(60)
            };
        }
    }

    public string? Token => _token;

    public async Task<HealthDto> EnsureApiAndDatabaseAsync(CancellationToken cancellationToken = default)
    {
        // Check local process liveness separately: a slow /ready response must
        // never make the client start a second API process on the same port.
        var localApiIsResponding = !_useRemoteApi && await TryLivenessAsync(cancellationToken);
        HealthDto? firstHealth = await TryReadinessAsync(cancellationToken);
        if (firstHealth is { ApiConfigured: true, DatabaseConnected: true })
            return firstHealth;

        // A configured cloud endpoint is authoritative: never silently fall
        // back to starting a local API when the remote service is unavailable.
        if (_useRemoteApi)
            throw new InvalidOperationException(firstHealth?.Message ??
                "Облачный API недоступен. Проверь интернет и адрес LOCA_API_BASE_URL.");

        // If something already answers on localhost, don't start a second API process.
        if (firstHealth is not null || localApiIsResponding)
        {
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(500, cancellationToken);
                var current = await TryReadinessAsync(cancellationToken);
                if (current is { ApiConfigured: true, DatabaseConnected: true }) return current;
                if (current is not null) firstHealth = current;
            }
            throw new InvalidOperationException(firstHealth?.Message ??
                "Локальный API отвечает на /health, но /ready не вернул корректный ответ. Проверь подключение к Supabase.");
        }

        var apiPath = Path.Combine(AppContext.BaseDirectory, "Server", "LocaLoca.Api.exe");
        if (!File.Exists(apiPath))
        {
            throw new FileNotFoundException(
                "Не найден локальный сервер LocaLoca.Api.exe. Сначала выполни tools\\Publish-LocalApi.ps1 в папке проекта.", apiPath);
        }

        var info = new ProcessStartInfo
        {
            FileName = apiPath,
            WorkingDirectory = Path.GetDirectoryName(apiPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        _apiProcess = Process.Start(info) ?? throw new InvalidOperationException("Windows не смогла запустить локальный API.");
        _ownsApiProcess = true;

        string lastMessage = "Локальный API запускается…";
        for (var i = 0; i < 50; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(400, cancellationToken);

            if (_apiProcess.HasExited)
                throw new InvalidOperationException($"Локальный API завершился с кодом {_apiProcess.ExitCode}. Проверь облачную конфигурацию Supabase в %LOCALAPPDATA%\\LocaLoca\\server-config.json.");

            var health = await TryReadinessAsync(cancellationToken);
            if (health is { ApiConfigured: true, DatabaseConnected: true }) return health;
            if (health is not null) lastMessage = health.Message;
        }

        throw new TimeoutException($"Не удалось подключить локальный API к облачной PostgreSQL за 20 секунд. {lastMessage}");
    }

    public async Task<AuthResponse> LoginAsync(string login, string password, CancellationToken cancellationToken = default)
    {
        var result = await PostJsonAsync<LoginRequest, AuthResponse>("api/auth/login",
            new LoginRequest(login, password), authenticated: false, cancellationToken);
        _token = result.Token;
        return result;
    }

    public async Task<AuthResponse> RegisterAsync(string login, string password, string displayName, CancellationToken cancellationToken = default)
    {
        var result = await PostJsonAsync<RegisterRequest, AuthResponse>("api/auth/register",
            new RegisterRequest(login, password, displayName), authenticated: false, cancellationToken);
        _token = result.Token;
        return result;
    }

    public async Task<UserDto> UpdateDisplayNameAsync(string displayName, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "api/me/profile/display-name")
        {
            Content = JsonContent.Create(new UpdateDisplayNameRequest(displayName), options: JsonOptions)
        };
        AddAuthorization(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<UserDto>(JsonOptions, cancellationToken))
            ?? throw new InvalidOperationException("Сервер вернул пустой профиль.");
    }

    public async Task<List<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync<ItemsResponse<CategoryDto>>("api/catalog/categories", cancellationToken);
        return result.Items;
    }

    public async Task<List<AttractionDto>> GetCatalogAsync(string? search = null, int? categoryId = null, CancellationToken cancellationToken = default)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(search)) parts.Add("search=" + Uri.EscapeDataString(search.Trim()));
        if (categoryId.HasValue) parts.Add("categoryId=" + categoryId.Value.ToString(CultureInfo.InvariantCulture));
        var path = "api/catalog" + (parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts));
        var result = await GetJsonAsync<ItemsResponse<AttractionDto>>(path, cancellationToken);
        return result.Items;
    }

    public async Task<List<VisitDto>> GetVisitsAsync(CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync<ItemsResponse<VisitDto>>("api/me/visits", cancellationToken);
        return result.Items;
    }

    public async Task<List<int>> GetWishlistAsync(CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync<IntItemsResponse>("api/me/wishlist", cancellationToken);
        return result.Items;
    }

    public Task<SummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync<SummaryDto>("api/me/summary", cancellationToken);

    public async Task AddToWishlistAsync(int attractionId, CancellationToken cancellationToken = default)
    {
        await SendForMessageAsync(HttpMethod.Post, $"api/me/wishlist/{attractionId}", null, cancellationToken);
    }

    public async Task RemoveFromWishlistAsync(int attractionId, CancellationToken cancellationToken = default)
    {
        await SendForMessageAsync(HttpMethod.Delete, $"api/me/wishlist/{attractionId}", null, cancellationToken);
    }

    public async Task<VisitSavedDto> SaveVisitAsync(
        int attractionId,
        int? visitId,
        DateTime visitDate,
        int rating,
        string impressions,
        string notes,
        IReadOnlyList<StorageFile> photos,
        CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(attractionId.ToString(CultureInfo.InvariantCulture)), "attractionId");
        form.Add(new StringContent(visitDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "visitDate");
        form.Add(new StringContent(rating.ToString(CultureInfo.InvariantCulture)), "rating");
        form.Add(new StringContent(impressions ?? string.Empty), "impressions");
        form.Add(new StringContent(notes ?? string.Empty), "notes");
        if (visitId.HasValue) form.Add(new StringContent(visitId.Value.ToString(CultureInfo.InvariantCulture)), "visitId");

        foreach (var file in photos)
        {
            var stream = await file.OpenStreamForReadAsync();
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(file.FileType));
            form.Add(fileContent, "photos", file.Name);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/me/visits/save") { Content = form };
        AddAuthorization(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<VisitSavedDto>(JsonOptions, cancellationToken))
            ?? throw new InvalidOperationException("Сервер вернул пустой ответ при сохранении посещения.");
    }

    public async Task DeleteVisitAsync(int visitId, CancellationToken cancellationToken = default) =>
        await SendForMessageAsync(HttpMethod.Delete, $"api/me/visits/{visitId}", null, cancellationToken);

    public async Task UpdateMapPlaceVisitAsync(int placeId, string name, DateTime visitDate, int rating,
        string impressions, string notes, CancellationToken cancellationToken = default)
    {
        var body = new MapPlaceUpdateRequest(name, "visited", visitDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            rating, impressions, notes);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/me/map-places/{placeId}")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        AddAuthorization(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DeleteMapPlaceAsync(int placeId, CancellationToken cancellationToken = default) =>
        await SendForMessageAsync(HttpMethod.Delete, $"api/me/map-places/{placeId}", null, cancellationToken);

    public async Task UploadMapPlacePhotosAsync(int placeId, IReadOnlyList<StorageFile> photos,
        CancellationToken cancellationToken = default)
    {
        if (photos.Count == 0) return;
        using var form = new MultipartFormDataContent();
        foreach (var file in photos)
        {
            var fileContent = new StreamContent(await file.OpenStreamForReadAsync());
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(file.FileType));
            form.Add(fileContent, "photos", file.Name);
        }
        await SendForMessageAsync(HttpMethod.Post, $"api/me/map-places/{placeId}/photos", form, cancellationToken);
    }

    public async Task<List<PhotoDto>> GetPhotosAsync(int visitId, CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync<ItemsResponse<PhotoDto>>($"api/me/visits/{visitId}/photos", cancellationToken);
        return result.Items;
    }

    public async Task<List<PhotoDto>> GetMapPlacePhotosAsync(int placeId, CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync<ItemsResponse<PhotoDto>>($"api/me/map-places/{placeId}/photos", cancellationToken);
        return result.Items;
    }

    public async Task<byte[]> GetPhotoBytesAsync(int photoId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/me/photos/{photoId}");
        AddAuthorization(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<byte[]> GetMapPlacePhotoBytesAsync(int photoId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/me/map-place-photos/{photoId}");
        AddAuthorization(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task DeletePhotoAsync(int photoId, CancellationToken cancellationToken = default) =>
        await SendForMessageAsync(HttpMethod.Delete, $"api/me/photos/{photoId}", null, cancellationToken);

    public async Task DeleteMapPlacePhotoAsync(int photoId, CancellationToken cancellationToken = default) =>
        await SendForMessageAsync(HttpMethod.Delete, $"api/me/map-place-photos/{photoId}", null, cancellationToken);

    public Uri GetMapUri(bool startNewVisit = false)
    {
        if (string.IsNullOrWhiteSpace(_token)) throw new InvalidOperationException("Сначала выполни вход в аккаунт.");
        var mode = startNewVisit ? "&mode=visit" : string.Empty;
        return new Uri(_http.BaseAddress!, $"map#token={Uri.EscapeDataString(_token)}{mode}");
    }

    public void Logout() => _token = null;

    private async Task<bool> TryLivenessAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var shortTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shortTimeout.CancelAfter(TimeSpan.FromSeconds(1.5));
            using var response = await _http.GetAsync("health", shortTimeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private async Task<HealthDto?> TryReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var shortTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shortTimeout.CancelAfter(_useRemoteApi ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(10));
            using var response = await _http.GetAsync("ready", shortTimeout.Token);
            return await response.Content.ReadFromJsonAsync<HealthDto>(JsonOptions, shortTimeout.Token)
                   ?? new HealthDto(false, false, "API вернул пустой ответ.");
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        AddAuthorization(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken))
               ?? throw new InvalidOperationException("Сервер вернул пустой ответ.");
    }

    private async Task<TResponse> PostJsonAsync<TRequest, TResponse>(string path, TRequest body, bool authenticated, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: JsonOptions) };
        if (authenticated) AddAuthorization(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken))
               ?? throw new InvalidOperationException("Сервер вернул пустой ответ.");
    }

    private async Task SendForMessageAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        AddAuthorization(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private void AddAuthorization(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string message = body;
        try
        {
            var error = JsonSerializer.Deserialize<ErrorDto>(body, JsonOptions);
            if (!string.IsNullOrWhiteSpace(error?.Error)) message = error.Error;
        }
        catch (JsonException) { }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Неверный логин или пароль либо истекла сессия.");
        if (string.IsNullOrWhiteSpace(message)) message = $"Ошибка API: {(int)response.StatusCode} {response.ReasonPhrase}";
        throw new InvalidOperationException(message);
    }

    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", ".bmp" => "image/bmp", _ => "image/jpeg"
    };

    public void Dispose()
    {
        _http.Dispose();
        if (_ownsApiProcess && _apiProcess is { HasExited: false })
        {
            try { _apiProcess.Kill(entireProcessTree: true); }
            catch { /* Завершение окна не должно падать из-за уже закрытого процесса API. */ }
        }
        _apiProcess?.Dispose();
    }

    private sealed record LoginRequest(string Login, string Password);
    private sealed record RegisterRequest(string Login, string Password, string DisplayName);
}
