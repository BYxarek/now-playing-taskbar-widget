using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SpotifyTaskbarWidget;

internal enum CatalogPlaybackResult { Started, ConnectSpotify, NoSpotifyDevice, Failed }

internal static class SpotifyPlayback
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });

    public static Task<CatalogPlaybackResult> PlayAsync(string id, bool playlist, CancellationToken cancellationToken) =>
        SendCommandAsync(HttpMethod.Put, "play", playlist
            ? JsonContent.Create(new { context_uri = $"spotify:playlist:{id}" })
            : JsonContent.Create(new { uris = new[] { $"spotify:track:{id}" } }), cancellationToken);

    public static Task<CatalogPlaybackResult> EnqueueAsync(string id, CancellationToken cancellationToken) =>
        SendCommandAsync(HttpMethod.Post, "queue?uri=" + Uri.EscapeDataString($"spotify:track:{id}"), null, cancellationToken);

    private static async Task<CatalogPlaybackResult> SendCommandAsync(HttpMethod method, string endpoint, HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, "https://api.spotify.com/v1/me/player/" + endpoint) { Content = content };
        try
        {
            var account = TrackCatalog.Spotify.Account;
            var token = await account.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            if (token == null) return CatalogPlaybackResult.ConnectSpotify;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                account.ForgetToken(token);
                return CatalogPlaybackResult.ConnectSpotify;
            }
            if (response.StatusCode == HttpStatusCode.NotFound) return CatalogPlaybackResult.NoSpotifyDevice;
            return response.StatusCode == HttpStatusCode.NoContent ? CatalogPlaybackResult.Started : CatalogPlaybackResult.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return CatalogPlaybackResult.Failed;
        }
    }

}
