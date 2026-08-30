using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace SpotifyTaskbarWidget;

public partial class App : Application
{
    private static Mutex? _mutex;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _currentHotkeyItem;
    private Forms.ToolStripMenuItem? _changeHotkeyItem;
    private Forms.ToolStripMenuItem? _exitItem;

    /// <summary>True only when the user quit on purpose (or an update);
    /// false when the window dies with Explorer and must be recreated.</summary>
    public static bool IntentionalExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "SpotifyTaskbarWidget_SingleInstance", out bool isNew);
        if (!isNew)
        {
            IntentionalExit = true;
            Shutdown();
            return;
        }

        // The window can be destroyed by an Explorer restart (it is owned by
        // the taskbar) and recreated - the app only exits when the user says so
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            // Shared writing with Diag: dedup of repeats (a timer stuck in an
            // error fires several times a second) and a size ceiling
            Diag.Log(args.Exception.ToString());
            args.Handled = true;
        };

        base.OnStartup(e);

        // One widget window per taskbar selected in the settings
        SpotifyTaskbarWidget.MainWindow.SyncToMonitors();

        // If NO window could be created (startup crash on a particular
        // machine), exit cleanly instead of leaving a zombie process -
        // no UI, no tray icon - holding the single-instance mutex and
        // blocking further attempts to open it (the "flashes then does not
        // load" complaint). The log keeps the exception for diagnosis.
        if (!SpotifyTaskbarWidget.MainWindow.HasWindows)
        {
            Diag.Log("No widget window could be created at startup — exiting so the single-instance mutex is released.");
            IntentionalExit = true;
            Shutdown();
            return;
        }

        CreateTrayIcon();
    }

    private void CreateTrayIcon()
    {
        _currentHotkeyItem = new Forms.ToolStripMenuItem { Enabled = false };
        _changeHotkeyItem = new Forms.ToolStripMenuItem();
        _exitItem = new Forms.ToolStripMenuItem();
        _changeHotkeyItem.Click += (_, _) => Dispatcher.Invoke(SpotifyTaskbarWidget.MainWindow.ChangeHotkey);
        _exitItem.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            IntentionalExit = true;
            Shutdown();
        });

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add(_currentHotkeyItem);
        _trayMenu.Items.Add(_changeHotkeyItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(_exitItem);
        _trayMenu.Opening += (_, _) => RefreshTrayMenu();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                ?? System.Drawing.SystemIcons.Application,
            Text = L.AppTitle,
            ContextMenuStrip = _trayMenu,
            Visible = true,
        };
        _trayIcon.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                RefreshTrayMenu();
                _trayMenu.Show(Forms.Control.MousePosition);
            }
        };
    }

    private void RefreshTrayMenu()
    {
        if (_currentHotkeyItem == null || _changeHotkeyItem == null || _exitItem == null) return;
        _currentHotkeyItem.Text = L.TrayCurrent(SpotifyTaskbarWidget.MainWindow.CurrentHotkeyText());
        _changeHotkeyItem.Text = L.TrayChangeHotkey;
        _exitItem.Text = L.TrayExit;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon != null) _trayIcon.Visible = false;
        _trayIcon?.Dispose();
        _trayMenu?.Dispose();
        base.OnExit(e);
    }
}
