using System;
using System.Collections.Generic;
using System.Linq;
using Stickmark.ViewModels;

namespace Stickmark.Utils;

/// Multiple tokens (space separated) AND matching + score (title > tags > body) + create highlight based on match location.
public static class SearchMatcher
{
    public readonly record struct MatchResult(double Score, bool Matched);

    public static IReadOnlyList<string> Tokenize(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// Calculate overall note score. Matched=true if all tokens match somewhere (title/tag/body).
    public static MatchResult Score(string title, string tagsLine, string body, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return new MatchResult(0, true);

        double total = 0;
        foreach (var token in tokens)
        {
            var titleHits = CountOccurrences(title, token);
            var tagHits = CountOccurrences(tagsLine, token);
            var bodyHits = CountOccurrences(body, token);
            if (titleHits == 0 && tagHits == 0 && bodyHits == 0)
            {
                return new MatchResult(0, false);
            }

            // Weight: 1 title match = 6 body matches. The tag is slightly above the text.
            total += titleHits * 6.0 + tagHits * 2.5 + bodyHits;

            // Bonus (but not too strong) for matches that start exactly on a word boundary.
            if (StartsWordMatch(title, token)) total += 4.0;
            else if (StartsWordMatch(tagsLine, token)) total += 1.5;
            else if (StartsWordMatch(body, token)) total += 0.5;
        }
        return new MatchResult(total, true);
    }

    public static IReadOnlyList<HighlightSegment> Highlight(string text, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrEmpty(text) || tokens.Count == 0)
        {
            return new[] { new HighlightSegment(text ?? string.Empty, false) };
        }

        var ranges = new List<(int Start, int Length)>();
        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token)) continue;
            var i = 0;
            while (i <= text.Length - token.Length)
            {
                var idx = text.IndexOf(token, i, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                ranges.Add((idx, token.Length));
                i = idx + token.Length;
            }
        }
        if (ranges.Count == 0)
        {
            return new[] { new HighlightSegment(text, false) };
        }

        // Merge overlapping or adjacent ranges.
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int End)>();
        foreach (var (start, length) in ranges)
        {
            var end = start + length;
            if (merged.Count > 0 && start <= merged[^1].End)
            {
                if (end > merged[^1].End)
                {
                    merged[^1] = (merged[^1].Start, end);
                }
            }
            else
            {
                merged.Add((start, end));
            }
        }

        var segments = new List<HighlightSegment>();
        var cursor = 0;
        foreach (var (start, end) in merged)
        {
            if (start > cursor)
            {
                segments.Add(new HighlightSegment(text.Substring(cursor, start - cursor), false));
            }
            segments.Add(new HighlightSegment(text.Substring(start, end - start), true));
            cursor = end;
        }
        if (cursor < text.Length)
        {
            segments.Add(new HighlightSegment(text.Substring(cursor), false));
        }
        return segments;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var i = 0;
        while (i <= haystack.Length - needle.Length)
        {
            var idx = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            count++;
            i = idx + needle.Length;
        }
        return count;
    }

    private static bool StartsWordMatch(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
        var idx = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        if (idx == 0) return true;
        return !char.IsLetterOrDigit(haystack[idx - 1]);
    }
}
