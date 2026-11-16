using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace SpotifyTaskbarWidget;

internal sealed class CatalogAccount(TrackPlatform platform, string? clientId, string? clientSecret)
{
    private const int CallbackPort = 43873;
    private bool IsSpotify => platform == TrackPlatform.Spotify;
    private string CallbackPath => IsSpotify ? "/spotify/callback" : "/soundcloud/callback";
    internal string RedirectUri => $"http://127.0.0.1:{CallbackPort}{CallbackPath}";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        MaxResponseContentBufferSize = 64 * 1024,
    };
    private readonly SemaphoreSlim TokenGate = new(1);
    private sealed record AccountToken(string Access, string Refresh, DateTimeOffset Expires);
    private volatile AccountToken? _token;
    private int _connecting;

    public static CatalogAccount? For(TrackPlatform platform) => TrackCatalog.For(platform)?.Account;

    public bool IsConnected => _token != null;

    public void ForgetToken(string access)
    {
        if (_token is { } token && token.Access == access)
            Interlocked.CompareExchange(ref _token, null, token);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (IsSpotify || await GetTokenAsync(cancellationToken).ConfigureAwait(false) is not { } access) return;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.soundcloud.com/disconnect");
            request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", access);
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            Diag.Once("soundcloud-disconnect", "SoundCloud account revocation failed.");
        }
        finally { _token = null; }
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId) || (!IsSpotify && string.IsNullOrWhiteSpace(clientSecret)) ||
            Interlocked.Exchange(ref _connecting, 1) != 0)
            return false;

        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
            listener.ExclusiveAddressUse = true;
            listener.Start();
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            string authority = IsSpotify ? "accounts.spotify.com" : "secure.soundcloud.com";
            string scope = IsSpotify ? "&scope=user-modify-playback-state%20playlist-read-private%20playlist-read-collaborative%20user-library-read" : "";
            var url = $"https://{authority}/authorize?response_type=code&code_challenge_method=S256" +
                $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                $"{scope}&state={state}&code_challenge={challenge}";
            using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            while (true)
            {
                using var socket = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    using var stream = socket.GetStream();
                    bool callback = TryReadCallback(await ReadRequestLineAsync(stream, readTimeout.Token).ConfigureAwait(false),
                        CallbackPath, state, out var code);
                    string reply = code == null ? "400 Bad Request" : "200 OK";
                    var message = code == null ? "Authorization was not completed." : "Return to Now Playing - Taskbar Widget.";
                    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {reply}\r\nContent-Type: text/plain\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {message.Length}\r\n\r\n{message}");
                    await stream.WriteAsync(response, readTimeout.Token).ConfigureAwait(false);
                    if (!callback) continue;
                    if (code == null) return false;
                    _token = await ExchangeAsync(new()
                    {
                        ["grant_type"] = "authorization_code", ["client_id"] = clientId,
                        ["code"] = code, ["redirect_uri"] = RedirectUri, ["code_verifier"] = verifier,
                    }, null, cancellationToken).ConfigureAwait(false);
                    return _token != null;
                }
                catch (IOException) { }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or JsonException or System.ComponentModel.Win32Exception)
        {
            Diag.Once($"{platform}-account", $"{platform} account authorization failed.");
            return false;
        }
        finally { Interlocked.Exchange(ref _connecting, 0); }
    }

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token == null) return null;
        await TokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var token = _token;
            if (token == null) return null;
            if (token.Expires > DateTimeOffset.UtcNow) return token.Access;
            var refreshed = await ExchangeAsync(new()
            {
                ["grant_type"] = "refresh_token", ["client_id"] = clientId ?? "", ["refresh_token"] = token.Refresh,
            }, token.Refresh, cancellationToken).ConfigureAwait(false);
            return ReferenceEquals(Interlocked.CompareExchange(ref _token, refreshed, token), token) ? refreshed?.Access : null;
        }
        finally { TokenGate.Release(); }
    }

    private async Task<AccountToken?> ExchangeAsync(Dictionary<string, string> form, string? refresh,
        CancellationToken cancellationToken)
    {
        if (!IsSpotify) form["client_secret"] = clientSecret ?? "";
        var endpoint = IsSpotify ? "https://accounts.spotify.com/api/token" : "https://secure.soundcloud.com/oauth/token";
        using var content = new FormUrlEncodedContent(form);
        using var response = await Http.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest) return null;
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var value = json.RootElement;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("access_token", out var accessValue) || accessValue.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("expires_in", out var expiresValue) || expiresValue.ValueKind != JsonValueKind.Number ||
            !expiresValue.TryGetInt32(out int expires) || expires <= 0)
            throw new JsonException("Incomplete catalog OAuth response.");
        var access = accessValue.GetString();
        if (value.TryGetProperty("refresh_token", out var replacement) && replacement.ValueKind == JsonValueKind.String)
            refresh = replacement.GetString();
        return string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh) ? null
            : new AccountToken(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expires - 30d));
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            int end = Array.IndexOf(buffer, (byte)'\n', total, read);
            if (end >= 0) return Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\r');
            total += read;
        }
        return "";
    }

    internal static bool TryReadCallback(string line, string callbackPath, string state, out string? code)
    {
        code = null;
        var parts = line.Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || !parts[1].StartsWith(callbackPath + "?", StringComparison.Ordinal))
            return false;
        var query = HttpUtility.ParseQueryString(parts[1][(callbackPath.Length + 1)..]);
        if (query.GetValues("state") is not [var returned] || returned != state) return false;
        if (query.GetValues("code") is [var value] && !string.IsNullOrWhiteSpace(value) && query["error"] == null)
        {
            code = value;
            return true;
        }
        return query.GetValues("error") is [var error] && error == "access_denied" && query["code"] == null;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
