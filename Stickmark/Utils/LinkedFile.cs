using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Stickmark.Models;

namespace Stickmark.Utils;

/// File I/O helper for notes linked to external text/markdown files.
/// Reading is automatically recognized by the BOM, writing is unified as UTF-8 (no BOM).
public static class LinkedFile
{
    /// Extensions allowed in the open dialog, drop, and file connection.
    private static readonly string[] KnownExtensions =
    {
        ".md", ".markdown", ".mkd", ".mdown", ".mdwn", ".mdtext",
        ".txt", ".text", ".log",
    };

    /// Filter string for the open dialog.
    public const string OpenDialogFilter =
        "Markdown / text (*.md;*.markdown;*.txt)|*.md;*.markdown;*.mkd;*.mdown;*.mdwn;*.txt;*.text;*.log|All files (*.*)|*.*";

    /// Convert to absolute/normalized path. If it fails, trim the original and return it.
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path.Trim()); }
        catch { return path.Trim(); }
    }

    /// Is it a text file that is supported (=worthy of being opened and rendered)? Unknown extensions are also allowed as text.
    /// Excludes only executable files.
    public static bool IsSupported(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        foreach (var known in KnownExtensions)
        {
            if (string.Equals(ext, known, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // If the extension is not known, only obvious binary/executable files are filtered out.
        return ext.ToLowerInvariant() is not (".exe" or ".dll" or ".zip" or ".png" or ".jpg" or ".jpeg"
            or ".gif" or ".pdf" or ".mp3" or ".mp4" or ".bin" or ".db");
    }

    /// Format of linked notes. Text files are always treated as Markdown source — the editor can
    /// Take a path (IsMarkup) that can be written back to the file as is, and rich text XAML is mixed in the file.
    /// Blocks stored accidents at source. (Plain text looks almost the same when rendered in Markdown.)
    public static NoteContentFormat FormatFor(string path)
    {
        _ = path;
        return NoteContentFormat.Markdown;
    }

    /// Read the file text and last write time (UTC). The editor may lock the file, so it will retry a few times.
    public static async Task<(string Content, DateTime WriteUtc)> ReadAsync(string path)
    {
        IOException? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                // Automatic recognition of BOM (UTF-8/16); Even files locked by shared reading are read as much as possible.
                string text;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    text = await reader.ReadToEndAsync().ConfigureAwait(false);
                }
                var writeUtc = File.GetLastWriteTimeUtc(path);
                return (text, writeUtc);
            }
            catch (IOException ex)
            {
                last = ex;
                await Task.Delay(60).ConfigureAwait(false);
            }
        }
        throw last ?? new IOException($"Could not read '{path}'.");
    }

    /// Saves the text to the file as UTF-8 (no BOM) and returns a new LastWriteTimeUtc.
    /// If the directory does not exist, it is created. If the file was deleted, recreate it.
    public static async Task<DateTime> WriteAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await File.WriteAllTextAsync(path, content, encoding).ConfigureAwait(false);
        return File.GetLastWriteTimeUtc(path);
    }
}
