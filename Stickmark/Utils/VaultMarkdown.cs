using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Stickmark.Models;

namespace Stickmark.Utils;

/// Converts a note to and from a single `.md` file in a vault (folder) — the core primitive of 2.0 vault mode.
/// Contains meta (id, title, format, color, tags, date) required for round trip in human-readable text + YAML front matter.
///
/// Rules:
/// - Markdown/HTML/PlainText notes are round-tripped without loss because the text is the same as the original source.
/// - RichTextXaml notes are saved as PlainText to keep the vault human-readable (formatting loss).
/// - Hand-crafted `.md` files without front matter are also generously read (given a new id, considered Markdown).
public static class VaultMarkdown
{
    public static string ToMarkdown(Note note)
    {
        var (format, body) = note.Format == NoteContentFormat.RichTextXaml
            ? (NoteContentFormat.PlainText, note.PlainText ?? string.Empty)
            : (note.Format, note.Content ?? string.Empty);

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("id: ").Append(note.Id.ToString()).Append('\n');
        sb.Append("title: ").Append(YamlScalar(note.Title)).Append('\n');
        sb.Append("format: ").Append(format).Append('\n');
        sb.Append("color: ").Append(note.Color).Append('\n');
        if (note.Tags is { Count: > 0 })
        {
            sb.Append("tags: [").Append(string.Join(", ", note.Tags.Select(YamlScalar))).Append("]\n");
        }
        sb.Append("created: ").Append(note.CreatedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("modified: ").Append(note.ModifiedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("---\n\n");
        sb.Append(body.Replace("\r\n", "\n").TrimEnd()).Append('\n');
        return sb.ToString();
    }

    /// Parse `.md` text into notes. Even if the front matter is missing or incomplete, I do my best to make notes.
    public static Note FromMarkdown(string content, string? fallbackTitle = null) =>
        FromMarkdown(content, fallbackTitle, out _);

    /// <paramref name="hadFrontmatterId"/>: Whether the frontmatter has a valid id — if not, the caller
    /// (Volt Load) provides a stable ID based on the file name to prevent the ID from changing every time it is reloaded.
    public static Note FromMarkdown(string content, string? fallbackTitle, out bool hadFrontmatterId)
    {
        content = (content ?? string.Empty).Replace("\r\n", "\n");
        var front = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = content;

        // Frontmatter: Only when the first line is "---" and then closed with "---".
        if (content.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = content.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end >= 0)
            {
                var block = content.Substring(4, end - 4);
                foreach (var line in block.Split('\n'))
                {
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    var key = line[..idx].Trim();
                    var val = line[(idx + 1)..].Trim();
                    if (key.Length > 0) front[key] = val;
                }
                // The body begins with the line following the closing "---".
                var after = end + 4; // "\n---"
                var nl = content.IndexOf('\n', after);
                body = nl >= 0 ? content[(nl + 1)..] : string.Empty;
            }
        }

        var format = front.TryGetValue("format", out var f) && Enum.TryParse<NoteContentFormat>(f, true, out var fmt)
            ? fmt
            :NoteContentFormat.Markdown; // The default vault file is Markdown
        // Since ToMarkdown writes the body as TrimEnd()+'\n', parsing is also done in the same regular form —
        // The round trip is idempotent, so a reload immediately after a save does not create a ghost change (diff).
        var trimmedBody = body.TrimStart('\n').TrimEnd();

        var note = new Note
        {
            Content = trimmedBody,
            Format = format,
            Color = front.TryGetValue("color", out var c) && Enum.TryParse<NoteColor>(c, true, out var col)
                ? col : NoteColor.Yellow,
            CreatedAt = ParseDate(front, "created") ?? DateTime.UtcNow,
            ModifiedAt = ParseDate(front, "modified") ?? DateTime.UtcNow,
        };

        hadFrontmatterId = false;
        if (front.TryGetValue("id", out var idStr) && Guid.TryParse(idStr, out var id))
        {
            note.Id = id;
            hadFrontmatterId = true;
        }
        // else: Note.Id default (new Guid) — Vault load appends a stable id based on the file name with hadFrontmatterId.

        note.PlainText = TextExtraction.ToPlainText(trimmedBody, format);
        // If the front matter has a title key (including an empty value), its value is authentic — the empty title is used as the file name.
        // Disguise so that it doesn't create a ghost change on each round trip. Derived from body/file name only when there is no key.
        note.Title = front.TryGetValue("title", out var rawTitle)
            ? UnquoteYaml(rawTitle) ?? string.Empty
            : (TextExtraction.DeriveTitle(note.PlainText) is { Length: > 0 } d ? d : (fallbackTitle ?? string.Empty));
        note.Tags = ParseTags(front.GetValueOrDefault("tags"), note.PlainText);
        return note;
    }

    private static List<string> ParseTags(string? tagsLine, string plainText)
    {
        // If there are frontmatter tags, extract them. If not, extract #tags from the text.
        if (!string.IsNullOrWhiteSpace(tagsLine))
        {
            var inner = tagsLine.Trim().TrimStart('[').TrimEnd(']');
            var list = inner.Split(',')
                .Select(s => UnquoteYaml(s.Trim()))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.TrimStart('#'))
                .ToList();
            if (list.Count > 0) return list;
        }
        return TextExtraction.ExtractTags(plainText).ToList();
    }

    private static DateTime? ParseDate(IDictionary<string, string> front, string key) =>
        front.TryGetValue(key, out var s) &&
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;

    private static readonly char[] YamlSpecials =
        { ':', '#', '"', '\'', '[', ']', '{', '}', ',', '&', '*', '!', '|', '>', '%', '@', '`' };

    private static string YamlScalar(string? value)
    {
        value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
        if (value.Length == 0) return "\"\"";
        var needsQuote = value[0] == ' ' || value[^1] == ' ' || value.IndexOfAny(YamlSpecials) >= 0;
        return needsQuote ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : value;
    }

    private static string? UnquoteYaml(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return value;
    }
}
