using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace SpotifyTaskbarWidget;

public partial class MainWindow
{
    private TrackPlatform _browsePlatform;
    private CatalogAccount? _browseAccount;
    private bool _libraryConnected;
    private string? _libraryLanguage;
    private int _libraryVersion;
    private int _searchVersion;

    private void UpdateCatalogBrowseMenus(TrackPlatform platform, CatalogAccount? account)
    {
        CatalogSearchMenu.Header = L.CatalogSearch;
        SearchTracksMenu.Header = L.SearchTracks;
        SearchPlaylistsMenu.Header = L.SearchPlaylists;
        CatalogQuery.ToolTip = L.SearchQuery;
        AutomationProperties.SetName(CatalogQuery, L.SearchQuery);
        LibraryMenu.Header = L.MyLibrary;
        CatalogSearchMenu.Visibility = LibraryMenu.Visibility = account == null ? Visibility.Collapsed : Visibility.Visible;

        bool platformChanged = platform != _browsePlatform || account != _browseAccount;
        if (platformChanged)
        {
            _browsePlatform = platform;
            _browseAccount = account;
            ++_searchVersion;
            ResetSearchResults();
            CatalogQuery.Clear();
        }

        bool connected = account?.IsConnected == true;
        if (!platformChanged && connected == _libraryConnected && _libraryLanguage == _settings.Language) return;
        _libraryConnected = connected;
        _libraryLanguage = _settings.Language;
        int version = ++_libraryVersion;
        LibraryMenu.Items.Clear();
        if (TrackCatalog.For(platform) is not { } catalog) return;
        if (!connected)
        {
            LibraryMenu.Items.Add(new MenuItem
            {
                Header = platform == TrackPlatform.Spotify ? L.ConnectSpotify : L.ConnectSoundCloud,
                IsEnabled = false,
            });
            return;
        }

        var tracks = new MenuItem { Header = platform == TrackPlatform.Spotify ? L.SavedTracks : L.LikedTracks };
        AddPagedSubmenu(tracks, platform, (next, token) => catalog.BrowseTracksAsync(null, next, token),
            track => BuildTrackMenu(track, platform), L.BrowseFailed, () => version == _libraryVersion);
        LibraryMenu.Items.Add(tracks);
        var playlists = new MenuItem { Header = L.MyPlaylists };
        AddPagedSubmenu(playlists, platform, (next, token) => catalog.BrowsePlaylistsAsync(null, next, token),
            playlist => BuildPlaylistMenu(playlist, catalog, platform), L.BrowseFailed, () => version == _libraryVersion);
        LibraryMenu.Items.Add(playlists);
    }

