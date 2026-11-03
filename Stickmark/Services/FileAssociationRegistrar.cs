using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Stickmark.Services;

/// Register Stickmark in the 'Open Program' list of the .md / .markdown file (HKCU, no administrator privileges required).
/// Do not intercept the default connection and only add it with OpenWithProgids — the user can manually select 'Always open with this app'
/// Once selected, it can be opened by double-clicking. Right-click → It immediately appears in the list of connected programs.
public static class FileAssociationRegistrar
{
    private const string ProgId = "Stickmark.Markdown";
    private static readonly string[] Extensions = { ".md", ".markdown" };

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// Register a Stickmark that runs through exePath. If the content is already the same, nothing is done.
    /// All failures are quietly swallowed — file linking is an add-on and not required for the app to function.
    public static void Ensure(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return;

        try
        {
            var openCommand = $"\"{exePath}\" \"%1\"";
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
            if (classes is null) return;

            // Skip if already registered identically (prevent excessive shell refresh).
            using (var existing = classes.OpenSubKey($@"{ProgId}\shell\open\command"))
            {
                if (existing?.GetValue(null) as string == openCommand)
                {
                    EnsureOpenWithProgids(classes);
                    return;
                }
            }

            using (var prog = classes.CreateSubKey(ProgId))
            {
                prog.SetValue(null, "Markdown Document");
                using (var icon = prog.CreateSubKey("DefaultIcon"))
                    icon.SetValue(null, $"\"{exePath}\",0");
                using (var cmd = prog.CreateSubKey(@"shell\open\command"))
                    cmd.SetValue(null, openCommand);
            }

            EnsureOpenWithProgids(classes);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Registry access failure, etc. — ignored.
        }
    }

    private static void EnsureOpenWithProgids(RegistryKey classes)
    {
        foreach (var ext in Extensions)
        {
            try
            {
                using var progids = classes.CreateSubKey($@"{ext}\OpenWithProgids");
                progids.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
            catch
            {
                // Ignore individual extension failures.
            }
        }
    }
}
