using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using HoshinoTransfer.Windows.Models;

namespace HoshinoTransfer.Windows.Services;

public sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly ICredentialVault _vault;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private DateTimeOffset _accessExpiresAt;

    public Uri BaseAddress { get; }
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public string? DeviceId { get; private set; }
    public bool HasSession => !string.IsNullOrWhiteSpace(RefreshToken);

    public ApiClient(ICredentialVault? vault = null)
    {
        _vault = vault ?? new CredentialVault();
        RestoreSavedCredentials();
        var endpoint = "https://rt2ucj.taild7fb6f.ts.net/";
        var developmentOverride = Environment.GetEnvironmentVariable("HOSHINOTRANSFER_API_URL");
        if (!string.IsNullOrWhiteSpace(developmentOverride)) endpoint = developmentOverride;
        if (!endpoint.EndsWith('/')) endpoint += "/";
        BaseAddress = new Uri(endpoint, UriKind.Absolute);
        _http.BaseAddress = BaseAddress;
    }

    public async Task<AuthResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/auth/login", new { username, password }, JsonOptions, cancellationToken);
        return await ReadAuthAsync(response, cancellationToken);
    }

    public async Task<AuthResult> RegisterAsync(string username, string displayName, string password, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/auth/register", new { username, displayName, password }, JsonOptions, cancellationToken);
        return await ReadAuthAsync(response, cancellationToken);
    }

    public async Task RefreshSessionAsync(CancellationToken cancellationToken = default)
    {
        var usedRefreshToken = RefreshToken;
        if (string.IsNullOrEmpty(usedRefreshToken)) throw new ApiException("No refresh credential is available.", HttpStatusCode.Unauthorized);
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.Equals(usedRefreshToken, RefreshToken, StringComparison.Ordinal)) return;
            using var response = await _http.PostAsJsonAsync("api/v1/auth/refresh", new { refreshToken = usedRefreshToken }, JsonOptions, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                ClearSession();
                throw await ReadApiExceptionAsync(response, cancellationToken);
            }
            var envelope = await response.Content.ReadFromJsonAsync<SessionOnlyEnvelope>(JsonOptions, cancellationToken)
                           ?? throw new ApiException("The service returned an empty refresh response.", response.StatusCode);
            ApplySession(envelope.Session);
        }
        finally { _refreshGate.Release(); }
    }

    public async Task<AuthResult?> RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(AccessToken) && string.IsNullOrWhiteSpace(RefreshToken)) return null;
        try
        {
            if (_accessExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1)) await RefreshSessionAsync(cancellationToken);
            var envelope = await GetJsonAsync<CurrentUserEnvelope>("api/v1/users/me", cancellationToken);
            return new AuthResult(envelope.User.Id, envelope.User.Username, envelope.User.DisplayName);
        }
        catch (ApiException ex)
        {
            if (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) ClearSession();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            ClearSession();
            return null;
        }
    }

    private void RestoreSavedCredentials()
    {
        try
        {
            var json = _vault.Read();
            if (string.IsNullOrWhiteSpace(json)) return;
            var saved = JsonSerializer.Deserialize<SavedClientSession>(json, JsonOptions);
            if (saved is null) return;
            AccessToken = saved.AccessToken;
            RefreshToken = saved.RefreshToken;
            DeviceId = saved.DeviceId;
            _accessExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(saved.AccessExpiresAt);
        }
        catch { ClearSession(); }
    }

    private void ApplySession(SessionEnvelope session)
    {
        AccessToken = session.AccessToken;
        RefreshToken = session.RefreshToken;
        _accessExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(session.AccessExpiresAt);
        PersistSession();
    }

    private void PersistSession()
    {
        if (string.IsNullOrWhiteSpace(RefreshToken)) return;
        try { _vault.Write(JsonSerializer.Serialize(new SavedClientSession(AccessToken, RefreshToken, DeviceId, _accessExpiresAt.ToUnixTimeMilliseconds()), JsonOptions)); }
        catch { }
    }

    private async Task EnsureFreshSessionAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(RefreshToken) && _accessExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            await RefreshSessionAsync(cancellationToken);
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/health", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> PrivateApiIsProtectedAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/users/me");
        using var response = await _http.SendAsync(request, cancellationToken);
        return response.StatusCode == HttpStatusCode.Unauthorized;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (AccessToken is not null)
            {
                using var response = await SendAuthorizedAsync(HttpMethod.Post, "api/v1/auth/logout", new { }, cancellationToken);
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Unauthorized)
                    throw await ReadApiExceptionAsync(response, cancellationToken);
            }
        }
        finally { ClearSession(); }
    }

    public void ClearSession()
    {
        AccessToken = null; RefreshToken = null; DeviceId = null; _accessExpiresAt = DateTimeOffset.MinValue;
        try { _vault.Delete(); } catch { }
    }

    public Task<FriendsResponse> GetFriendsAsync(CancellationToken ct = default) => GetJsonAsync<FriendsResponse>("api/v1/friends", ct);
    public Task<DevicesResponse> GetDevicesAsync(CancellationToken ct = default) => GetJsonAsync<DevicesResponse>("api/v1/devices", ct);
    public Task<ChatsResponse> GetChatsAsync(CancellationToken ct = default) => GetJsonAsync<ChatsResponse>("api/v1/chats", ct);
    public Task<TransfersResponse> GetTransfersAsync(CancellationToken ct = default) => GetJsonAsync<TransfersResponse>("api/v1/transfers", ct);

    public async Task<IReadOnlyList<UserDto>> SearchUsersAsync(string query, CancellationToken ct = default)
        => (await GetJsonAsync<UsersResponse>($"api/v1/users/search?q={Uri.EscapeDataString(query)}", ct)).Users;

    public async Task<UserDto> UpdateDisplayNameAsync(string displayName, CancellationToken ct = default)
        => (await SendJsonAsync<object, CurrentUserEnvelope>(HttpMethod.Patch, "api/v1/users/me", new { displayName }, ct)).User.ToDto();

    public async Task RequestFriendAsync(string username, CancellationToken ct = default)
        => _ = await SendJsonAsync<object, JsonElement>(HttpMethod.Post, "api/v1/friends/request", new { username }, ct);

    public async Task RespondFriendAsync(string userId, bool accept, CancellationToken ct = default)
        => _ = await SendJsonAsync<object, JsonElement>(HttpMethod.Post, $"api/v1/friends/{(accept ? "accept" : "reject")}", new { userId }, ct);

    public async Task RemoveFriendAsync(string userId, CancellationToken ct = default)
        => _ = await SendJsonAsync<object, JsonElement>(HttpMethod.Delete, $"api/v1/friends/{Uri.EscapeDataString(userId)}", null, ct);

    public async Task BlockFriendAsync(string userId, CancellationToken ct = default)
        => _ = await SendJsonAsync<object, JsonElement>(HttpMethod.Post, "api/v1/friends/block", new { userId }, ct);

    public async Task UnblockFriendAsync(string userId, CancellationToken ct = default)
        => _ = await SendJsonAsync<object, JsonElement>(HttpMethod.Post, "api/v1/friends/unblock", new { userId }, ct);

    public async Task<DeviceDto> RegisterDeviceAsync(string deviceName, string platform, CancellationToken ct = default)
    {
        var device = (await SendJsonAsync<object, DeviceEnvelope>(HttpMethod.Post, "api/v1/devices", new { deviceName, platform }, ct)).Device;
        DeviceId = device.Id;
        PersistSession();
        return device;
    }

    public async Task<PairingCodeEnvelope> CreatePairingCodeAsync(CancellationToken ct = default)
        => await SendJsonAsync<object, PairingCodeEnvelope>(HttpMethod.Post, "api/v1/devices/pairing", new { }, ct);

    public async Task<DeviceDto> PairDeviceAsync(string code, string deviceName, string platform, CancellationToken ct = default)
    {
        var device = (await SendJsonAsync<object, DeviceEnvelope>(HttpMethod.Post, "api/v1/devices/pair", new { pairingCode = code, deviceName, platform }, ct)).Device;
        DeviceId = device.Id;
        PersistSession();
        return device;
    }

    public async Task RevokeDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        _ = await SendJsonAsync<object, JsonElement>(HttpMethod.Delete, $"api/v1/devices/{Uri.EscapeDataString(deviceId)}", null, ct);
        if (DeviceId == deviceId) { DeviceId = null; PersistSession(); }
    }

    public async Task HeartbeatDeviceAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(DeviceId))
            _ = await SendJsonAsync<object, JsonElement>(HttpMethod.Post, $"api/v1/devices/{Uri.EscapeDataString(DeviceId)}/heartbeat", new { }, ct);
    }

    public async Task<ChatDto> OpenChatAsync(string userId, CancellationToken ct = default)
        => (await SendJsonAsync<object, ChatEnvelope>(HttpMethod.Post, "api/v1/chats", new { userId }, ct)).Chat;

    public Task<MessagesResponse> GetMessagesAsync(string chatId, CancellationToken ct = default)
        => GetJsonAsync<MessagesResponse>($"api/v1/chats/{Uri.EscapeDataString(chatId)}/messages", ct);

    public async Task<MessageDto> SendMessageAsync(string chatId, string content, string? transferId = null, CancellationToken ct = default)
        => (await SendJsonAsync<object, MessageEnvelope>(HttpMethod.Post, $"api/v1/chats/{Uri.EscapeDataString(chatId)}/messages", new { content, transferId }, ct)).Message;

    public async Task SendTypingAsync(string chatId, bool isTyping, CancellationToken ct = default)
        => _ = await SendJsonAsync<object, JsonElement>(HttpMethod.Post, $"api/v1/chats/{Uri.EscapeDataString(chatId)}/typing", new { isTyping }, ct);

    public async Task MarkChatReadAsync(string chatId, CancellationToken ct = default)
        => _ = await SendJsonAsync<object, JsonElement>(HttpMethod.Post, $"api/v1/chats/{Uri.EscapeDataString(chatId)}/read", new { }, ct);

    public async Task<TransferCreateEnvelope> CreateTransferAsync(string receiverId, IReadOnlyList<TransferItemRequest> items, CancellationToken ct = default)
        => await SendJsonAsync<object, TransferCreateEnvelope>(HttpMethod.Post, "api/v1/transfers/create", new { receiverId, items }, ct);

    public async Task<TransferDto> GetTransferAsync(string transferId, CancellationToken ct = default)
        => (await GetJsonAsync<TransferEnvelope>($"api/v1/transfers/{Uri.EscapeDataString(transferId)}", ct)).Transfer;

    public async Task<TransferProgressEnvelope> GetTransferProgressAsync(string transferId, CancellationToken ct = default)
        => await GetJsonAsync<TransferProgressEnvelope>($"api/v1/transfers/{Uri.EscapeDataString(transferId)}/progress", ct);

    public Task<TransferDto> AcceptTransferAsync(string transferId, CancellationToken ct = default) => TransferActionAsync(transferId, "accept", ct);
    public Task<TransferDto> CancelTransferAsync(string transferId, CancellationToken ct = default) => TransferActionAsync(transferId, "cancel", ct);
    public Task<TransferDto> ResumeTransferAsync(string transferId, CancellationToken ct = default) => TransferActionAsync(transferId, "resume", ct);
    public Task<TransferDto> PauseTransferAsync(string transferId, CancellationToken ct = default) => TransferActionAsync(transferId, "pause", ct);
    public Task<TransferDto> DeclineTransferAsync(string transferId, CancellationToken ct = default) => TransferActionAsync(transferId, "decline", ct);
    public Task<TransferDto> CompleteTransferAsync(string transferId, CancellationToken ct = default) => TransferActionAsync(transferId, "complete", ct);

    public async Task<TransferDto> RetryTransferAsync(string transferId, CancellationToken ct = default)
        => (await SendJsonAsync<object, TransferEnvelope>(HttpMethod.Post, $"api/v1/transfers/{Uri.EscapeDataString(transferId)}/retry", new { }, ct)).Transfer;

    public async Task RegisterDirectEndpointAsync(string transferId, string host, int port, string token, CancellationToken ct = default)
        => _ = await SendJsonAsync<object, JsonElement>(HttpMethod.Post, $"api/v1/transfers/{Uri.EscapeDataString(transferId)}/direct", new { host, port, token }, ct);

    public async Task RequestRelayFallbackAsync(string transferId, CancellationToken ct = default)
        => _ = await SendJsonAsync<object, JsonElement>(HttpMethod.Post, $"api/v1/transfers/{Uri.EscapeDataString(transferId)}/fallback-relay", new { }, ct);

    public async Task<TransferDto> CompleteDirectTransferAsync(string transferId, CancellationToken ct = default)
        => (await SendJsonAsync<object, TransferEnvelope>(HttpMethod.Post, $"api/v1/transfers/{Uri.EscapeDataString(transferId)}/complete-direct", new { }, ct)).Transfer;

    public async Task<TransferDto> FailTransferAsync(string transferId, string reason, CancellationToken ct = default)
        => (await SendJsonAsync<object, TransferEnvelope>(HttpMethod.Post, $"api/v1/transfers/{Uri.EscapeDataString(transferId)}/fail", new { reason }, ct)).Transfer;

    public async Task<Stream> DownloadDirectChunkAsync(string url, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw await ReadApiExceptionAsync(response, ct);
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task UploadChunkAsync(string transferId, string itemId, int index, Stream content, long length, CancellationToken ct = default)
    {
        await EnsureFreshSessionAsync(ct);
        using var request = Authorized(HttpMethod.Put, $"api/v1/transfers/{Uri.EscapeDataString(transferId)}/items/{Uri.EscapeDataString(itemId)}/chunks/{index}");
        var body = new StreamContent(content, 64 * 1024);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.ContentLength = length;
        request.Content = body;
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(RefreshToken))
        {
            await RefreshSessionAsync(ct);
            throw new ApiException("Session refreshed. Retry this chunk after reading the server's current chunk list.", HttpStatusCode.Unauthorized);
        }
        if (!response.IsSuccessStatusCode) throw await ReadApiExceptionAsync(response, ct);
    }

    public async Task DownloadItemAsync(string transferId, TransferItemDto item, string destinationPath, CancellationToken ct = default)
    {
        await EnsureFreshSessionAsync(ct);
        HttpResponseMessage response;
        for (var attempt = 0; ; attempt++)
        {
            using var request = Authorized(HttpMethod.Get, $"api/v1/transfers/{Uri.EscapeDataString(transferId)}/items/{Uri.EscapeDataString(item.Id)}/download");
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0 || string.IsNullOrWhiteSpace(RefreshToken)) break;
            response.Dispose();
            await RefreshSessionAsync(ct);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw await ReadApiExceptionAsync(response, ct);
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, 128 * 1024, ct);
        }

        await using var verifyStream = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(verifyStream, ct);
        var actual = Convert.ToHexString(digest).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(item.Sha256)))
        {
            File.Delete(destinationPath);
            throw new ApiException("Downloaded file failed SHA-256 verification and was removed.", HttpStatusCode.UnprocessableEntity);
        }
    }

    public async Task ListenEventsAsync(Action<string, string> onEvent, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !string.IsNullOrEmpty(AccessToken))
        {
            try
            {
                await EnsureFreshSessionAsync(ct);
                using var request = Authorized(HttpMethod.Get, "api/v1/events");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await RefreshSessionAsync(ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Realtime event connection returned {(int)response.StatusCode}.");
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);
                var eventName = "message";
                var data = new List<string>();
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].Trim();
                    else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Add(line[5..].Trim());
                    else if (line.Length == 0 && data.Count > 0)
                    {
                        onEvent(eventName, string.Join("\n", data));
                        eventName = "message";
                        data.Clear();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ApiException) when (!ct.IsCancellationRequested) { ClearSession(); return; }
            catch (HttpRequestException) when (!ct.IsCancellationRequested) { }
            catch (IOException) when (!ct.IsCancellationRequested) { }
            if (!ct.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task<TransferDto> TransferActionAsync(string transferId, string action, CancellationToken ct)
        => (await SendJsonAsync<object, TransferEnvelope>(HttpMethod.Post, $"api/v1/transfers/{Uri.EscapeDataString(transferId)}/{action}", new { }, ct)).Transfer;

    private async Task<T> GetJsonAsync<T>(string route, CancellationToken ct)
    {
        await EnsureFreshSessionAsync(ct);
        using var response = await SendAuthorizedAsync(HttpMethod.Get, route, null, ct);
        if (!response.IsSuccessStatusCode) throw await ReadApiExceptionAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct) ?? throw new ApiException("The service returned an empty response.", response.StatusCode);
    }

    private async Task<TResponse> SendJsonAsync<TRequest, TResponse>(HttpMethod method, string route, TRequest? body, CancellationToken ct)
    {
        await EnsureFreshSessionAsync(ct);
        using var response = await SendAuthorizedAsync(method, route, body, ct);
        if (!response.IsSuccessStatusCode) throw await ReadApiExceptionAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, ct) ?? throw new ApiException("The service returned an empty response.", response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string route, object? body, CancellationToken ct)
    {
        var request = Authorized(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        var response = await _http.SendAsync(request, ct);
        request.Dispose();
        if (response.StatusCode != HttpStatusCode.Unauthorized || string.IsNullOrWhiteSpace(RefreshToken)) return response;
        response.Dispose();
        await RefreshSessionAsync(ct);
        var retry = Authorized(method, route);
        if (body is not null) retry.Content = JsonContent.Create(body, options: JsonOptions);
        try { return await _http.SendAsync(retry, ct); }
        finally { retry.Dispose(); }
    }

    private HttpRequestMessage Authorized(HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, route);
        if (!string.IsNullOrWhiteSpace(AccessToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        if (!string.IsNullOrWhiteSpace(DeviceId)) request.Headers.TryAddWithoutValidation("X-Device-Id", DeviceId);
        return request;
    }

    private async Task<ApiException> ReadApiExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var message = $"Service request failed ({(int)response.StatusCode}).";
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>(JsonOptions, ct);
            if (!string.IsNullOrWhiteSpace(error?.Error?.Message)) message = error.Error.Message;
        }
        catch (JsonException) { }
        return new ApiException(message, response.StatusCode);
    }

    private async Task<AuthResult> ReadAuthAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) throw await ReadApiExceptionAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<AuthEnvelope>(JsonOptions, cancellationToken)
                     ?? throw new ApiException("The service returned an empty authentication response.", response.StatusCode);
        if (string.IsNullOrWhiteSpace(result.Session.AccessToken)) throw new ApiException("The service did not return an access token.", response.StatusCode);
        ApplySession(result.Session);
        return new AuthResult(result.User.Id, result.User.Username, result.User.DisplayName);
    }

    public void Dispose()
    {
        _refreshGate.Dispose();
        _http.Dispose();
    }
}

