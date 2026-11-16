# Music API capabilities

Reviewed November 16, 2026. The widget provides catalog search and pagination,
track playback and queueing, and a read-only library for each platform.
Catalog search uses app credentials. Account-specific operations use a
separate account authorization.

| Feature | Spotify Web API | SoundCloud public API |
| --- | --- | --- |
| Search from the menu | Search tracks, artists, albums and playlists. Development Mode allows up to ten results per request. | Search tracks, users and playlists. Track search can filter by genre and playback access. |
| Full playlist browsing | Paginate playlist items. Development Mode gives access to contents of playlists the signed-in user owns or collaborates on. | Paginate playlist tracks and resolve pasted SoundCloud URLs. Public playlists are available with app credentials. |
| My playlists and saved tracks | Use the signed-in user's playlists and saved tracks. Add/remove/check saved items through the `/me/library` API in Development Mode. | Use OAuth 2.1 authorization code with PKCE for personal playlists and actions such as liking tracks and playlists. Public artist likes and public playlists can also support discovery. |
| Add to queue / next up | Premium playback with an account token can add a track or episode to the active Spotify Connect queue. Reading the queue takes a playback-state scope. | Build the queue in the widget from accessible track IDs and request each stream when it is needed. |
| Devices and listening history | Show Spotify Connect devices, transfer playback and read recently played tracks with the relevant scopes. | The signed-in account endpoint provides up to 25 recently played tracks. |
| Discovery | Search for an artist and list albums. Several old browse or recommendation endpoints are unavailable in Development Mode. | Add related tracks, public uploader playlists, and timed comments. Writing a comment or following a creator needs account authorization. |

## Possible next additions

1. **Recently played.** Read the signed-in user's history on both platforms.
2. **Edit the library.** Save or remove tracks on Spotify and like or unlike
   tracks on SoundCloud. Request Spotify write permissions for this feature.
3. **Provider-specific shortcuts.** Add Spotify device selection and SoundCloud
   related tracks or uploader playlists.

Spotify limits Development Mode users and reserves its playback command API
for Premium. SoundCloud uses stream access levels and separate rate limits
for search, likes and stream requests. Spotify and SoundCloud both impose
attribution requirements and restrict experiences that combine content or
streams from multiple services. Review the terms for the intended distribution
before shipping playback, and check permissions before designing a shared
cross-platform queue.

## Official references

- [Spotify Web API search](https://developer.spotify.com/documentation/web-api/reference/search)
- [Spotify Development Mode migration](https://developer.spotify.com/documentation/web-api/tutorials/february-2026-migration-guide)
- [Spotify playback](https://developer.spotify.com/documentation/web-api/reference/start-a-users-playback),
  [queue](https://developer.spotify.com/documentation/web-api/reference/add-to-queue),
  [scopes](https://developer.spotify.com/documentation/web-api/concepts/scopes) and
  [developer policy](https://developer.spotify.com/policy)
- [SoundCloud API guide](https://developers.soundcloud.com/docs/api/guide),
  [OpenAPI reference](https://developers.soundcloud.com/docs/api/explorer/open-api),
  [rate limits](https://developers.soundcloud.com/docs/api/rate-limits.html) and
  [API terms](https://developers.soundcloud.com/docs/api/terms-of-use)
