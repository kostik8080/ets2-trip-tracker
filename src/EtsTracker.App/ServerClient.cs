using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace EtsTracker.App;

/// <summary>Вход на сервер компании конкретного водителя трекера.</summary>
public sealed class ServerAccount
{
    public string Nickname { get; set; } = "";
    public string Token { get; set; } = "";
    public bool IsAdmin { get; set; }
}

/// <summary>Адрес сервера и входы водителей. Файл data/server.json.</summary>
public sealed class ServerSettings
{
    public string Url { get; set; } = "http://127.0.0.1:8080";
    /// <summary>Вход на сервер для каждого водителя этого трекера (ключ — его номер в локальной базе).</summary>
    public Dictionary<long, ServerAccount> Accounts { get; set; } = new();
}

public sealed class ServerException : Exception
{
    public bool Unauthorized { get; }

    public ServerException(string message, bool unauthorized = false) : base(message) => Unauthorized = unauthorized;
}

/// <summary>Обращения трекера к серверу компании.</summary>
public sealed class ServerClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Uri _baseUrl;
    private readonly string? _token;

    public ServerClient(string url, string? token = null)
    {
        _baseUrl = ParseUrl(url);
        _token = token;
    }

    /// <summary>
    /// Проверяет адрес. Без HTTPS пароль уходит открытым текстом, поэтому http разрешён только
    /// для сервера на этом же компьютере.
    /// </summary>
    public static Uri ParseUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new ServerException("Адрес сервера должен начинаться с http:// или https://.");
        if (uri.Scheme == "http" && !uri.IsLoopback)
            throw new ServerException("Для сервера в интернете нужен адрес с https://.");
        return uri;
    }

    public Task<AuthResponse> RegisterAsync(RegisterRequest req) => PostAsync<AuthResponse>("api/auth/register", req);

    public Task<AuthResponse> LoginAsync(LoginRequest req) => PostAsync<AuthResponse>("api/auth/login", req);

    public Task<TripUploadResult> UploadAsync(List<TripUpload> trips) => PostAsync<TripUploadResult>("api/trips", trips);

    public Task<InviteResponse> CreateInviteAsync(DriverRank rank)
        => PostAsync<InviteResponse>("api/invites", new InviteRequest(rank));

    public Task<Leaderboard> GetLeaderboardAsync(string period, string metric)
        => SendAsync<Leaderboard>(new HttpRequestMessage(HttpMethod.Get, $"api/leaderboard?period={period}&metric={metric}"));

    public Task<ServerDriver> GetMeAsync() => SendAsync<ServerDriver>(new HttpRequestMessage(HttpMethod.Get, "api/me"));

    /// <summary>Закрывает вход на сервере. Если сервер недоступен, токен просто забудется на этом компьютере.</summary>
    public async Task LogoutAsync()
    {
        try { await SendRawAsync(new HttpRequestMessage(HttpMethod.Post, "api/auth/logout")); }
        catch (ServerException) { }
    }

    private Task<T> PostAsync<T>(string path, object body)
        => SendAsync<T>(new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: ApiJson.Options) });

    private async Task<T> SendAsync<T>(HttpRequestMessage request)
    {
        using var response = await SendRawAsync(request);
        return await response.Content.ReadFromJsonAsync<T>(ApiJson.Options)
               ?? throw new ServerException("Сервер прислал пустой ответ.");
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request)
    {
        request.RequestUri = new Uri(_baseUrl, request.RequestUri!);
        if (_token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ServerException("Сервер недоступен. Проверьте адрес и подключение.");
        }
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            string message = response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "Слишком много попыток. Подождите минуту.",
                _ => $"Ошибка сервера ({(int)response.StatusCode}).",
            };
            try
            {
                if (await response.Content.ReadFromJsonAsync<ApiError>(ApiJson.Options) is { Error: { Length: > 0 } text })
                    message = text;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException) { }
            throw new ServerException(message, response.StatusCode == HttpStatusCode.Unauthorized);
        }
    }
}
