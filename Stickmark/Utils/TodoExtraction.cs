using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Stickmark.Utils;

/// The pure core of the unified to-do view: extract checkbox lines from PlainText/Markdown source;
/// Toggle by replacing only one mark character with an absolute offset (spec-2 §3.2).
///
/// Note: this is the only logic that transforms the original text, so the rules are strict:
/// - Do not use split/join — CRLF/LF mixed and trailing newlines are preserved in bytes.
/// - Contrast the current line with the RawLine held by the caller before the toggle (optimistic concurrency) — Notes in the meantime
/// If the line has changed due to editing, null is returned, and the caller recovers by re-collecting.
public static class TodoExtraction
{
    /// <param name="LineIndex">Line number based on 0 (split based on \n).</param>
    /// <param name="IsChecked">Whether [x]/[X].</param>
    /// <param name="Text">Text to display behind the checkbox (trimmed).</param>
    /// <param name="RawLine">Original line text with tail \r removed — for contrasting stale when toggled.</param>
    public readonly record struct TodoLine(int LineIndex, bool IsChecked, string Text, string RawLine);

    // `- [ ] text` / `* [x] ...` / `+ [X] ...`, allowing indentation (nesting).
    private static readonly Regex TaskLine = new(
        @"^(\s*)([-*+])\s+\[( |x|X)\]\s?(.*)$", RegexOptions.Compiled);

    /// Extract all checkbox lines from the source. Validation of target format (PlainText/Markdown only) is the responsibility of the caller.
    public static IReadOnlyList<TodoLine> ExtractTasks(string? source)
    {
        var result = new List<TodoLine>();
        if (string.IsNullOrEmpty(source)) return result;

        var lineIndex = 0;
        var start = 0;
        while (start <= source.Length)
        {
            var nl = source.IndexOf('\n', start);
            var end = nl < 0 ? source.Length : nl;
            var line = source[start..end].TrimEnd('\r');

            var m = TaskLine.Match(line);
            if (m.Success)
            {
                result.Add(new TodoLine(
                    lineIndex,
                    IsChecked: m.Groups[3].Value is "x" or "X",
                    Text: m.Groups[4].Value.Trim(),
                    RawLine: line));
            }

            if (nl < 0) break;
            start = nl + 1;
            lineIndex++;
        }
        return result;
    }

    /// If the lineIndex line matches expectedRaw, a new source is created with only one mark character ([ ]↔[x]) replaced.
    /// returns. If the line has changed, is out of range, or is not a checkbox line, null (caller recollection).
    public static string? ToggleLine(string? source, int lineIndex, string expectedRaw, bool check)
    {
        if (string.IsNullOrEmpty(source) || lineIndex < 0) return null;

        // Find the absolute offset from the start of the lineIndex line by \n scanning.
        var lineStart = 0;
        for (var i = 0; i < lineIndex; i++)
        {
            var nl = source.IndexOf('\n', lineStart);
            if (nl < 0) return null;                      // out of range
            lineStart = nl + 1;
        }
        var lineEnd = source.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = source.Length;

        var raw = source[lineStart..lineEnd].TrimEnd('\r');
        if (!string.Equals(raw, expectedRaw, StringComparison.Ordinal)) return null;   // stale

        var m = TaskLine.Match(raw);
        if (!m.Success) return null;

        var current = m.Groups[3].Value is "x" or "X";
        if (current == check) return source;              // Already desired state — no changes

        // Absolute offset of mark character (one character inside [ ]) = start of line + position within line of group 3.
        var markOffset = lineStart + m.Groups[3].Index;
        return string.Concat(
            source.AsSpan(0, markOffset),
            check ? "x" : " ",
            source.AsSpan(markOffset + 1));
    }
}
