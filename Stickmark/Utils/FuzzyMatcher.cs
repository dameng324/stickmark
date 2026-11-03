using System;
using System.Collections.Generic;

namespace Stickmark.Utils;

/// Fuzzy (discontinuous subsequence) matching for fast converters (Ctrl+P) only. Existing token-wise substring AND
/// Unlike <see cref="SearchMatcher"/>, each token is matched as long as it is a subsequence of the target string.
public static class FuzzyMatcher
{
    public readonly record struct FuzzyResult(bool Matched, double Score, IReadOnlyList<int> Positions);

    private const double ConsecutiveRunBonus = 3.0;
    private const double WordStartBonus = 4.0;
    private const double TitleWeight = 2.0;
    private const double FirstMatchPenaltyPerIndex = 0.1;

    /// Each query token tokenized as a space must match (AND) a subsequence of <paramref name="target"/>.
    /// If <paramref name="titleLength"/> is given, weight is given to the matching letter of the previous index (title area).
    public static FuzzyResult Match(string target, string query, int titleLength = -1)
    {
        target ??= string.Empty;
        var tokens = Tokenize(query);
        if (tokens.Count == 0)
        {
            return new FuzzyResult(true, 0, Array.Empty<int>());
        }

        var allPositions = new SortedSet<int>();
        double score = 0;
        int? firstMatchIndex = null;

        foreach (var token in tokens)
        {
            if (!TryMatchSubsequence(target, token, out var positions))
            {
                return new FuzzyResult(false, 0, Array.Empty<int>());
            }

            for (var i = 0; i < positions.Count; i++)
            {
                var pos = positions[i];
                allPositions.Add(pos);

                var inTitle = titleLength >= 0 && pos < titleLength;
                score += inTitle ? TitleWeight : 1.0;

                if (i > 0 && positions[i] == positions[i - 1] + 1)
                {
                    score += ConsecutiveRunBonus;
                }

                if (IsWordStart(target, pos))
                {
                    score += WordStartBonus;
                }

                if (firstMatchIndex is null || pos < firstMatchIndex)
                {
                    firstMatchIndex = pos;
                }
            }
        }

        if (firstMatchIndex is { } idx)
        {
            score -= FirstMatchPenaltyPerIndex * idx;
        }

        return new FuzzyResult(true, score, new List<int>(allPositions));
    }

    private static IReadOnlyList<string> Tokenize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// Greedy left → right subsequence matching: Find each letter of the token in order starting from the earliest position in target.
    private static bool TryMatchSubsequence(string target, string token, out List<int> positions)
    {
        positions = new List<int>(token.Length);
        var ti = 0;
        foreach (var ch in token)
        {
            var found = false;
            for (; ti < target.Length; ti++)
            {
                if (char.ToUpperInvariant(target[ti]) == char.ToUpperInvariant(ch))
                {
                    positions.Add(ti);
                    ti++;
                    found = true;
                    break;
                }
            }
            if (!found) return false;
        }
        return true;
    }

    /// Space·'#'·After the punctuation mark (or at the beginning of the string) is treated as the beginning of a word.
    private static bool IsWordStart(string target, int index)
    {
        if (index <= 0) return true;
        var prev = target[index - 1];
        return char.IsWhiteSpace(prev) || prev == '#' || char.IsPunctuation(prev);
    }
}
