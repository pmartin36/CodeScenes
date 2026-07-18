using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;

namespace SceneBuilder.Core.Reconcile
{
    // m6-b4-t1: scene->code append of a prefab-instance root. Split out of ReconcilerAppends.cs
    // per the project's file-size budget (Reconciler.cs is at 854 lines) — third partial-class
    // file, no visibility change.
    public static partial class Reconciler
    {
        // Mirrors the plain-node branch in DetectAppends (index/parent-handle resolution,
        // headsHandle, AddedEntry shape) minus child recursion (a prefab instance's internal
        // hierarchy is out of scope — M10) and minus the Tag/Layer/Active/Static flag fields
        // (InstanceHandle exposes no such calls).
        private static void HandleInstanceNode(
            SnapshotNode node,
            int siblingIndex,
            string? parentLogicalId,
            bool parentIsMapped,
            SceneModel expected,
            IReadOnlyDictionary<string, string> goidToLogicalId,
            IReadOnlyDictionary<string, GameObjectNode> modelByLogicalId,
            HashSet<string> reserved,
            IReadOnlyDictionary<string, string> prefabPathByGuid,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            Dictionary<string, int> nextIndexByParentKey,
            List<SourceEdit> edits,
            List<IdentityMapEntry> addedEntries,
            List<Conflict> conflicts,
            SnapshotNode[] siblings)
        {
            var isMapped = !string.IsNullOrEmpty(node.GlobalObjectId) && goidToLogicalId.ContainsKey(node.GlobalObjectId);
            if (isMapped || !parentIsMapped)
            {
                // Resolves on a later sync, mirroring the plain leaf case (a create candidate under
                // an unmapped parent is stranded until the parent itself is appended).
                return;
            }

            var parentKey = parentLogicalId ?? string.Empty;
            if (!nextIndexByParentKey.TryGetValue(parentKey, out var index))
            {
                index = parentLogicalId == null
                    ? expected.Roots.Length
                    : modelByLogicalId.TryGetValue(parentLogicalId, out var parentModel)
                        ? parentModel.Children.Length
                        : 0;
            }

            nextIndexByParentKey[parentKey] = index + 1;

            // The parent's receiver, via the ONE handle-introduction path (shared with the plain
            // append/reparent/component-attach emission), exactly as the plain branch does.
            var (parentHandle, introduceParentHandle) = resolveOwnerHandle(parentLogicalId);

            if (node.SourcePrefabGuid == null || !prefabPathByGuid.TryGetValue(node.SourcePrefabGuid, out var path))
            {
                // Never emit a statement with no path. Ensuring the source prefab is in
                // identityMap.Assets before Reconcile runs is the adapter's job (b5-t2/b5-t3).
                var provisionalId = LogicalIdResolver.Synthesize(parentHandle, node.Name, index);
                conflicts.Add(new Conflict
                {
                    Kind = ConflictKind.DanglingReference,
                    LogicalId = provisionalId,
                    GlobalObjectId = node.GlobalObjectId,
                    Reason = $"Prefab instance '{node.Name}' references source prefab guid " +
                        $"'{node.SourcePrefabGuid}', which has no known asset path in the identity map.",
                });

                return;
            }

            // Two same-prefab instances share the stem Name -> head a `var` handle so the
            // duplicate-name healer (EnsureNoAmbiguousDuplicateNames) never has to inject an
            // unsupported positional rewrite into an Instance statement.
            var headsHandle = siblings.Count(n => n.Name == node.Name) > 1;

            string newLogicalId;
            string? ownHandle = null;

            if (headsHandle)
            {
                ownHandle = HandleNaming.Derive(node.Name, reserved);
                reserved.Add(ownHandle);
                newLogicalId = ownHandle;
            }
            else
            {
                newLogicalId = LogicalIdResolver.Synthesize(parentHandle, node.Name, index);
            }

            edits.Add(new AppendStatement
            {
                NewLogicalId = newLogicalId,
                ParentAnchor = parentLogicalId,
                NewSiblingIndex = siblingIndex,
                Name = node.Name,
                SourcePrefabPath = path,
                Transform = node.Transform != new TransformData() ? node.Transform : null,
                Active = null,
                Tag = null,
                Layer = null,
                IsStatic = null,
                Handle = ownHandle,
                ParentHandle = parentHandle,
                IntroduceParentHandle = introduceParentHandle,
            });

            addedEntries.Add(new IdentityMapEntry
            {
                LogicalId = newLogicalId,
                GlobalObjectId = node.GlobalObjectId,
                Kind = "PrefabInstance",
                ParentLogicalId = parentHandle,
                Name = node.Name,
                SourcePrefabGuid = node.SourcePrefabGuid,
                PrefabKey = node.PrefabKey,
            });
        }
    }
}
