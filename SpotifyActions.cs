using System.Diagnostics;

namespace SpotifyTaskbarWidget;

internal static class SpotifyActions
{
    public static bool TryOpenPlaylist(string id)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo($"spotify:playlist:{id}") { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName("Spotify");
        foreach (var process in processes)
            process.Dispose();
        return processes.Length > 0;
    }

    public static void OpenSpotifyWindow()
    {
        var window = GetWindowHandles().FirstOrDefault();
        if (window != IntPtr.Zero)
        {
            Interop.ShowWindow(window, Interop.SW_RESTORE);
            Interop.SetForegroundWindow(window);
        }
        else
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
            }
            catch { }
        }
    }

    public static List<IntPtr> GetWindowHandles()
    {
        var windows = new List<IntPtr>();
        foreach (var process in Process.GetProcessesByName("Spotify"))
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                        windows.Add(process.MainWindowHandle);
                }
                catch (InvalidOperationException) { } // Spotify exited during enumeration
            }
        }
        return windows;
    }
}
