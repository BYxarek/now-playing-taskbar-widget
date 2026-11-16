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
- API app credentials entered in the widget are encrypted with Windows Data
  Protection (DPAPI) and stored in the current user's registry. The widget
  also supports credentials supplied through environment variables. Access
  tokens and catalog lookup results are kept in memory.
- Connecting an account opens the platform's authorization page in your
  browser. The local callback listens on `127.0.0.1` only while authorization
  is in progress. The returned account tokens and library results are kept in
  memory. Opening **My library** reads the account's playlists and saved or
  liked tracks. Starting or queuing a Spotify track sends its identifier to
  Spotify. The SoundCloud queue is kept locally in memory.
- The installer-based desktop build checks the public GitHub Releases API for
  new versions at startup and periodically. It downloads an installer from
  GitHub when you request an update or enable automatic installation. These
  requests include the widget's user agent and GitHub receives your IP address.
  Microsoft Store updates are handled by the Store.

This is an independent open-source project, not affiliated with Spotify AB.
[Source code](https://github.com/BYxarek/now-playing-taskbar-widget) ·
[Contact via GitHub Issues](https://github.com/BYxarek/now-playing-taskbar-widget/issues)
