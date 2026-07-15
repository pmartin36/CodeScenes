#nullable enable
using System;
using System.Collections.Generic;
using SceneBuilder.Core.Identity;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Folds asset entries into the sidecar <c>Assets[]</c> cache, keyed by GUID, and reports the REAL
    /// delta. Shared by <see cref="SceneBuilderBuild"/> and <see cref="SceneBuilderSync"/>, which
    /// previously carried a copy each.
    /// </summary>
    /// <remarks>
    /// <see cref="Result.ChangedCount"/> is the point. Callers used to report (and gate writes on) the
    /// count of HARVESTED refs — every asset ref the walk encountered — which is not a delta at all: a
    /// scene that merely CONTAINS a material harvests one on every pass and so reported "+1 asset(s)"
    /// and rewrote the sidecar forever. Only an entry that is genuinely NEW, or whose
    /// <c>LastKnownPath</c>/<c>TypeHint</c> actually MOVED, counts here.
    /// </remarks>
    internal static class AssetCacheMerge
    {
        internal readonly struct Result
        {
            internal Result(AssetEntry[] merged, int changedCount)
            {
                Merged = merged;
                ChangedCount = changedCount;
            }

            /// <summary>The merged cache. Stable order: existing entries first, in place, then new ones.</summary>
            internal AssetEntry[] Merged { get; }

            /// <summary>
            /// How many entries this merge actually ADDED or CHANGED. Zero means <see cref="Merged"/> is
            /// equivalent to what was passed in, so nothing needs persisting.
            /// </summary>
            internal int ChangedCount { get; }
        }

        /// <summary>
        /// Merges <paramref name="incoming"/> into <paramref name="existing"/> by GUID. An incoming entry
        /// wins over a cached one (its <c>LastKnownPath</c> reflects the current project layout), so a
        /// moved/renamed asset's path is refreshed — but ONLY a genuine add/change is counted.
        /// </summary>
        internal static Result Merge(IReadOnlyList<AssetEntry>? existing, IReadOnlyList<AssetEntry>? incoming)
        {
            var current = existing ?? Array.Empty<AssetEntry>();
            if (incoming == null || incoming.Count == 0)
            {
                return new Result(AsArray(current), 0);
            }

            // Explicit ordered list + index map: the merged cache must be BYTE-stable across runs, so a
            // re-merge that changes nothing serializes identically and the write-if-changed path can
            // skip it. Relying on Dictionary.Values enumeration order for that would be a coin flip.
            var ordered = new List<AssetEntry>(current);
            var indexByGuid = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < ordered.Count; i++)
            {
                // A duplicate GUID in the persisted cache: keep the FIRST, so the merge is a function of
                // its input and never oscillates between two rows on successive runs.
                if (!string.IsNullOrEmpty(ordered[i].Guid) && !indexByGuid.ContainsKey(ordered[i].Guid))
                {
                    indexByGuid[ordered[i].Guid] = i;
                }
            }

            var changed = 0;
            foreach (var entry in incoming)
            {
                if (string.IsNullOrEmpty(entry.Guid))
                {
                    continue;
                }

                if (!indexByGuid.TryGetValue(entry.Guid, out var index))
                {
                    indexByGuid[entry.Guid] = ordered.Count;
                    ordered.Add(entry);
                    changed++;
                    continue;
                }

                if (!SameFacts(ordered[index], entry))
                {
                    ordered[index] = entry;
                    changed++;
                }
            }

            return new Result(ordered.ToArray(), changed);
        }

        // GUID is identity; LastKnownPath/TypeHint are the facts a merge can refresh. Equal facts =>
        // nothing to persist, no matter how many times the ref was harvested.
        private static bool SameFacts(AssetEntry a, AssetEntry b) =>
            string.Equals(a.LastKnownPath, b.LastKnownPath, StringComparison.Ordinal)
            && string.Equals(a.TypeHint, b.TypeHint, StringComparison.Ordinal);

        private static AssetEntry[] AsArray(IReadOnlyList<AssetEntry> list)
        {
            if (list is AssetEntry[] array)
            {
                return array;
            }

            var copy = new AssetEntry[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                copy[i] = list[i];
            }

            return copy;
        }
    }
}
