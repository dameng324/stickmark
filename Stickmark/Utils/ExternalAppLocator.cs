using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Stickmark.Utils;

/// Detects and executes Obsidian·VS Code installation as read-only. For each Tray Menu Opened event
/// Since it is called, keep it light (registry 1-2 times, file existence check within 3 times, §3.9).
public static class ExternalAppLocator
{
    // ── Obsidian ────────────────────────────────────────────────────────────

    public static bool IsObsidianInstalled() => IsUrlProtocolRegistered("obsidian");

    public static void OpenObsidian(string vaultPath)
    {
        var uri = "obsidian://open?path=" + Uri.EscapeDataString(vaultPath);
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    // ── VS Code ─────────────────────────────────────────────────────────────

    public static bool IsVsCodeAvailable() => FindVsCode() is not null || IsUrlProtocolRegistered("vscode");

    public static string? FindVsCode() => FindVsCode(File.Exists, Environment.GetEnvironmentVariable);

    /// In the same search order (code.cmd in ①LOCALAPPDATA ②ProgramFiles ③PATH, directly split search without where) —
    /// Separate file system and environment variable queries so that they can be injected and unit test without a registry.
    public static string? FindVsCode(Func<string, bool> fileExists, Func<string, string?> getEnv)
    {
        var localAppData = getEnv("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(localAppData))
        {
            var candidate = Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe");
            if (fileExists(candidate)) return candidate;
        }

        var programFiles = getEnv("ProgramFiles");
        if (!string.IsNullOrEmpty(programFiles))
        {
            var candidate = Path.Combine(programFiles, "Microsoft VS Code", "Code.exe");
            if (fileExists(candidate)) return candidate;
        }

        var pathVar = getEnv("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir.Trim(), "code.cmd");
                if (fileExists(candidate)) return candidate;
            }
        }

        return null;
    }

    public static void OpenVsCode(string vaultPath)
    {
        var codePath = FindVsCode();
        if (codePath is not null)
        {
            // Pass the folder path as an argument array without shell string concatenation.
            var psi = new ProcessStartInfo(codePath) { UseShellExecute = false };
            psi.ArgumentList.Add(vaultPath);
            Process.Start(psi);
            return;
        }

        // If executable is not found, fallback to protocol — normalize path separator to URI convention (/).
        var uri = "vscode://file/" + vaultPath.Replace('\\', '/');
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    // ── Common ───────────────────────────────────────────────────────────────────

    private static bool IsUrlProtocolRegistered(string scheme)
    {
        try
        {
            using var hkcr = Registry.ClassesRoot.OpenSubKey(scheme);
            if (hkcr?.GetValue("URL Protocol") is not null) return true;
        }
        catch { /* Read failure is considered not installed */ }

        try
        {
            using var hkcu = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{scheme}");
            if (hkcu?.GetValue("URL Protocol") is not null) return true;
        }
        catch { /* Read failure is considered not installed */ }

        return false;
    }
}
