using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Stickmark.Utils;

/// Change the note title to a file name that is safe for the file system, and make sure it is not duplicated within one folder.
public static class ExportNaming
{
    private const int MaxLength = 80;

    /// Normalize the title to a safe file name (excluding extension). If the title is empty or an empty string after normalization, fallback is used.
    public static string SafeFileName(string? title, string fallback = "note")
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length == 0) return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            var bad = ch is '\r' or '\n' or '\t' || Array.IndexOf(invalid, ch) >= 0;
            sb.Append(bad ? '_' : ch);
        }

        // Windows does not allow dots or spaces at the end of the name.
        var name = sb.ToString().TrimEnd('.', ' ');
        if (name.Length > MaxLength) name = name[..MaxLength].TrimEnd('.', ' ');
        if (name.Length == 0) return fallback;

        // Reserved device names (CON, NUL, COM1...) are still reserved even if they have an extension, so avoid them by adding _ in front.
        return IsReservedDeviceName(name) ? "_" + name : name;
    }

    private static bool IsReservedDeviceName(string name)
    {
        // Reservation is determined only by the part before the first dot (e.g. "CON.txt" is also reserved).
        var stem = name;
        var dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        return Array.Exists(ReservedNames, r => string.Equals(r, stem, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// To ensure that baseName + extension is unique in the taken set (lowercase comparison), add " (2)" and " (3)" if necessary.
    /// The returned file name is added to taken.
    public static string UniqueFileName(string baseName, string extension, ISet<string> taken)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var candidate = baseName + ext;
        var i = 2;
        while (taken.Contains(candidate.ToLowerInvariant()))
        {
            candidate = $"{baseName} ({i}){ext}";
            i++;
        }
        taken.Add(candidate.ToLowerInvariant());
        return candidate;
    }
}