public sealed record TransferItemRequest(string FileName, long Size, string MimeType, string Sha256);
public sealed record AuthResult(string Id, string Username, string DisplayName);
public sealed record SavedClientSession(string? AccessToken, string? RefreshToken, string? DeviceId, long AccessExpiresAt);
public sealed class AuthEnvelope { public UserEnvelope User { get; set; } = new(); public SessionEnvelope Session { get; set; } = new(); }
public sealed class SessionOnlyEnvelope { public SessionEnvelope Session { get; set; } = new(); }
public sealed class CurrentUserEnvelope { public UserEnvelope User { get; set; } = new(); }
public sealed class UsersResponse { public List<UserDto> Users { get; set; } = []; }
public sealed class PairingCodeEnvelope { public string PairingId { get; set; } = ""; public string PairingCode { get; set; } = ""; public string ExpiresAt { get; set; } = ""; public int ExpiresInSeconds { get; set; } }
public sealed class TransferProgressEnvelope
{
    public string TransferId { get; set; } = ""; public string Status { get; set; } = ""; public string Transport { get; set; } = ""; public int ChunkSize { get; set; }
    public long TotalBytes { get; set; } public long ReceivedBytes { get; set; } public List<TransferProgressItem> Items { get; set; } = [];
}
public sealed class TransferProgressItem
{
    public string ItemId { get; set; } = ""; public string FileName { get; set; } = ""; public long Size { get; set; } public long ReceivedBytes { get; set; }
    public int NextChunkIndex { get; set; } public int ChunkCount { get; set; } public List<int> ReceivedChunks { get; set; } = [];
}
public sealed class UserEnvelope
{
    public string Id { get; set; } = ""; public string Username { get; set; } = ""; public string DisplayName { get; set; } = "";
    public UserDto ToDto() => new() { Id = Id, Username = Username, DisplayName = DisplayName };
}
public sealed class SessionEnvelope { public string AccessToken { get; set; } = ""; public string RefreshToken { get; set; } = ""; public long AccessExpiresAt { get; set; } public long RefreshExpiresAt { get; set; } }
public sealed class ApiErrorEnvelope { public ApiError? Error { get; set; } }
public sealed class ApiError { public string Code { get; set; } = ""; public string Message { get; set; } = ""; }

public sealed class ApiException(string message, HttpStatusCode statusCode) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
