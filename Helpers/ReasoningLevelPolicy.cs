using System;
using System.Collections.Generic;
using System.Linq;

namespace LenSa.Core.Helpers
{
    /// <summary>Pure normalization and model-to-model coercion for server reasoning level catalogs.</summary>
    public static class ReasoningLevelPolicy
    {
        private static readonly string[] LegacyLevels = { "minimal", "low", "medium", "high" };

        /// <summary>
        /// Normalizes a published catalog while preserving server order. A missing field means an old
        /// server and uses the legacy levels; an explicitly empty list remains empty.
        /// </summary>
        public static IReadOnlyList<string> Normalize(string?[]? published)
        {
            if (published is null) return Array.AsReadOnly((string[])LegacyLevels.Clone());

            var levels = new List<string>(published.Length);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in published)
            {
                var token = value?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(token) || token == "none" || !seen.Add(token))
                    continue;
                levels.Add(token);
            }

            return levels.AsReadOnly();
        }

        /// <summary>
        /// Keeps a supported selection or moves it to the nearest lower available rank. If an
        /// unknown selected token was in the previous catalog, its old position supplies its rank.
        /// If it was in neither catalog, the lower middle of the new list is the deterministic fallback.
        /// </summary>
        public static string? Coerce(
            string? selected,
            IReadOnlyList<string> priorLevels,
            IReadOnlyList<string> nextLevels)
        {
            ArgumentNullException.ThrowIfNull(priorLevels);
            ArgumentNullException.ThrowIfNull(nextLevels);
            if (nextLevels.Count == 0) return null;

            var next = Normalize(nextLevels.Cast<string?>().ToArray());
            if (next.Count == 0) return null;

            var token = selected?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(token) || token is "off" or "none") return next[0];
            var exact = next.FirstOrDefault(level => string.Equals(level, token, StringComparison.Ordinal));
            if (exact is not null) return exact;

            var prior = Normalize(priorLevels.Cast<string?>().ToArray());
            var selectedRank = KnownRank(token) ?? IndexOf(prior, token);
            if (selectedRank is null)
                return next[(next.Count - 1) / 2];

            var best = -1;
            var bestRank = int.MinValue;
            for (var i = 0; i < next.Count; i++)
            {
                var rank = KnownRank(next[i]) ?? i;
                if (rank <= selectedRank.Value && rank > bestRank)
                {
                    best = i;
                    bestRank = rank;
                }
            }

            return next[best >= 0 ? best : 0];
        }

        private static int? IndexOf(IReadOnlyList<string> values, string token)
        {
            for (var i = 0; i < values.Count; i++)
                if (string.Equals(values[i], token, StringComparison.Ordinal))
                    return i;
            return null;
        }

        private static int? KnownRank(string token) => token switch
        {
            "minimal" => 0,
            "low" => 1,
            "medium" => 2,
            "high" => 3,
            "xhigh" => 4,
            "max" => 5,
            _ => null
        };
    }
}
