using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace SpotifyTaskbarWidget;

internal static class SoundCloudPlayback
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim TransportGate = new(1);
    private static readonly Queue<PlaylistTrack> Upcoming = new();
    private static long _generation;
    private static MediaPlayer? _player;
    private static MediaSource? _source;
    private static PlaylistTrack? _track;
    private static IRandomAccessStreamReference? _thumbnail;

    public static PlaylistTrack? Current
    {
        get { lock (Gate) return _track; }
    }

    public static IReadOnlyList<PlaylistTrack> QueueSnapshot()
    {
        lock (Gate) return Upcoming.ToArray();
    }

    public static bool HasNext
    {
        get { lock (Gate) return Upcoming.Count > 0; }
    }

    public static void Enqueue(PlaylistTrack track)
    {
        lock (Gate) Upcoming.Enqueue(track);
    }

    public static async Task<bool> PlayTrackAsync(PlaylistTrack track, Func<CancellationToken, Task> pauseExternal,
        CancellationToken cancellationToken)
    {
        long generation;
        lock (Gate) generation = _generation;
        await TransportGate.WaitAsync(cancellationToken);
        try
        {
            return await LoadTrackAsync(track, pauseExternal, generation, cancellationToken);
        }
        finally { TransportGate.Release(); }
    }

    public static async Task<bool> PlayNextAsync(bool onlyWhenIdle, Func<CancellationToken, Task> pauseExternal,
        CancellationToken cancellationToken)
    {
        long generation;
        lock (Gate) generation = _generation;
        await TransportGate.WaitAsync(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                PlaylistTrack track;
                lock (Gate)
                {
                    if (generation != _generation || (onlyWhenIdle && _player != null) ||
                        !Upcoming.TryDequeue(out track!)) return false;
                }
                using var trackTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                trackTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    if (await LoadTrackAsync(track, pauseExternal, generation, trackTimeout.Token)) return true;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
            return false;
        }
        finally { TransportGate.Release(); }
    }

    private static async Task<bool> LoadTrackAsync(PlaylistTrack track, Func<CancellationToken, Task> pauseExternal,
        long generation, CancellationToken cancellationToken)
    {
        lock (Gate)
            if (generation != _generation) return false;
        var stream = await TrackCatalog.SoundCloud.GetSoundCloudStreamAsync(track, cancellationToken);
        return stream != null && await PlayAsync(track, stream, pauseExternal, generation, cancellationToken);
    }

    public static async Task<bool> PlayAsync(PlaylistTrack track, Uri stream,
        Func<CancellationToken, Task> pauseExternal, long generation, CancellationToken cancellationToken)
    {
        var player = new MediaPlayer();
        MediaSource? source = null;
        bool retained = false;
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult(true);
        player.MediaFailed += (_, _) => opened.TrySetResult(false);
        try
        {
            source = MediaSource.CreateFromUri(stream);
            var item = new MediaPlaybackItem(source);
            var display = item.GetDisplayProperties();
            display.Type = MediaPlaybackType.Music;
            display.MusicProperties.Title = track.Title;
            display.MusicProperties.Artist = track.Uploader ?? track.Artist;
            var thumbnail = track.Artwork == null ? null : RandomAccessStreamReference.CreateFromUri(track.Artwork);
            if (thumbnail != null) display.Thumbnail = thumbnail;
            item.ApplyDisplayProperties(display);
            player.Source = item;
            if (!await opened.Task.WaitAsync(cancellationToken)) return false;
            lock (Gate)
                if (generation != _generation) return false;
            await pauseExternal(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            lock (Gate)
            {
                if (generation != _generation) return false;
                ReleaseCurrent();
                player.MediaFailed += (sender, _) => AdvanceAfter(sender);
                player.MediaEnded += (sender, _) => AdvanceAfter(sender);
                player.Play();
                _player = player;
                _source = source;
                _track = track;
                _thumbnail = thumbnail;
                retained = true;
            }
            return true;
        }
        finally
        {
            if (!retained)
            {
                player.Dispose();
                source?.Dispose();
            }
        }
    }

    private static void AdvanceAfter(MediaPlayer sender)
    {
        _ = Task.Run(async () =>
        {
            lock (Gate)
            {
                if (!ReferenceEquals(_player, sender)) return;
                ReleaseCurrent();
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await PlayNextAsync(true, _ => Task.CompletedTask, timeout.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Diag.Once("soundcloud-queue", "SoundCloud could not open a queued track.");
            }
        });
    }

    public static void Stop()
    {
        lock (Gate)
        {
            ++_generation;
            Upcoming.Clear();
            ReleaseCurrent();
        }
    }

    private static void ReleaseCurrent()
    {
        var player = _player;
        _player = null;
        _track = null;
        _thumbnail = null;
        player?.Dispose();
        _source?.Dispose();
        _source = null;
    }

    public static TrackInfo? Read()
    {
        lock (Gate)
        {
            if (_player == null || _track == null) return null;
            var session = _player.PlaybackSession;
            return new TrackInfo(_track.Title, _track.Uploader ?? _track.Artist,
                session.PlaybackState == MediaPlaybackState.Playing, null, session.Position,
                session.NaturalDuration, DateTime.UtcNow, false, RepeatMode.Unknown, _thumbnail);
        }
    }

    public static bool Toggle()
    {
        lock (Gate)
        {
            if (_player == null) return false;
            if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause();
            else _player.Play();
            return true;
        }
    }

    public static bool Seek(TimeSpan position)
    {
        lock (Gate)
        {
            if (_player == null) return false;
            if (_player.PlaybackSession.CanSeek) _player.PlaybackSession.Position = position;
            return true;
        }
    }
}
