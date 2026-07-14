using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;

namespace SceneBuilder.Core.Parsing
{
    // Derives each node's LogicalId by priority (1: handle name, 2: explicit `.Id(...)`,
    // 3: synthesized `{parentLogicalId+"/"}{name}/{siblingIndex}`), extracted from BuilderParser
    // (b1-t7). For priority 3, reuses a persisted id from an existing IdentityMap instead of
    // recomputing a positional index, so a synthesized id survives sibling insertion/reordering.
    internal sealed class LogicalIdResolver
    {
        // Keyed by (parentLogicalId-or-"", name); each queue holds persisted LogicalIds for that
        // group ordered by ascending persisted sibling index, claimed in document order.
        private readonly Dictionary<(string ParentKey, string Name), Queue<string>> _claimable = new();

        public LogicalIdResolver(IdentityMap? existingMap)
        {
            if (existingMap == null)
            {
                return;
            }

            var groups = new Dictionary<(string ParentKey, string Name), List<(int Index, string LogicalId)>>();

            foreach (var entry in existingMap.Entries)
            {
                if (!TryParseSynthesized(entry.LogicalId, entry.ParentLogicalId, out var name, out var index))
                {
                    continue;
                }

                var key = (entry.ParentLogicalId ?? string.Empty, name);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<(int, string)>();
                    groups[key] = list;
                }

                list.Add((index, entry.LogicalId));
            }

            foreach (var (key, list) in groups)
            {
                list.Sort((a, b) => a.Index.CompareTo(b.Index));
                _claimable[key] = new Queue<string>(list.Select(x => x.LogicalId));
            }
        }

        public string Resolve(string? handleName, string? explicitId, string? parentLogicalId, string name, int siblingIndex)
        {
            if (!string.IsNullOrEmpty(handleName))
            {
                return handleName!;
            }

            if (!string.IsNullOrEmpty(explicitId))
            {
                return explicitId!;
            }

            var key = (parentLogicalId ?? string.Empty, name);
            if (_claimable.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                return queue.Dequeue();
            }

            return Synthesize(parentLogicalId, name, siblingIndex);
        }

        // Shared synthesized-id formula: {parentLogicalId+"/"}{name}/{siblingIndex}. Reused by
        // Resolve's priority-3 fallback above and by the Reconciler (b2) to predict a created
        // node's id; TryParseSynthesized below remains its exact inverse.
        internal static string Synthesize(string? parentLogicalId, string name, int siblingIndex)
        {
            var prefix = string.IsNullOrEmpty(parentLogicalId) ? string.Empty : parentLogicalId + "/";
            return $"{prefix}{name}/{siblingIndex}";
        }

        // Recognizes the synthesized-id shape `{parentLogicalId+"/"}{name}/{index}`: the last
        // '/'-segment must parse as an int, and the remaining prefix must equal
        // `parentLogicalId + "/"` (or be empty when parentLogicalId is null/empty).
        // Internal so ConflictDetector (Reconcile) can reuse the same recognizer instead of
        // reinventing the shape check.
        internal static bool TryParseSynthesized(string logicalId, string? parentLogicalId, out string name, out int index)
        {
            name = "";
            index = 0;

            var lastSlash = logicalId.LastIndexOf('/');
            if (lastSlash < 0 || lastSlash == logicalId.Length - 1)
            {
                return false;
            }

            if (!int.TryParse(logicalId[(lastSlash + 1)..], out index))
            {
                return false;
            }

            var expectedPrefix = string.IsNullOrEmpty(parentLogicalId) ? string.Empty : parentLogicalId + "/";
            if (!logicalId.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            name = logicalId[expectedPrefix.Length..lastSlash];
            return name.Length > 0;
        }
    }
}
