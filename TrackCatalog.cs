using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SpotifyTaskbarWidget;

public enum TrackPlatform { Windows, Spotify, SoundCloud }

internal sealed record CatalogTrack(Uri Link, byte[]? Artwork);
internal sealed record CatalogPlaylist(string Title, string? Id, bool FromLibrary = false);
internal sealed record PlaylistTrack(string Title, string Artist, string? Id, Uri? Link, string? Uploader, Uri? Artwork);
internal sealed record CatalogPage<T>(IReadOnlyList<T> Items, Uri? Next);

internal sealed class TrackCatalog(TrackPlatform platform, string? clientId, string? clientSecret)
{
    private const string CredentialsKey = @"Software\SpotifyTaskbarWidget\Catalog";
    private const int PlaylistLimit = 5;
    private const int PlaylistTrackLimit = 10;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        MaxResponseContentBufferSize = 4 * 1024 * 1024,
    };

    private static volatile TrackCatalog _spotify = Load(TrackPlatform.Spotify);
    private static volatile TrackCatalog _soundCloud = Load(TrackPlatform.SoundCloud);
    public static TrackCatalog Spotify => _spotify;
    public static TrackCatalog SoundCloud => _soundCloud;

    private readonly SemaphoreSlim _gate = new(1);
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _tokenExpires;
    private DateTimeOffset _retryAfter;
    private DateTimeOffset _lookupExpires;
    private (string Title, string Artist, bool ShowArt) _lastLookup;
    private CatalogTrack? _lastResult;
    private (string Artist, DateTimeOffset Expires, IReadOnlyList<CatalogPlaylist> Items)? _playlistCache;

    private bool IsSpotify => platform == TrackPlatform.Spotify;
    public string? ClientId { get; } = clientId;
    public string? ClientSecret { get; } = clientSecret;
    public CatalogAccount Account { get; } = new(platform, clientId, clientSecret);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    private static TrackCatalog Load(TrackPlatform platform)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(CredentialsKey);
            if (key?.GetValue(platform.ToString()) is byte[] encrypted &&
                JsonSerializer.Deserialize<string[]>(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser))
                    is [string id, string secret] && !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret))
                return new(platform, id, secret);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or UnauthorizedAccessException or
            System.Security.SecurityException or System.IO.IOException)
        {
            Diag.Once($"catalog-load-{platform}", $"Could not load saved {platform} credentials.");
        }
        return new(platform, ReadCredential(platform, "CLIENT_ID"), ReadCredential(platform, "CLIENT_SECRET"));
    }

    public bool TrySaveCredentials(string id, string secret)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) return false;
        if (ClientId == id && ClientSecret == secret) return true;
        try
        {
            var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new[] { id, secret }),
                null, DataProtectionScope.CurrentUser);
            using var key = Registry.CurrentUser.CreateSubKey(CredentialsKey);
            key.SetValue(platform.ToString(), encrypted, RegistryValueKind.Binary);
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or
            System.Security.SecurityException or System.IO.IOException)
        {
            Diag.Once($"catalog-save-{platform}", $"Could not save {platform} credentials.");
            return false;
        }
        if (IsSpotify) _spotify = new(platform, id, secret);
        else _soundCloud = new(platform, id, secret);
        return true;
    }

    private static string? ReadCredential(TrackPlatform platform, string name)
    {
        string key = $"{platform.ToString().ToUpperInvariant()}_{name}";
        return Environment.GetEnvironmentVariable(key)
            ?? Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User);
    }

    public static TrackCatalog? For(TrackPlatform platform) => platform switch
    {
        TrackPlatform.Spotify => Spotify,
        TrackPlatform.SoundCloud => SoundCloud,
        _ => null,
    };

    public async Task<CatalogTrack?> FindAsync(string title, string artist, bool showArt, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lookup = (title, artist, showArt);
            var now = DateTimeOffset.UtcNow;
            if (_lastLookup == lookup && now < _lookupExpires) return _lastResult;
            if (now < _retryAfter) return null;

            var query = Uri.EscapeDataString(IsSpotify ? $"track:{title} artist:{artist}" : title);
            var url = IsSpotify
                ? SpotifySearchUrl("track", 10, query)
                : $"https://api.soundcloud.com/tracks?limit=20&linked_partitioning=true&q={query}";
            using var json = await FetchCatalogAsync(url, cancellationToken).ConfigureAwait(false);
            var (link, art) = FindMatch(json.RootElement, IsSpotify, title, artist);
            _lastResult = link == null ? null : new CatalogTrack(link,
                showArt && art != null ? await GetArtworkAsync(art, cancellationToken).ConfigureAwait(false) : null);
            _lastLookup = lookup;
            _lookupExpires = DateTimeOffset.UtcNow.AddMinutes(link != null ? 10 : 2);
            return _lastResult;
        }
        catch (HttpRequestException ex)
        {
            RecordHttpFailure(ex);
            var retry = DateTimeOffset.UtcNow.AddSeconds(30);
            if (_retryAfter < retry) _retryAfter = retry;
            return null;
        }
        catch (JsonException)
        {
            _retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
            Diag.Once($"catalog-json-{platform}", $"{platform} catalog returned invalid JSON.");
            return null;
        }
        catch (OperationCanceledException)
        {
            _retryAfter = DateTimeOffset.UtcNow.AddSeconds(15);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CatalogPlaylist>?> FindPlaylistsAsync(string artist, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(artist)) return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_playlistCache is { } cached && cached.Artist == artist && cached.Expires > now)
                return cached.Items;
            if (now < _retryAfter) return null;

            var query = Uri.EscapeDataString(artist);
            var url = IsSpotify
                ? SpotifySearchUrl("playlist", PlaylistLimit, query)
                : $"https://api.soundcloud.com/playlists?limit={PlaylistLimit}&linked_partitioning=true&show_tracks=false&q={query}";
            using var json = await FetchCatalogAsync(url, cancellationToken).ConfigureAwait(false);
            var playlists = ReadPlaylists(IsSpotify ? Property(json.RootElement, "playlists") : json.RootElement, IsSpotify);
            _playlistCache = (artist, DateTimeOffset.UtcNow.AddMinutes(5), playlists);
            return playlists;
        }
        catch (HttpRequestException ex)
        {
            RecordHttpFailure(ex);
            return null;
        }
        catch (JsonException)
        {
            Diag.Once($"playlists-json-{platform}", $"{platform} playlist search returned invalid JSON.");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CatalogPage<CatalogPlaylist>?> BrowsePlaylistsAsync(string? query, Uri? next, CancellationToken cancellationToken)
    {
        string url = query == null
            ? (IsSpotify ? "https://api.spotify.com/v1/me/playlists?limit=10"
                : "https://api.soundcloud.com/me/playlists?limit=10&linked_partitioning=true&show_tracks=false")
            : SearchUrl("playlist", query);
        using var json = await FetchPageAsync(url, next, query == null, cancellationToken).ConfigureAwait(false);
        if (json == null) return null;
        var page = IsSpotify && query != null ? Property(json.RootElement, "playlists") : json.RootElement;
        var items = ReadPlaylists(page, IsSpotify, query == null);
        return new CatalogPage<CatalogPlaylist>(items, ReadNext(page, IsSpotify, next ?? new Uri(url)));
    }

    public async Task<CatalogPage<PlaylistTrack>?> BrowseTracksAsync(string? query, Uri? next, CancellationToken cancellationToken)
    {
        string url = query == null
            ? (IsSpotify ? "https://api.spotify.com/v1/me/tracks?limit=10"
                : "https://api.soundcloud.com/me/likes/tracks?limit=10&linked_partitioning=true")
            : SearchUrl("track", query);
        using var json = await FetchPageAsync(url, next, query == null, cancellationToken).ConfigureAwait(false);
        if (json == null) return null;
        var page = IsSpotify && query != null ? Property(json.RootElement, "tracks") : json.RootElement;
        return new CatalogPage<PlaylistTrack>(ReadPlaylistTracks(page, IsSpotify, wrapped: query == null),
            ReadNext(page, IsSpotify, next ?? new Uri(url)));
    }

    public async Task<CatalogPage<PlaylistTrack>?> GetPlaylistTracksAsync(CatalogPlaylist playlist, Uri? next,
        CancellationToken cancellationToken)
    {
        if (playlist.Id == null) return null;
        string id = Uri.EscapeDataString(playlist.Id);
        string url = IsSpotify
            ? $"https://api.spotify.com/v1/playlists/{id}/items?limit={PlaylistTrackLimit}&market={RegionInfo.CurrentRegion.TwoLetterISORegionName}"
            : $"https://api.soundcloud.com/playlists/{id}/tracks?linked_partitioning=true";
        using var json = await FetchPageAsync(url, next, playlist.FromLibrary, cancellationToken,
            preferAccount: IsSpotify).ConfigureAwait(false);
        if (json == null) return null;
        return new CatalogPage<PlaylistTrack>(ReadPlaylistTracks(json.RootElement, IsSpotify, wrapped: true),
            ReadNext(json.RootElement, IsSpotify, next ?? new Uri(url)));
    }

    private string SearchUrl(string kind, string query)
    {
        var encoded = Uri.EscapeDataString(query);
        return IsSpotify ? SpotifySearchUrl(kind, 10, encoded)
            : $"https://api.soundcloud.com/{kind}s?limit=10&linked_partitioning=true&q={encoded}" +
              (kind == "playlist" ? "&show_tracks=false" : "");
    }

    private async Task<JsonDocument?> FetchPageAsync(string url, Uri? next, bool requireAccount,
        CancellationToken cancellationToken, bool preferAccount = false)
    {
        if (!IsConfigured || (next != null && !SameEndpoint(new Uri(url), next))) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow < _retryAfter) return null;
            var userToken = requireAccount || preferAccount
                ? await Account.GetTokenAsync(cancellationToken).ConfigureAwait(false) : null;
            if (requireAccount && userToken == null) return null;
            try { return await FetchCatalogAsync(next?.AbsoluteUri ?? url, cancellationToken, userToken).ConfigureAwait(false); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized && userToken != null)
            {
                Account.ForgetToken(userToken);
                return null;
            }
        }
        catch (HttpRequestException ex)
        {
            RecordHttpFailure(ex);
            return null;
        }
        catch (JsonException)
        {
            Diag.Once($"catalog-page-json-{platform}", $"{platform} returned invalid catalog page data.");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool SameEndpoint(Uri current, Uri next) =>
        next.IsAbsoluteUri && next.Scheme == Uri.UriSchemeHttps && next.IsDefaultPort && next.UserInfo.Length == 0 &&
        next.Host == current.Host && next.AbsolutePath == current.AbsolutePath;

    internal static Uri? ReadNext(JsonElement page, bool spotify, Uri current)
    {
        var next = Https(ReadString(page, spotify ? "next" : "next_href"));
        return next != null && next != current && SameEndpoint(current, next) ? next : null;
    }

    private static string SpotifySearchUrl(string type, int limit, string query)
    {
        var market = RegionInfo.CurrentRegion.TwoLetterISORegionName;
        return $"https://api.spotify.com/v1/search?type={type}&limit={limit}&market={market}&q={query}";
    }

    private async Task<JsonDocument> FetchCatalogAsync(string url, CancellationToken cancellationToken, string? userToken = null)
    {
        if (userToken == null && (_accessToken == null || DateTimeOffset.UtcNow >= _tokenExpires))
            await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(IsSpotify ? "Bearer" : "OAuth", userToken ?? _accessToken);
        return await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Uri?> GetSoundCloudStreamAsync(PlaylistTrack track, CancellationToken cancellationToken)
    {
        if (IsSpotify || !IsConfigured || track.Id == null) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow < _retryAfter) return null;
            var url = $"https://api.soundcloud.com/tracks/{Uri.EscapeDataString(track.Id)}/streams";
            using var json = await FetchCatalogAsync(url, cancellationToken).ConfigureAwait(false);
            foreach (string format in new[] { "hls_aac_160_url", "hls_aac_96_url" })
            {
                var stream = Https(ReadString(json.RootElement, format));
                if (IsSoundCloudCdn(stream)) return stream;
                if (stream?.Host != "api.soundcloud.com" || !stream.AbsolutePath.StartsWith("/tracks/", StringComparison.Ordinal))
                    continue;
                using var request = new HttpRequestMessage(HttpMethod.Get, stream);
                request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", _accessToken);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && IsSoundCloudCdn(response.Headers.Location))
                    return response.Headers.Location;
            }
            return null;
        }
        catch (HttpRequestException ex)
        {
            RecordHttpFailure(ex);
            return null;
        }
        catch (JsonException) { return null; }
        finally { _gate.Release(); }
    }

    internal static bool IsSoundCloudCdn(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && Https(uri.AbsoluteUri) != null &&
        (uri.Host.EndsWith(".media-streaming.soundcloud.cloud", StringComparison.Ordinal) ||
         uri.Host.EndsWith(".sndcdn.com", StringComparison.Ordinal));

    private void RecordHttpFailure(HttpRequestException ex)
    {
        if (ex.StatusCode == HttpStatusCode.Unauthorized)
            _tokenExpires = DateTimeOffset.MinValue;
        Diag.Once($"catalog-{platform}-{ex.StatusCode}", $"{platform} catalog request failed ({ex.StatusCode}).");
    }

    private async Task AuthorizeAsync(CancellationToken cancellationToken)
    {
        var url = IsSpotify ? "https://accounts.spotify.com/api/token" : "https://secure.soundcloud.com/oauth/token";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (_refreshToken is { } refreshToken)
        {
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId!,
                ["client_secret"] = ClientSecret!,
            });
        }
        else
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
            });
        }

        try
        {
            using var json = await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
            var token = ReadString(json.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(token))
                throw new JsonException("Missing access token.");
            var expires = Property(json.RootElement, "expires_in");
            _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(
                expires.ValueKind == JsonValueKind.Number && expires.TryGetInt32(out var seconds) ? seconds - 30d : 0);
            _accessToken = token;
            _refreshToken = IsSpotify ? null : ReadString(json.RootElement, "refresh_token");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            _refreshToken = null;
            throw;
        }
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter;
            _retryAfter = retry?.Date ?? DateTimeOffset.UtcNow + (retry?.Delta ?? TimeSpan.FromMinutes(1));
        }
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> GetArtworkAsync(Uri uri, CancellationToken cancellationToken)
    {
        try { return await Http.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) { return null; }
    }

    internal static (Uri? Link, Uri? Art) FindMatch(JsonElement json, bool spotify, string title, string artist)
    {
        var results = spotify ? Property(Property(json, "tracks"), "items") : Property(json, "collection");
        foreach (var track in Items(results))
        {
            if (!Matches(ReadString(track, spotify ? "name" : "title"), title)) continue;
            var artists = Items(Property(track, "artists")).Select(a => ReadString(a, "name")).ToArray();
            var artistMatches = spotify
                ? artists.Any(a => Matches(a, artist)) || Matches(string.Join(", ", artists), artist)
                : Matches(ReadString(track, "metadata_artist"), artist) ||
                  Matches(ReadString(Property(track, "user"), "username"), artist);
            if (!artistMatches) continue;

            var link = ReadCatalogLink(track, spotify);
            if (link == null) continue;

            var art = Https(spotify
                ? Items(Property(Property(track, "album"), "images")).Select(i => ReadString(i, "url")).LastOrDefault()
                : ReadString(track, "artwork_url"));
            bool trustedArt = spotify ? art?.Host == "i.scdn.co"
                : art?.Host.EndsWith(".sndcdn.com", StringComparison.Ordinal) == true;
            return (link, trustedArt ? art : null);
        }
        return (null, null);
    }

    internal static IReadOnlyList<CatalogPlaylist> ReadPlaylists(JsonElement page, bool spotify, bool fromLibrary = false)
    {
        var items = spotify ? Property(page, "items")
            : page.ValueKind == JsonValueKind.Array ? page : Property(page, "collection");
        var playlists = new List<CatalogPlaylist>();
        var links = new HashSet<Uri>();
        foreach (var item in Items(items))
        {
            var title = ReadString(item, spotify ? "name" : "title");
            var link = ReadCatalogLink(item, spotify);
            if (string.IsNullOrWhiteSpace(title) || link == null || !links.Add(link)) continue;
            string? id;
            if (spotify)
            {
                var segments = link.Segments;
                id = segments.Length >= 3 && segments[^2] == "playlist/" &&
                    segments[^1].Length > 0 && segments[^1].All(char.IsAsciiLetterOrDigit)
                    ? segments[^1] : null;
            }
            else
            {
                const string urnPrefix = "soundcloud:playlists:";
                var urn = ReadString(item, "urn");
                id = urn?.StartsWith(urnPrefix, StringComparison.Ordinal) == true &&
                    urn.Length > urnPrefix.Length && urn[urnPrefix.Length..].All(char.IsAsciiDigit) ? urn : null;
            }
            playlists.Add(new CatalogPlaylist(title.Trim(), id, fromLibrary));
        }
        return playlists;
    }

    internal static IReadOnlyList<PlaylistTrack> ReadPlaylistTracks(JsonElement json, bool spotify, bool wrapped)
    {
        var items = spotify ? Property(json, "items")
            : json.ValueKind == JsonValueKind.Array ? json : Property(json, "collection");
        var tracks = new List<PlaylistTrack>();
        foreach (var item in Items(items))
        {
            var track = spotify && wrapped ? Property(item, "item") : item;
            if (spotify && wrapped && track.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                track = Property(item, "track");
            var title = ReadString(track, spotify ? "name" : "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            var uploader = spotify ? null : ReadString(Property(track, "user"), "username");
            var artist = spotify
                ? string.Join(", ", Items(Property(track, "artists")).Select(a => ReadString(a, "name"))
                    .Where(a => !string.IsNullOrWhiteSpace(a)))
                : ReadString(track, "metadata_artist") ?? uploader;
            var link = ReadCatalogLink(track, spotify);
            var id = spotify ? ReadString(track, "id") : ReadString(track, "urn");
            if (spotify)
            {
                if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsAsciiLetterOrDigit) ||
                    ReadString(track, "type") != "track" || Property(track, "is_playable").ValueKind == JsonValueKind.False)
                    id = null;
            }
            else
            {
                const string prefix = "soundcloud:tracks:";
                if (id?.StartsWith(prefix, StringComparison.Ordinal) != true || id.Length == prefix.Length ||
                    !id[prefix.Length..].All(char.IsAsciiDigit) || ReadString(track, "access") is "blocked" or "preview" ||
                    ReadString(track, "sharing") == "private" || link == null || string.IsNullOrWhiteSpace(uploader))
                    id = null;
            }
            var artwork = spotify ? null : Https(ReadString(track, "artwork_url"));
            if (artwork?.Host.EndsWith(".sndcdn.com", StringComparison.Ordinal) != true) artwork = null;
            tracks.Add(new PlaylistTrack(title, artist ?? "", id, link, uploader, artwork));
        }
        return tracks;
    }

    private static Uri? ReadCatalogLink(JsonElement json, bool spotify)
    {
        var link = Https(spotify ? ReadString(Property(json, "external_urls"), "spotify")
            : ReadString(json, "permalink_url"));
        return link?.Host == (spotify ? "open.spotify.com" : "soundcloud.com") ? link : null;
    }

    private static bool Matches(string? value, string expected) =>
        string.Equals(value?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static Uri? Https(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 ? uri : null;

    private static JsonElement Property(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) ? value : default;

    private static string? ReadString(JsonElement json, string name) =>
        Property(json, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static IEnumerable<JsonElement> Items(JsonElement json) =>
        json.ValueKind == JsonValueKind.Array ? json.EnumerateArray() : Enumerable.Empty<JsonElement>();
}
