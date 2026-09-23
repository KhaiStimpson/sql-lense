using System;
using System.Collections.Generic;

namespace SqlLense.Text
{
    /// <summary>Finds "did you mean" candidates using case-insensitive optimal string alignment distance.</summary>
    public static class NameSuggester
    {
        public static IReadOnlyList<string> Suggest(string name, IEnumerable<string> candidates, int maxResults = 3)
        {
            if (string.IsNullOrEmpty(name))
            {
                return Array.Empty<string>();
            }

            // Allow roughly one edit per three characters, at least one and at most three.
            var threshold = Math.Min(3, Math.Max(1, name.Length / 3));
            var best = new List<(string Name, int Distance)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                if (candidate.Length == 0 || Math.Abs(candidate.Length - name.Length) > threshold || !seen.Add(candidate))
                {
                    continue;
                }

                var distance = Distance(name, candidate, threshold);
                if (distance <= threshold && !string.Equals(candidate, name, StringComparison.Ordinal))
                {
                    best.Add((candidate, distance));
                }
            }

            if (best.Count == 0)
            {
                return Array.Empty<string>();
            }

            best.Sort((a, b) => a.Distance != b.Distance
                ? a.Distance.CompareTo(b.Distance)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            var count = Math.Min(maxResults, best.Count);
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = best[i].Name;
            }

            return result;
        }

        /// <summary>Optimal string alignment distance, bailing out early once it exceeds <paramref name="max"/>.</summary>
        internal static int Distance(string a, string b, int max)
        {
            var n = a.Length;
            var m = b.Length;
            var prev2 = new int[m + 1];
            var prev = new int[m + 1];
            var curr = new int[m + 1];
            for (var j = 0; j <= m; j++)
            {
                prev[j] = j;
            }

            for (var i = 1; i <= n; i++)
            {
                curr[0] = i;
                var rowMin = curr[0];
                var ca = char.ToUpperInvariant(a[i - 1]);
                for (var j = 1; j <= m; j++)
                {
                    var cb = char.ToUpperInvariant(b[j - 1]);
                    var cost = ca == cb ? 0 : 1;
                    var value = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                    if (i > 1 && j > 1 && ca == char.ToUpperInvariant(b[j - 2]) && char.ToUpperInvariant(a[i - 2]) == cb)
                    {
                        value = Math.Min(value, prev2[j - 2] + 1);
                    }

                    curr[j] = value;
                    if (value < rowMin)
                    {
                        rowMin = value;
                    }
                }

                if (rowMin > max)
                {
                    return max + 1;
                }

                var tmp = prev2;
                prev2 = prev;
                prev = curr;
                curr = tmp;
            }

            return prev[m];
        }
    }
}
