# Privacy Policy — Now Playing - Taskbar Widget

**Last updated: November 16, 2026**

Now Playing - Taskbar Widget has no analytics, telemetry, or widget accounts.

- All settings are stored locally on your device
  (`%APPDATA%\SpotifyTaskbarWidget\`).
- In the default Windows media mode, track information is read locally from
  the Windows media session and Spotify desktop.
- If you select an API catalog and configure its credentials, the widget sends
  a track search to that catalog over HTTPS. The Spotify query uses the title,
  artist and Windows region. The SoundCloud query uses the title, and the
  artist is matched locally. The widget may download the matching artwork.
  Opening the playlists submenu sends the current artist's name as a public
  playlist search query to the selected catalog. Expanding a result requests
  the list of tracks using that playlist's identifier.
  Using catalog search sends the text you submit to the selected service.
  Selecting a playable SoundCloud track requests and plays its audio stream.
  These requests are governed by [Spotify's privacy policy](https://www.spotify.com/legal/privacy-policy/)
  or [SoundCloud's privacy policy](https://soundcloud.com/pages/privacy).
- API app credentials are supplied through environment variables. Access
  tokens and catalog lookup results are kept in memory.
- Connecting an account opens the platform's authorization page in your
  browser. The local callback listens on `127.0.0.1` only while authorization
  is in progress. The returned account tokens and library results are kept in
  memory. Opening **My library** reads the account's playlists and saved or
  liked tracks. Starting or queuing a Spotify track sends its identifier to
  Spotify. The SoundCloud queue is kept locally in memory.
- Microsoft Store updates are handled by the Store.
- The optional "Support the project" menu item opens your web browser at
  ko-fi.com; any interaction there is governed by Ko-fi's own privacy policy.

This is an independent open-source project, not affiliated with Spotify AB.
Source code: https://github.com/mechanicwb2-hub/spotify-taskbar-widget

Contact: open an issue at
https://github.com/mechanicwb2-hub/spotify-taskbar-widget/issues
