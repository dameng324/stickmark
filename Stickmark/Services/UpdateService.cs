using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Stickmark.Resources;

namespace Stickmark.Services;

public sealed class UpdateService : IUpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/dameng324/stickmark/releases/latest";
    private const string AssetSuffix = "-win-x64-setup.exe";
    private const string ChecksumSuffix = "-win-x64-setup.exe.sha256";

    private static readonly HttpClient Http = CreateClient();
    private readonly ILogger<UpdateService> _logger;
    private bool _inProgress;

    public UpdateService(ILogger<UpdateService> logger) => _logger = logger;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Stickmark-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (_inProgress) return;
        _inProgress = true;
        try
        {
            var current = CurrentVersion();

            ReleaseInfo? release;
            try
            {
                var json = await Http.GetStringAsync(LatestReleaseApi).ConfigureAwait(false);
                release = ParseRelease(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Update check failed");
                if (userInitiated) Report(Strings.Update_CheckFailed, MessageBoxImage.Warning);
                return;
            }

            if (release?.Version is null)
            {
                if (userInitiated) Report(Strings.Update_InfoUnreadable, MessageBoxImage.Warning);
                return;
            }

            if (Compare(release.Version, current) <= 0)
            {
                _logger.LogInformation("Up to date (current {Current}, latest {Latest})", current, release.Tag);
                if (userInitiated) Report(string.Format(Strings.Update_UpToDateFormat, current.Major, current.Minor, current.Build), MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrEmpty(release.DownloadUrl) || string.IsNullOrEmpty(release.ChecksumUrl))
            {
                // Do not suggest releases without checksums to verify (no integrity verification).
                _logger.LogWarning("Newer release {Tag} lacks installer/sha256 asset", release.Tag);
                if (userInitiated) Report(string.Format(Strings.Update_AssetNotFoundFormat, release.Tag), MessageBoxImage.Warning);
                return;
            }

            var proceed = Ask(string.Format(Strings.Update_AvailablePromptFormat, release.Tag));
            if (!proceed) return;

            await DownloadAndApplyAsync(release).ConfigureAwait(false);
        }
        finally
        {
            _inProgress = false;
        }
    }

    private async Task DownloadAndApplyAsync(ReleaseInfo release)
    {
        // Defense: Tag·URL·checksum URL verification. Instead of putting the tag (GitHub value) in the file path or script as is,
        // Download/checksum sources are limited to https + GitHub hosts.
        if (!IsSafeReleaseTag(release.Tag) || !IsTrustedDownloadUrl(release.DownloadUrl)
            || !IsTrustedDownloadUrl(release.ChecksumUrl))
        {
            _logger.LogWarning("Refusing update: untrusted tag/url/checksum ({Tag})", release.Tag);
            Report(Strings.Update_ApplyFailed, MessageBoxImage.Warning);
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await Http.GetByteArrayAsync(release.DownloadUrl!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update download failed");
            Report(Strings.Update_DownloadFailed, MessageBoxImage.Warning);
            return;
        }

        // Integrity verification — check against .sha256 assets in release. In case of mismatch or omission, application is rejected (fail-closed).
        try
        {
            var checksumText = await Http.GetStringAsync(release.ChecksumUrl!).ConfigureAwait(false);
            if (!VerifyChecksum(bytes, checksumText))
            {
                _logger.LogError("Update integrity verification FAILED for {Tag}", release.Tag);
                Report(Strings.Update_IntegrityFailed, MessageBoxImage.Warning);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update checksum fetch/verify failed");
            Report(Strings.Update_IntegrityFailed, MessageBoxImage.Warning);
            return;
        }

        // Stage only after verification. The installer path is unpredictable and contains no tag.
        var stageDir = Path.Combine(Path.GetTempPath(), "Stickmark-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageDir);
        var tempInstaller = Path.Combine(stageDir, "Stickmark-update-setup.exe");
        try
        {
            await File.WriteAllBytesAsync(tempInstaller, bytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update staging write failed");
            Report(Strings.Update_DownloadFailed, MessageBoxImage.Warning);
            return;
        }

        var target = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(target))
        {
            Report(Strings.Update_ExecutablePathUnknown, MessageBoxImage.Warning);
            return;
        }

        try
        {
            LaunchInstaller(stageDir, tempInstaller, target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch the update installer");
            Report(Strings.Update_ApplyFailed, MessageBoxImage.Warning);
            return;
        }

        // Quit so the installer can replace files; the helper relaunches the app afterwards.
        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    private static void LaunchInstaller(string stageDir, string installerPath, string targetExe)
    {
        var pid = Environment.ProcessId;
        var installDir = Path.GetDirectoryName(targetExe) ?? string.Empty;
        var batPath = Path.Combine(stageDir, "apply-update.cmd");
        var script =
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
            "if not errorlevel 1 (\r\n" +
            "  timeout /t 1 /nobreak >nul\r\n" +
            "  goto wait\r\n" +
            ")\r\n" +
            $"start \"\" /wait \"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /DIR=\"{installDir}\"\r\n" +
            $"start \"\" \"{targetExe}\"\r\n" +
            $"del \"{installerPath}\" >nul 2>&1\r\n" +
            "del \"%~f0\"\r\n";
        File.WriteAllText(batPath, script, new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{batPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static Version CurrentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// Compares two versions on Major.Minor.Build only (ignores the revision field).
    internal static int Compare(Version a, Version b)
    {
        if (a.Major != b.Major) return a.Major.CompareTo(b.Major);
        var an = Math.Max(0, a.Minor); var bn = Math.Max(0, b.Minor);
        if (an != bn) return an.CompareTo(bn);
        return Math.Max(0, a.Build).CompareTo(Math.Max(0, b.Build));
    }

    internal static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
        var tag = tagEl.GetString() ?? string.Empty;
        var version = ParseVersion(tag);

        string? url = null, checksumUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null) continue;
                var dl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name.EndsWith(ChecksumSuffix, StringComparison.OrdinalIgnoreCase)) checksumUrl ??= dl;
                else if (name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase)) url ??= dl;
            }
        }
        return new ReleaseInfo(tag, version, url, checksumUrl);
    }

    internal static Version? ParseVersion(string tag)
    {
        var t = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(t, out var v) ? v : null;
    }

    /// Whether the GitHub tag is in the expected version format (v1.2.3, etc.). Verification before inserting into file path/script.
    internal static bool IsSafeReleaseTag(string? tag) =>
        !string.IsNullOrEmpty(tag) && Regex.IsMatch(tag, @"^v?\d+(\.\d+){0,3}$");

    /// The download URL is https and the GitHub release host. Block arbitrary origin downloads.
    internal static bool IsTrustedDownloadUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// Does the SHA-256 of the downloaded bytes match the value of the checksum file ("<64-character hex> file name").
    internal static bool VerifyChecksum(byte[] data, string? checksumFileText)
    {
        var expected = ExtractSha256Hex(checksumFileText);
        if (expected is null) return false;
        var actual = Convert.ToHexString(SHA256.HashData(data));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// Extract the first 64 hexadecimal (SHA-256) tokens from the checksum file text. If not, null.
    internal static string? ExtractSha256Hex(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = Regex.Match(text, @"\b([0-9a-fA-F]{64})\b");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static void Report(string message, MessageBoxImage icon) =>
        Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(message, "Stickmark", MessageBoxButton.OK, icon));

    private static bool Ask(string message) =>
        Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(message, Strings.Update_ConfirmCaption, MessageBoxButton.OKCancel, MessageBoxImage.Question)
            == MessageBoxResult.OK);

    internal sealed record ReleaseInfo(string Tag, Version? Version, string? DownloadUrl, string? ChecksumUrl);
}
