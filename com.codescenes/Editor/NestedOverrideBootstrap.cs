#nullable enable
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// m-nested-props b7-t2 (specs/24-nested-prefab-overrides.md): closes the parse-time gap left by
    /// <c>BuilderParser.Facade.cs</c> — a nested (below-root) <c>Target</c> parses with
    /// <c>SubKey == default</c> ("source never carries a GUID/fileID", see
    /// <c>BuilderParser.Instance.cs</c>:303/347/376) while the live snapshot read
    /// (<see cref="PrefabInstanceProbe"/>) carries the REAL <c>SubKey</c>. Since
    /// <c>OverrideTarget.Equals</c> keys on <c>(SubKey, ComponentType)</c> only, an un-stamped desired
    /// target never equals its own live counterpart — materialize/diff/reconcile see a spurious
    /// [Set(0,0), Revert(real)] or drop+re-append. <see cref="Resolve"/> rewrites every nested target
    /// in <c>desired</c> with its live sub-object's real <c>SubKey</c> BEFORE that comparison ever
    /// happens, wired ONCE at the shared <see cref="DesiredModelLoader.Load"/> seam so both directions
    /// (Build and Sync) inherit it by default.
    /// </summary>
    internal static class NestedOverrideBootstrap
    {
        private static readonly PrefabInstanceKey UnresolvedSubKey = new();

        /// <summary>
        /// Rewrites <paramref name="desired"/> so every nested (<c>ChildPath != ""</c>) override,
        /// added/removed-component, removed-child <c>Target</c>, and every
        /// <see cref="AddedGameObject.Parent"/>, on each <see cref="PrefabInstanceNode"/> carries its
        /// live sub-object's real <see cref="PrefabInstanceKey"/>, resolved via the sidecar's
        /// <c>Kind="PrefabInstance"</c> entry + a <c>ChildPath</c> walk from the live instance root.
        /// An instance not yet live (no sidecar entry, no/unresolvable <c>GlobalObjectId</c> — the
        /// first-ever build before instantiation) is left untouched (<c>SubKey</c> stays default) with
        /// NO conflict. A selector that resolves to no live sub-object under a LIVE instance root is a
        /// LOCATED <paramref name="conflicts"/> entry — never a silent drop. Root targets
        /// (<c>ChildPath == ""</c>) and already-stamped targets are untouched (byte-unchanged).
        /// </summary>
        public static SceneModel Resolve(SceneModel desired, IdentityMap sidecar, out IReadOnlyList<Conflict> conflicts)
        {
            var entriesByLogicalId = sidecar.Entries.ToDictionary(e => e.LogicalId, e => e);
            var collected = new List<Conflict>();

            var roots = desired.Roots
                .Select(root => ResolveGameObject(root, entriesByLogicalId, collected))
                .ToArray();

            conflicts = collected;
            return desired with { Roots = roots };
        }

        private static GameObjectNode ResolveGameObject(
            GameObjectNode go,
            IReadOnlyDictionary<string, IdentityMapEntry> entriesByLogicalId,
            List<Conflict> conflicts)
        {
            var rebuilt = go with
            {
                Children = go.Children.Select(c => ResolveGameObject(c, entriesByLogicalId, conflicts)).ToArray(),
            };

            if (rebuilt is not PrefabInstanceNode instance || !HasNestedTargets(instance))
            {
                return rebuilt;
            }

            var liveRoot = ResolveLiveRoot(instance.LogicalId, entriesByLogicalId);
            if (liveRoot == null)
            {
                // Not yet live (no sidecar entry / empty or unresolvable GlobalObjectId) — the
                // executor's ChildPath fallback (b6-t1) applies the override on first build, and this
                // same Bootstrap stamps the real SubKey once the sidecar records it (b7-t1 read-back).
                return instance;
            }

            return instance with
            {
                Overrides = instance.Overrides
                    .Select(o => o with { Target = StampTarget(o.Target, instance.LogicalId, liveRoot, conflicts) })
                    .ToArray(),
                AddedComponents = instance.AddedComponents
                    .Select(a => a with { Target = StampTarget(a.Target, instance.LogicalId, liveRoot, conflicts) })
                    .ToArray(),
                RemovedComponents = instance.RemovedComponents
                    .Select(t => StampTarget(t, instance.LogicalId, liveRoot, conflicts))
                    .ToArray(),
                RemovedGameObjects = instance.RemovedGameObjects
                    .Select(t => StampTarget(t, instance.LogicalId, liveRoot, conflicts))
                    .ToArray(),
                AddedGameObjects = instance.AddedGameObjects
                    .Select(a => a with { Parent = StampTarget(a.Parent, instance.LogicalId, liveRoot, conflicts) })
                    .ToArray(),
            };
        }

        private static GameObject? ResolveLiveRoot(
            string instanceLogicalId, IReadOnlyDictionary<string, IdentityMapEntry> entriesByLogicalId)
        {
            if (!entriesByLogicalId.TryGetValue(instanceLogicalId, out var entry)
                || entry.Kind != "PrefabInstance"
                || string.IsNullOrEmpty(entry.GlobalObjectId)
                || !GlobalObjectId.TryParse(entry.GlobalObjectId, out var goid))
            {
                return null;
            }

            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(goid) as GameObject;
        }

        private static OverrideTarget StampTarget(
            OverrideTarget target, string instanceLogicalId, GameObject liveRoot, List<Conflict> conflicts)
        {
            if (target.ChildPath == "" || target.SubKey != UnresolvedSubKey)
            {
                // Root target (byte-unchanged) or already a resolved real key (idempotent re-run).
                return target;
            }

            var sub = PrefabInstanceProbe.ResolveSubObjectByChildPath(liveRoot, target.ChildPath);
            if (sub == null)
            {
                conflicts.Add(new Conflict
                {
                    Kind = ConflictKind.UnresolvedNestedSelector,
                    LogicalId = instanceLogicalId,
                    Reason = $"No live sub-object at '{target.ChildPath}' under prefab instance '{instanceLogicalId}'.",
                    Location = null,
                });
                return target;
            }

            return target with { SubKey = PrefabInstanceProbe.SubKeyOf(sub) };
        }

        private static bool HasNestedTargets(PrefabInstanceNode instance) =>
            instance.Overrides.Any(o => o.Target.ChildPath != "")
            || instance.AddedComponents.Any(a => a.Target.ChildPath != "")
            || instance.RemovedComponents.Any(t => t.ChildPath != "")
            || instance.RemovedGameObjects.Any(t => t.ChildPath != "")
            || instance.AddedGameObjects.Any(a => a.Parent.ChildPath != "");
    }
}