    private bool PromptForCatalogCredentials(TrackPlatform platform, TrackCatalog catalog)
    {
        var id = new TextBox { Text = catalog.ClientId ?? "", MaxLength = 512 };
        var secret = new PasswordBox { Password = catalog.ClientSecret ?? "", MaxLength = 512 };
        var redirect = new TextBox { Text = catalog.Account.RedirectUri, IsReadOnly = true };
        var error = new TextBlock { Text = L.CatalogSaveFailed, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var save = new Button { Content = L.Save, MinWidth = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.Cancel, MinWidth = 90, IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = L.CatalogSetupHint(platform.ToString()), TextWrapping = TextWrapping.Wrap });
        foreach (var (caption, field) in new (string, Control)[]
            { ("Client ID", id), ("Client Secret", secret), ("Redirect URI", redirect) })
        {
            var label = new Label { Content = caption, Target = field, Padding = new Thickness(0), Margin = new Thickness(0, 10, 0, 3) };
            AutomationProperties.SetLabeledBy(field, label);
            panel.Children.Add(label);
            panel.Children.Add(field);
        }
        panel.Children.Add(error);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = L.CatalogSetupTitle(platform.ToString()), Content = panel, Width = 380,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false, Topmost = true,
        };
        void Validate()
        {
            save.IsEnabled = !string.IsNullOrWhiteSpace(id.Text) && !string.IsNullOrWhiteSpace(secret.Password);
            error.Visibility = Visibility.Collapsed;
        }
        id.TextChanged += (_, _) => Validate();
        secret.PasswordChanged += (_, _) => Validate();
        dialog.Loaded += (_, _) => { id.Focus(); id.SelectAll(); };
        save.Click += (_, _) =>
        {
            if (catalog.TrySaveCredentials(id.Text.Trim(), secret.Password.Trim()))
                dialog.DialogResult = true;
            else
                error.Visibility = Visibility.Visible;
        };
        Validate();
        return dialog.ShowDialog() == true;
    }

    private void ResetSearchResults()
    {
        while (CatalogSearchMenu.Items.Count > 4)
            CatalogSearchMenu.Items.RemoveAt(4);
        SearchStatus.Visibility = Visibility.Collapsed;
    }

    private async void CatalogQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchCatalogAsync(playlists: false);
    }

    private async void SearchCatalog_Click(object sender, RoutedEventArgs e) =>
        await SearchCatalogAsync(sender == SearchPlaylistsMenu);

    private async Task SearchCatalogAsync(bool playlists)
    {
        TrackPlatform platform = _settings.TrackPlatform;
        if (TrackCatalog.For(platform) is not { } catalog) return;
        int version = ++_searchVersion;
        ResetSearchResults();
        string query = CatalogQuery.Text.Trim();
        if (!catalog.IsConfigured || query.Length == 0)
        {
            SearchStatus.Header = catalog.IsConfigured ? L.SearchNeedQuery : L.PlaylistsNeedCredentials;
            SearchStatus.Visibility = Visibility.Visible;
            return;
        }

        bool Current() => !_closed && version == _searchVersion && _settings.TrackPlatform == platform;
        if (playlists)
            await AppendPageAsync(CatalogSearchMenu, SearchStatus, null,
                (next, token) => catalog.BrowsePlaylistsAsync(query, next, token),
                playlist => BuildPlaylistMenu(playlist, catalog, platform), Current, L.BrowseFailed);
        else
            await AppendPageAsync(CatalogSearchMenu, SearchStatus, null,
                (next, token) => catalog.BrowseTracksAsync(query, next, token),
                track => BuildTrackMenu(track, platform), Current, L.BrowseFailed);
    }

    private void AddPagedSubmenu<T>(MenuItem menu, TrackPlatform platform,
        Func<Uri?, CancellationToken, Task<CatalogPage<T>?>> load, Func<T, MenuItem> build,
        string unavailable, Func<bool>? stillValid = null)
    {
        var status = new MenuItem { IsEnabled = false, Header = L.BrowseLoading };
        menu.Items.Add(status);
        bool loading = false;
        bool loaded = false;
        bool Current() => !_closed && _settings.TrackPlatform == platform && stillValid?.Invoke() != false;
        menu.SubmenuOpened += async (_, e) =>
        {
            if (e.Source != menu || loading || loaded || !Current()) return;
            loading = true;
            try { loaded = await AppendPageAsync(menu, status, null, load, build, Current, unavailable); }
            finally { loading = false; }
        };
    }

    private async Task<bool> AppendPageAsync<T>(MenuItem menu, MenuItem status, Uri? next,
        Func<Uri?, CancellationToken, Task<CatalogPage<T>?>> load, Func<T, MenuItem> build,
        Func<bool> current, string unavailable)
    {
        status.Visibility = Visibility.Visible;
        status.IsEnabled = false;
        status.Header = L.BrowseLoading;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        CatalogPage<T>? page = null;
        try { page = await load(next, timeout.Token); }
        catch (OperationCanceledException) { }
        if (!current()) return false;

        if (page == null)
        {
            status.Header = unavailable;
            if (next != null) status.IsEnabled = true;
            return false;
        }

        foreach (var item in page.Items)
            menu.Items.Add(build(item));
        if (page.Next is { } continuation)
        {
            var more = new MenuItem { Header = L.ShowMore };
            more.Click += async (_, _) =>
            {
                if (current()) await AppendPageAsync(menu, more, continuation, load, build, current, unavailable);
            };
            menu.Items.Add(more);
        }
        if (next != null) menu.Items.Remove(status);
        else if (page.Items.Count == 0 && page.Next == null) status.Header = L.BrowseEmpty;
        else status.Visibility = Visibility.Collapsed;
        return true;
    }

    private MenuItem BuildTrackMenu(PlaylistTrack track, TrackPlatform platform)
    {
        string title = TrackCaption(track);
        var row = new MenuItem { Header = PlaylistLabel(title), IsEnabled = track.Id != null, ToolTip = title };
        ToolTipService.SetShowOnDisabled(row, true);
        AutomationProperties.SetName(row, title);
        if (track.Id is not { } id) return row;

        var status = new MenuItem { IsEnabled = false, Visibility = Visibility.Collapsed };
        var play = new MenuItem { Header = L.PlayTrack };
        var enqueue = new MenuItem { Header = L.AddToQueue };
        play.Click += async (_, _) => await RunCatalogCommandAsync(platform, id, track, status, enqueue: false);
        enqueue.Click += async (_, _) => await RunCatalogCommandAsync(platform, id, track, status, enqueue: true);
        row.Items.Add(play);
        row.Items.Add(enqueue);
        row.Items.Add(status);
        return row;
    }

    private static string TrackCaption(PlaylistTrack track) =>
        track.Artist.Length == 0 ? track.Title : $"{track.Title} — {track.Artist}";

    private void SoundCloudQueue_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.Source != SoundCloudQueueMenu) return;
        var tracks = SoundCloudPlayback.QueueSnapshot();
        var previousAction = SoundCloudQueueMenu.Items[0];
        while (SoundCloudQueueMenu.Items.Count > 1)
            SoundCloudQueueMenu.Items.RemoveAt(1);
        // Recreate the action so reopening the menu only registers one click handler.
        var action = new MenuItem { Header = SoundCloudPlayback.Current == null ? L.PlayTrack : L.TipNext };
        action.Click += async (_, _) =>
        {
            action.IsEnabled = false;
            if (!await PlayNextSoundCloudAsync()) action.Header = L.PlaybackUnavailable;
            else SoundCloudQueueMenu.IsSubmenuOpen = false;
            action.IsEnabled = true;
        };
        SoundCloudQueueMenu.Items.Add(action);
        SoundCloudQueueMenu.Items.Remove(previousAction);
        foreach (var track in tracks)
            SoundCloudQueueMenu.Items.Add(new MenuItem { Header = PlaylistLabel(TrackCaption(track)), IsEnabled = false });
    }

    private async Task<bool> PlayNextSoundCloudAsync()
    {
        if (_startingCatalogPlayback || _closed || _settings.TrackPlatform != TrackPlatform.SoundCloud) return false;
        _startingCatalogPlayback = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { return await SoundCloudPlayback.PlayNextAsync(false, _media.CaptureExternalPause(), timeout.Token); }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Diag.Once("soundcloud-next", "SoundCloud could not open the next queued track.");
            return false;
        }
        finally
        {
            _startingCatalogPlayback = false;
            foreach (var window in Instances)
            {
                window.UpdatePlaybackMenu();
                _ = window.RefreshTrackAsync();
            }
        }
    }
}
