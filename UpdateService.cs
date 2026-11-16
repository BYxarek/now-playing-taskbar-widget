using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SpotifyTaskbarWidget;

internal static class UpdateService
{
    private const string Repository = "BYxarek/now-playing-taskbar-widget";
    private const string InstallerName = "SpotifyTaskbarWidget-Setup.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C1E9F42-5B8A-4D33-9C61-2F4A8E7B5D10}_is1";
    internal const string FailedArgument = "--update-failed";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly (Version Number, string[] Preview)? CurrentVersion = ParseVersion(
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "");
    private static readonly string? InstallDir = FindInstallDirectory();
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromHours(1) };
    private static List<Release> _releases = new();
    private static bool _autoInstallFailed;

    internal sealed record Release(string Tag, bool IsPreview, long Size, string Digest);

    public static bool IsSupported => InstallDir != null && CurrentVersion != null;
    public static bool IsChecking { get; private set; }
    public static bool IsInstalling { get; private set; }
    public static bool HasChecked { get; private set; }
    public static bool CheckFailed { get; private set; }
    public static bool InstallFailed { get; private set; }
    public static Release? Available => CurrentVersion is { } version
        ? SelectRelease(_releases, version, WidgetSettings.Shared.IncludePrereleases) : null;
    public static event Action? Changed;

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("SpotifyTaskbarWidget");
    }

    public static void Start(bool previousInstallFailed)
    {
        if (!IsSupported) return;
        InstallFailed = _autoInstallFailed = previousInstallFailed;
        Timer.Tick += async (_, _) => await CheckAsync();
        Timer.Start();
        _ = CheckAsync();
    }

    public static async Task CheckAsync()
    {
        if (!IsSupported || IsChecking || IsInstalling) return;
        IsChecking = true;
        CheckFailed = false;
        Changed?.Invoke();
        try
        {
            var releases = new List<Release>();
            for (int page = 1; ; page++)
            {
                using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}");
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                releases.AddRange(ReadReleases(json.RootElement));
                if (json.RootElement.GetArrayLength() < 100) break;
            }
            _releases = releases;
            HasChecked = true;
        }
        catch (Exception ex)
        {
            CheckFailed = true;
            Diag.Once("update-check", "GitHub update check failed: " + ex.Message);
        }
        finally
        {
            IsChecking = false;
            Changed?.Invoke();
        }
        if (!CheckFailed)
            await InstallAsync(automatic: true);
    }

    public static async Task InstallAsync(bool automatic = false)
    {
        if (!IsSupported || IsChecking || IsInstalling || Available is not { } release ||
            (automatic && (_autoInstallFailed || !WidgetSettings.Shared.AutoUpdate)))
            return;

        IsInstalling = true;
        InstallFailed = false;
        Changed?.Invoke();
        string? installer = null;
        bool handedOff = false;
        try
        {
            installer = Path.Combine(Directory.CreateTempSubdirectory("WidgetUpdate-").FullName, InstallerName);
            await Task.Run(() => DownloadAsync(release, installer));
            if (App.IntentionalExit || (automatic && !WidgetSettings.Shared.AutoUpdate) || release != Available)
                return;

            StartInstaller(installer, Environment.ProcessPath!, InstallDir!);
            handedOff = true;
            App.IntentionalExit = true;
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            InstallFailed = _autoInstallFailed = true;
            Diag.Log("Update installation failed: " + ex.Message);
        }
        finally
        {
            if (!handedOff)
            {
                if (installer != null)
                {
                    try
                    {
                        File.Delete(installer);
                        Directory.Delete(Path.GetDirectoryName(installer)!);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                IsInstalling = false;
                Changed?.Invoke();
            }
        }
    }

    private static string? FindInstallDirectory()
    {
        if (PackagedApp.IsPackaged || Environment.ProcessPath is not { } exe) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is string dir && Path.IsPathFullyQualified(dir) &&
                Path.GetFullPath(Path.Combine(dir, "SpotifyTaskbarWidget.exe")).Equals(exe, StringComparison.OrdinalIgnoreCase))
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        }
        catch (Exception ex)
        {
            Diag.Once("update-install-location", "Could not locate the installed widget: " + ex.Message);
        }
        return null;
    }

    internal static IEnumerable<Release> ReadReleases(JsonElement releases)
    {
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("tag_name").GetString() is not { } tag)
                continue;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != InstallerName ||
                    asset.GetProperty("state").GetString() != "uploaded" ||
                    !asset.TryGetProperty("digest", out var digestProperty) ||
                    digestProperty.GetString() is not { } digest ||
                    !Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z"))
                    continue;
                long size = asset.GetProperty("size").GetInt64();
                if (size is >= 2 and <= 512 * 1024 * 1024)
                    yield return new Release(tag, release.GetProperty("prerelease").GetBoolean(), size, digest[7..]);
                break;
            }
        }
    }

    private static async Task DownloadAsync(Release release, string installer)
    {
        string url = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(release.Tag)}/{InstallerName}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != release.Size)
            throw new IOException("Unexpected installer size.");

        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var file = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await CopyVerifiedAsync(source, file, release, timeout.Token);
    }

    internal static async Task CopyVerifiedAsync(Stream source, Stream file, Release release, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] signature = new byte[2];
        await source.ReadExactlyAsync(signature, token);
        if (signature[0] != 'M' || signature[1] != 'Z')
            throw new IOException("Installer is not a Windows executable.");
        hash.AppendData(signature);
        await file.WriteAsync(signature, token);
        byte[] buffer = new byte[81920];
        long total = signature.Length;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            total += read;
            if (total > release.Size) throw new IOException("Installer exceeds the published size.");
            hash.AppendData(buffer, 0, read);
            await file.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Installer SHA-256 or size does not match the release.");
    }

    private static void StartInstaller(string installer, string widget, string installDir)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        string args = $"/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=20 /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /WIDGETUPDATE=1 /DIR=\"{installDir}\"";
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                $widgetProcess = Get-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
                if ($widgetProcess -and -not $widgetProcess.WaitForExit(30000)) { exit 1 }
                $failed = $true
                try {
                    $setup = Start-Process -FilePath {{Quote(installer)}} -ArgumentList {{Quote(args)}} -PassThru -Wait
                    $failed = $setup.ExitCode -ne 0
                } finally {
                    if ($failed) { Start-Process -FilePath {{Quote(widget)}} -ArgumentList '{{FailedArgument}}' }
                    else { Start-Process -FilePath {{Quote(widget)}} }
                }
            } finally {
                Remove-Item -LiteralPath {{Quote(installer)}} -ErrorAction SilentlyContinue
                [IO.Directory]::Delete({{Quote(Path.GetDirectoryName(installer)!)}})
            }
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the installer helper.");
    }

    internal static (Version Number, string[] Preview)? ParseVersion(string value)
    {
        // GitHub has used both v1.4.0.beta.1 and v1.4.0-beta.1.
        var match = Regex.Match(value, @"\A[vV]?(\d+\.\d+\.\d+)(?:[-.]([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?\z",
            RegexOptions.CultureInvariant);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var number)
            ? (number, match.Groups[2].Success ? match.Groups[2].Value.Split('.') : [])
            : null;
    }

    internal static Release? SelectRelease(IEnumerable<Release> releases, (Version Number, string[] Preview) current, bool includePreview)
    {
        Release? best = null;
        foreach (var release in releases)
        {
            if (ParseVersion(release.Tag) is not { } version ||
                (!includePreview && (release.IsPreview || version.Preview.Length > 0)) ||
                CompareVersions(version, current) <= 0)
                continue;
            best = release;
            current = version;
        }
        return best;
    }

    private static int CompareVersions((Version Number, string[] Preview) left, (Version Number, string[] Preview) right)
    {
        int comparison = left.Number.CompareTo(right.Number);
        if (comparison != 0) return comparison;
        if (left.Preview.Length == 0 || right.Preview.Length == 0)
            return right.Preview.Length.CompareTo(left.Preview.Length);
        foreach (var (a, b) in left.Preview.Zip(right.Preview))
        {
            bool aNumeric = a.All(char.IsAsciiDigit);
            bool bNumeric = b.All(char.IsAsciiDigit);
            if (aNumeric != bNumeric) return aNumeric ? -1 : 1;
            string leftIdentifier = aNumeric ? a.TrimStart('0') : a;
            string rightIdentifier = bNumeric ? b.TrimStart('0') : b;
            if (aNumeric && leftIdentifier.Length != rightIdentifier.Length)
                return leftIdentifier.Length.CompareTo(rightIdentifier.Length);
            comparison = string.CompareOrdinal(leftIdentifier, rightIdentifier);
            if (comparison != 0) return comparison;
        }
        return left.Preview.Length.CompareTo(right.Preview.Length);
    }
}
