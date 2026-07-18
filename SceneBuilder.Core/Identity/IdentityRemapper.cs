using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Identity
{
    // b1-t3: structural identity remap. Pre-order matches CURRENT GameObjects/Components
    // against PRIOR IdentityMap entries, parent-by-parent, so renames/reorders inherit the
    // prior GlobalObjectId instead of producing a spurious CreateObject + orphaned deletion.
    public static class IdentityRemapper
    {
        private static readonly List<IdentityMapEntry> EmptyEntries = new();

        public static IdentityMap Remap(SceneModel current, IdentityMap prior)
        {
            // b5-t3: a prior PrefabInstance entry is a first-class node kind alongside GameObject —
            // both live in the same match pool so a rebuilt instance inherits its prior
            // GlobalObjectId/PrefabKey/SourcePrefabGuid instead of orphaning the prior entry.
            var priorGameObjects = prior.Entries.Where(e => e.Kind == "GameObject" || e.Kind == "PrefabInstance").ToList();
            var priorRoots = priorGameObjects.Where(e => e.ParentLogicalId == null).ToList();

            var priorChildrenByParent = new Dictionary<string, List<IdentityMapEntry>>();
            foreach (var entry in priorGameObjects)
            {
                if (entry.ParentLogicalId == null)
                {
                    continue;
                }

                if (!priorChildrenByParent.TryGetValue(entry.ParentLogicalId, out var siblings))
                {
                    siblings = new List<IdentityMapEntry>();
                    priorChildrenByParent[entry.ParentLogicalId] = siblings;
                }

                siblings.Add(entry);
            }

            var priorComponentsByOwner = new Dictionary<string, List<IdentityMapEntry>>();
            foreach (var entry in prior.Entries)
            {
                if (entry.Kind != "Component" || entry.ParentLogicalId == null)
                {
                    continue;
                }

                if (!priorComponentsByOwner.TryGetValue(entry.ParentLogicalId, out var components))
                {
                    components = new List<IdentityMapEntry>();
                    priorComponentsByOwner[entry.ParentLogicalId] = components;
                }

                components.Add(entry);
            }

            var consumedPriorLogicalIds = new HashSet<string>();
            var output = new List<IdentityMapEntry>();

            MatchLevel(current.Roots, priorRoots, null, priorChildrenByParent, priorComponentsByOwner, consumedPriorLogicalIds, output);

            foreach (var entry in prior.Entries)
            {
                if (!consumedPriorLogicalIds.Contains(entry.LogicalId))
                {
                    output.Add(entry);
                }
            }

            return prior with { Entries = output.ToArray() };
        }

        private static void MatchLevel(
            GameObjectNode[] currentNodes,
            List<IdentityMapEntry> priorSiblings,
            string? currentParentLogicalId,
            Dictionary<string, List<IdentityMapEntry>> priorChildrenByParent,
            Dictionary<string, List<IdentityMapEntry>> priorComponentsByOwner,
            HashSet<string> consumedPriorLogicalIds,
            List<IdentityMapEntry> output)
        {
            var matches = MatchSiblings(currentNodes, priorSiblings, consumedPriorLogicalIds);

            for (var i = 0; i < currentNodes.Length; i++)
            {
                var node = currentNodes[i];
                var match = matches[i];
                var isPrefabInstance = node is PrefabInstanceNode;

                output.Add(new IdentityMapEntry
                {
                    LogicalId = node.LogicalId,
                    GlobalObjectId = match?.GlobalObjectId ?? "",
                    Kind = isPrefabInstance ? "PrefabInstance" : "GameObject",
                    ComponentType = null,
                    ParentLogicalId = currentParentLogicalId,
                    Name = node.Name,
                    SiblingIndex = i,
                    PrefabKey = isPrefabInstance ? match?.PrefabKey : null,
                    SourcePrefabGuid = isPrefabInstance ? match?.SourcePrefabGuid : null,
                });

                var priorComponents = match != null && priorComponentsByOwner.TryGetValue(match.LogicalId, out var components)
                    ? components
                    : EmptyEntries;
                MatchComponents(node.Components, priorComponents, node.LogicalId, consumedPriorLogicalIds, output);

                var priorChildren = match != null && priorChildrenByParent.TryGetValue(match.LogicalId, out var children)
                    ? children
                    : EmptyEntries;
                MatchLevel(node.Children, priorChildren, node.LogicalId, priorChildrenByParent, priorComponentsByOwner, consumedPriorLogicalIds, output);
            }
        }

        // Match priority, each side consumed at most once: (a) exact LogicalId equality;
        // (b) Name equality among still-unmatched; (c) SiblingIndex equality among still-unmatched.
        private static IdentityMapEntry?[] MatchSiblings(GameObjectNode[] currentNodes, List<IdentityMapEntry> priorSiblings, HashSet<string> consumedPriorLogicalIds)
        {
            var matches = new IdentityMapEntry?[currentNodes.Length];
            var remaining = new List<IdentityMapEntry>(priorSiblings);

            for (var i = 0; i < currentNodes.Length; i++)
            {
                var candidate = remaining.Find(e => e.LogicalId == currentNodes[i].LogicalId);
                if (candidate == null)
                {
                    continue;
                }

                matches[i] = candidate;
                remaining.Remove(candidate);
            }

            for (var i = 0; i < currentNodes.Length; i++)
            {
                if (matches[i] != null)
                {
                    continue;
                }

                var candidate = remaining.Find(e => e.Name == currentNodes[i].Name);
                if (candidate == null)
                {
                    continue;
                }

                matches[i] = candidate;
                remaining.Remove(candidate);
            }

            for (var i = 0; i < currentNodes.Length; i++)
            {
                if (matches[i] != null)
                {
                    continue;
                }

                var candidate = remaining.Find(e => e.SiblingIndex == i);
                if (candidate == null)
                {
                    continue;
                }

                matches[i] = candidate;
                remaining.Remove(candidate);
            }

            for (var i = 0; i < currentNodes.Length; i++)
            {
                if (matches[i] != null)
                {
                    consumedPriorLogicalIds.Add(matches[i]!.LogicalId);
                }
            }

            return matches;
        }

        // Matches current components to prior component entries under the matched owner by
        // (ComponentType, ordinal-within-that-type) — same scheme as
        // Differ.ComputeComponentKeys / BuilderParser.AssignComponentLogicalIds.
        private static void MatchComponents(
            ComponentData[] currentComponents,
            List<IdentityMapEntry> priorComponents,
            string ownerLogicalId,
            HashSet<string> consumedPriorLogicalIds,
            List<IdentityMapEntry> output)
        {
            var currentKeys = ComputeComponentKeys(currentComponents);
            var remaining = new List<IdentityMapEntry>(priorComponents);
            var remainingKeys = new List<(string TypeFullName, int Ordinal)>(ComputePriorComponentKeys(priorComponents));

            for (var i = 0; i < currentComponents.Length; i++)
            {
                var index = remainingKeys.IndexOf(currentKeys[i]);
                IdentityMapEntry? match = null;
                if (index >= 0)
                {
                    match = remaining[index];
                    remaining.RemoveAt(index);
                    remainingKeys.RemoveAt(index);
                    consumedPriorLogicalIds.Add(match.LogicalId);
                }

                output.Add(new IdentityMapEntry
                {
                    LogicalId = currentComponents[i].LogicalId,
                    GlobalObjectId = match?.GlobalObjectId ?? "",
                    Kind = "Component",
                    ComponentType = currentComponents[i].Type.FullName,
                    ParentLogicalId = ownerLogicalId,
                });
            }
        }

        private static (string TypeFullName, int Ordinal)[] ComputeComponentKeys(ComponentData[] components)
        {
            var keys = new (string TypeFullName, int Ordinal)[components.Length];
            var ordinalByType = new Dictionary<string, int>();
            for (var i = 0; i < components.Length; i++)
            {
                var typeFullName = components[i].Type.FullName;
                var ordinal = ordinalByType.TryGetValue(typeFullName, out var count) ? count : 0;
                ordinalByType[typeFullName] = ordinal + 1;
                keys[i] = (typeFullName, ordinal);
            }

            return keys;
        }

        private static (string TypeFullName, int Ordinal)[] ComputePriorComponentKeys(List<IdentityMapEntry> priorComponents)
        {
            var keys = new (string TypeFullName, int Ordinal)[priorComponents.Count];
            var ordinalByType = new Dictionary<string, int>();
            for (var i = 0; i < priorComponents.Count; i++)
            {
                var typeFullName = priorComponents[i].ComponentType ?? "";
                var ordinal = ordinalByType.TryGetValue(typeFullName, out var count) ? count : 0;
                ordinalByType[typeFullName] = ordinal + 1;
                keys[i] = (typeFullName, ordinal);
            }

            return keys;
        }
    }
}
