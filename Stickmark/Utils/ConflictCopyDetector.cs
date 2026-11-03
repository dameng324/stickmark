using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Stickmark.Utils;

/// Detect cloud sync conflicting copy file names (Dropbox/OneDrive/Syncthing). Pure functions — on the filesystem
/// Instead of accessing it directly, only "whether the original exists" required to determine OneDrive is injected as a callback (§3.8).
public static class ConflictCopyDetector
{
    private static readonly Regex DropboxEn = new(
        @"\(.+ conflicted copy( \d{4}-\d{2}-\d{2})?( \(\d+\))?\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DropboxKo = new(
        @"\(conflict[^)]*copy[^)]*\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Syncthing = new(
        @"\.sync-conflict-\d{8}-\d{6}(-\w+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Since the machine name does not change during the life of the process, compile it only once with static readonly.
    private static readonly Regex OneDriveSuffix = new(
        "-" + Regex.Escape(Environment.MachineName) + @"(-\d+)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// fileName is the file name (not path) including extension. siblingExists is a callback that says “Does that file name exist in the vault?”
    /// — The OneDrive pattern frequently causes false positives in hyphenated file names, so you must check the machine name + the existence of the original.
    /// The remaining three types (Dropbox EN/KO, Syncthing) are judged based only on the file name (since the original may have already been organized).
    public static bool IsConflictCopy(string fileName, Func<string, bool> siblingExists)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        if (DropboxEn.IsMatch(stem) || DropboxKo.IsMatch(stem) || Syncthing.IsMatch(stem))
            return true;

        var match = OneDriveSuffix.Match(stem);
        if (match.Success)
        {
            var original = stem[..match.Index];
            if (original.Length > 0 && siblingExists(original + ext))
                return true;
        }

        return false;
    }
}
