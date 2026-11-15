using Windows.Media.Control;
using Windows.Storage.Streams;
using Windows.Media;

namespace SpotifyTaskbarWidget;

public sealed record TrackInfo(string Title, string Artist, bool IsPlaying, bool? IsShuffle,
    TimeSpan Position, TimeSpan Duration, DateTime PositionAtUtc, bool IsSpotify, RepeatMode Repeat,
    IRandomAccessStreamReference? Thumbnail);

/// <summary>
/// Reads the current track through the Windows media API (SMTC).
/// Spotify desktop publishes the playing track there - no login and no
/// Spotify API needed. Prefers the Spotify session; without one, follows
/// the active media session.
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private volatile GlobalSystemMediaTransportControlsSession? _session;
    private readonly object _pickLock = new();
    private bool _closed;
    private long _pickRequested;
    private long _pickApplied;

    /// <summary>Fired (on a background thread) when the track or the state changes.</summary>
    public event Action? Changed;

    /// <summary>Fired only when position/duration change - happens every few
    /// seconds, so the handler has to stay light.</summary>
    public event Action? TimelineChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // When starting with Windows, WinRT may not be ready yet - a transient
        // failure here left the widget with no track for the WHOLE session.
        // Retry with backoff (4s->64s, ~2 min) before giving up for good.
        GlobalSystemMediaTransportControlsSessionManager manager;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                    .AsTask(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= 5)
                {
                    // No media session API (N editions without the Media Feature
                    // Pack, broken WinRT) - leave a trace for the report
                    Diag.Once("smtc-init", "Media session API unavailable (this is why nothing shows as playing): " + ex.Message);
                    return;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(4 << attempt), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
        lock (_pickLock)
        {
            if (_closed) return;
            _manager = manager;
            manager.SessionsChanged += OnSessionsChanged;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
        }
        PickSession();
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => PickSession();

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => PickSession();

    /// <summary>Unsubscribes everything - without this a closed window stayed
    /// pinned in memory by the WinRT events and kept processing sessions.</summary>
    public void Shutdown()
    {
        lock (_pickLock)
        {
            _closed = true;
            if (_manager is { } manager)
            {
                _manager = null;
                manager.SessionsChanged -= OnSessionsChanged;
                manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }
            SetSession(null);
        }
    }

    private void PickSession()
    {
        GlobalSystemMediaTransportControlsSessionManager manager;
        long request;
        lock (_pickLock)
        {
            if (_manager is not { } current) return;
            manager = current;
            request = ++_pickRequested;
        }

        try
        {
            var sessions = manager.GetSessions();
            var chosen = sessions.FirstOrDefault(IsSpotifySession);
            if (chosen == null)
            {
                try { chosen = manager.GetCurrentSession(); } catch { }
                chosen ??= sessions.FirstOrDefault(IsPlayingSession) ?? sessions.FirstOrDefault();
            }
            lock (_pickLock)
            {
                if (_closed || request < _pickApplied) return;
                _pickApplied = request;
                if (Equals(_session, chosen)) return;
                SetSession(chosen);
            }
        }
        catch (Exception ex)
        {
            Diag.Once("get-sessions", "Reading media sessions failed: " + ex.Message);
            return;
        }
        Changed?.Invoke();
    }

    private void SetSession(GlobalSystemMediaTransportControlsSession? session)
    {
        var old = _session;
        _session = session;
        if (old != null)
        {
            old.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            old.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            old.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        if (session != null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelineChanged;
        }
    }

    private static bool IsSpotifySession(GlobalSystemMediaTransportControlsSession session) =>
        session.SourceAppUserModelId?.Contains("spotify", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsPlayingSession(GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (Equals(sender, _session)) Changed?.Invoke();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (Equals(sender, _session)) Changed?.Invoke();
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        if (Equals(sender, _session)) TimelineChanged?.Invoke();
    }

    /// <summary>Quick timeline read (no track properties and no cover art).</summary>
    public (TimeSpan Position, TimeSpan Duration, bool IsPlaying, DateTime PositionAtUtc)? GetTimeline()
    {
        var s = _session;
        if (s == null) return null;
        try
        {
            var tl = s.GetTimelineProperties();
            var pi = s.GetPlaybackInfo();
            if (!Equals(s, _session)) return null;
            return (tl?.Position ?? TimeSpan.Zero,
                    tl != null ? tl.EndTime - tl.StartTime : TimeSpan.Zero,
                    pi?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    tl?.LastUpdatedTime.UtcDateTime ?? DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    public async Task<TrackInfo?> GetTrackAsync(CancellationToken cancellationToken)
    {
        var s = _session;
        if (s == null) return null;
        try
        {
            // WinRT continuations may run in the callback apartment, where starting another read can fail.
            var props = await Task.Run(async () => await s.TryGetMediaPropertiesAsync().AsTask(cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var pi = s.GetPlaybackInfo();
            bool playing = pi?.PlaybackStatus ==
                           GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var tl = s.GetTimelineProperties();
            TimeSpan position = tl?.Position ?? TimeSpan.Zero;
            TimeSpan duration = tl != null ? tl.EndTime - tl.StartTime : TimeSpan.Zero;
            DateTime positionAt = tl?.LastUpdatedTime.UtcDateTime ?? DateTime.UtcNow;
            var repeat = pi?.AutoRepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.None => RepeatMode.Off,
                MediaPlaybackAutoRepeatMode.List => RepeatMode.Context,
                MediaPlaybackAutoRepeatMode.Track => RepeatMode.Track,
                _ => RepeatMode.Unknown,
            };

            return Equals(s, _session)
                ? new TrackInfo(props?.Title ?? "", props?.Artist ?? "", playing, pi?.IsShuffleActive,
                    position, duration, positionAt, IsSpotifySession(s), repeat, props?.Thumbnail)
                : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Diag.Once("get-track", "Reading track from the active media session failed: " + ex.Message);
            return null;
        }
    }

    public static async Task<byte[]?> GetThumbnailAsync(IRandomAccessStreamReference thumbnail, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await thumbnail.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (stream.Size is 0 or > 16 * 1024 * 1024) return null;

            uint size = (uint)stream.Size;
            using var reader = new DataReader(stream);
            if (await reader.LoadAsync(size).AsTask(cancellationToken).ConfigureAwait(false) != size) return null;
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        var s = _session;
        if (s == null) return;
        try { await s.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task NextAsync()
    {
        var s = _session;
        if (s == null) return;
        try { await s.TrySkipNextAsync(); } catch { }
    }

    public async Task PreviousAsync()
    {
        var s = _session;
        if (s == null) return;
        try { await s.TrySkipPreviousAsync(); } catch { }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var s = _session;
        if (s == null) return;
        try { await s.TryChangePlaybackPositionAsync(position.Ticks); } catch { }
    }

    public async Task CycleRepeatAsync()
    {
        var s = _session;
        if (s == null) return;
        try
        {
            var current = s.GetPlaybackInfo()?.AutoRepeatMode ?? Windows.Media.MediaPlaybackAutoRepeatMode.None;
            var next = current switch
            {
                Windows.Media.MediaPlaybackAutoRepeatMode.None => Windows.Media.MediaPlaybackAutoRepeatMode.List,
                Windows.Media.MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
            };
            await s.TryChangeAutoRepeatModeAsync(next);
        }
        catch { }
    }

    public async Task ToggleShuffleAsync()
    {
        var s = _session;
        if (s == null) return;
        try
        {
            bool current = s.GetPlaybackInfo()?.IsShuffleActive ?? false;
            await s.TryChangeShuffleActiveAsync(!current);
        }
        catch { }
    }
}
