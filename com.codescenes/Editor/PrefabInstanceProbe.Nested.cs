#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Core.Model;
using UnityAddedGameObject = UnityEditor.SceneManagement.AddedGameObject;
using UnityRemovedGameObject = UnityEditor.SceneManagement.RemovedGameObject;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// m-nested-props b5-t2 (specs/24-nested-prefab-overrides.md): undoes the M6/M10 opaque-collapse
    /// for BELOW-ROOT targets. <see cref="BuildSourceToLiveMap"/> is the only way to go from a
    /// <see cref="PropertyModification.target"/> (a SOURCE-asset object) to its LIVE sub-object inside
    /// the instance — there is no direct source-&gt;live Unity API. Built LAZILY (see
    /// <c>PrefabInstanceProbe.Overrides.cs</c>'s <c>Lazy&lt;&gt;</c> usage) so a plain/unmodified
    /// instance (the per-keystroke common case) pays zero descent cost.
    /// </summary>
    internal static partial class PrefabInstanceProbe
    {
        /// <summary>
        /// Descends every transform UNDER <paramref name="root"/> (excluding the root itself) and maps
        /// each descendant's SOURCE correspondence — both the GameObject and every component on it —
        /// to the LIVE GameObject that owns it.
        /// </summary>
        private static Dictionary<UnityEngine.Object, GameObject> BuildSourceToLiveMap(GameObject root)
        {
            var map = new Dictionary<UnityEngine.Object, GameObject>();
            var t = root.transform;
            for (var i = 0; i < t.childCount; i++)
            {
                MapDescendant(t.GetChild(i).gameObject, map);
            }

            return map;
        }

        private static void MapDescendant(GameObject go, Dictionary<UnityEngine.Object, GameObject> map)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (source != null)
            {
                map[source] = go;
            }

            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                var componentSource = PrefabUtility.GetCorrespondingObjectFromSource(component);
                if (componentSource != null)
                {
                    map[componentSource] = go;
                }
            }

            var t = go.transform;
            for (var i = 0; i < t.childCount; i++)
            {
                MapDescendant(t.GetChild(i).gameObject, map);
            }
        }

        /// <summary>
        /// The durable pair-key for a GameObject — identical derivation to the root-key at
        /// <see cref="ReadInstanceRoot"/>, reused (not re-invented) for any live OR asset-side
        /// GameObject (an asset object's <see cref="GlobalObjectId"/> is its durable source fileID).
        /// </summary>
        private static PrefabInstanceKey SubKeyOf(GameObject go)
        {
            var goid = GlobalObjectId.GetGlobalObjectIdSlow(go);
            return new PrefabInstanceKey { TargetPrefabId = goid.targetPrefabId, TargetObjectId = goid.targetObjectId };
        }

        /// <summary>
        /// m-nested-props b5-t3 (specs/24-nested-prefab-overrides.md behavior #12 / confirmation #7):
        /// the authoritative path-&gt;live disambiguation fallback. Resolves a "/"-joined ChildPath
        /// (as produced by <see cref="RelativePath"/>) from the OUTERMOST instance root to the live
        /// sub-object it names, even at depth&gt;=2 where a bare live pair-key could theoretically
        /// collide. Consumed by the write side (b6-t1) for SubKey-&gt;live resolution.
        /// </summary>
        internal static GameObject? ResolveSubObjectByChildPath(GameObject outermostRoot, string childPath)
        {
            return string.IsNullOrEmpty(childPath)
                ? outermostRoot
                : outermostRoot.transform.Find(childPath)?.gameObject;
        }

        /// <summary>
        /// m-nested-props b5-t3 (specs/24-nested-prefab-overrides.md behavior #12 / confirmation #7):
        /// the correspondence-chain fallback, mirroring PrefabHierarchyReader.cs's
        /// GetCorrespondingObjectFromSource -&gt; asset path -&gt; GetCorrespondingObjectFromSourceAtPath
        /// idiom. Resolves the SOURCE object for a live sub-object inside its owning (possibly nested)
        /// prefab asset — the write-side PropertyModification.target builder for a nested-prefab
        /// target (spec behavior #3).
        /// </summary>
        internal static UnityEngine.Object? ResolveSourceAtPath(GameObject liveSubObject)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(liveSubObject);
            if (source == null)
            {
                return null;
            }

            var sourcePath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(sourcePath))
            {
                return source;
            }

            return PrefabUtility.GetCorrespondingObjectFromSourceAtPath(liveSubObject, sourcePath) ?? source;
        }

        // PrefabUtility.GetAddedGameObjects(root) -> AddedGameObject[] (SnapshotNode.AddedGameObjects).
        // instanceGameObject is already LIVE — Parent.SubKey is derived directly (no source->live map
        // needed here).
        private static AddedGameObject[] ReadAddedGameObjects(GameObject root, Func<UnityEngine.Object, string?>? resolveSceneRef)
        {
            var added = PrefabUtility.GetAddedGameObjects(root);
            if (added == null || added.Count == 0)
            {
                return Array.Empty<AddedGameObject>();
            }

            var result = new List<AddedGameObject>();
            foreach (UnityAddedGameObject entry in added)
            {
                var live = entry.instanceGameObject;
                if (live == null)
                {
                    continue;
                }

                var parent = live.transform.parent;
                var parentTarget = (parent == null || parent == root.transform)
                    ? new OverrideTarget()
                    : new OverrideTarget
                    {
                        SubKey = SubKeyOf(parent.gameObject),
                        ComponentType = "",
                        ChildPath = RelativePath(root, parent.gameObject),
                    };

                result.Add(new AddedGameObject
                {
                    Parent = parentTarget,
                    Node = ReadAddedChildNode(live, resolveSceneRef),
                });
            }

            result.Sort((x, y) =>
            {
                var cmp = x.Parent.SubKey.TargetPrefabId.CompareTo(y.Parent.SubKey.TargetPrefabId);
                if (cmp != 0)
                {
                    return cmp;
                }

                cmp = x.Parent.SubKey.TargetObjectId.CompareTo(y.Parent.SubKey.TargetObjectId);
                return cmp != 0 ? cmp : string.CompareOrdinal(x.Node.Name, y.Node.Name);
            });

            return result.ToArray();
        }

        // PrefabUtility.GetRemovedGameObjects(root) -> OverrideTarget[] (SnapshotNode.RemovedGameObjects).
        // assetGameObject is SOURCE-side (it no longer exists live) — its own GlobalObjectId (durable
        // source fileID) is the SubKey; ChildPath is the name-path from the instance's own source root.
        private static OverrideTarget[] ReadRemovedGameObjects(GameObject root)
        {
            var removed = PrefabUtility.GetRemovedGameObjects(root);
            if (removed == null || removed.Count == 0)
            {
                return Array.Empty<OverrideTarget>();
            }

            var sourceRoot = PrefabUtility.GetCorrespondingObjectFromSource(root) as GameObject;

            var result = new List<OverrideTarget>();
            foreach (UnityRemovedGameObject entry in removed)
            {
                var asset = entry.assetGameObject;
                if (asset == null)
                {
                    continue;
                }

                result.Add(new OverrideTarget
                {
                    SubKey = SubKeyOf(asset),
                    ComponentType = "",
                    ChildPath = RelativePath(sourceRoot, asset),
                });
            }

            result.Sort((x, y) =>
            {
                var cmp = x.SubKey.TargetPrefabId.CompareTo(y.SubKey.TargetPrefabId);
                return cmp != 0 ? cmp : x.SubKey.TargetObjectId.CompareTo(y.SubKey.TargetObjectId);
            });

            return result.ToArray();
        }

        // Compact live GameObject -> Core GameObjectNode reader. NEEDED because AddedGameObject.Node is
        // a Core GameObjectNode (not SnapshotNode) — b6-t2's create-with-payload reads Node.Components.
        // Reuses SerializedFieldBridge.ReadComponent (the SAME component reader SceneSnapshotReader
        // uses) — skips the Transform component, mirrors ReadNodeShallow's field set.
        private static GameObjectNode ReadAddedChildNode(GameObject live, Func<UnityEngine.Object, string?>? resolveSceneRef)
        {
            var t = live.transform;
            var lp = t.localPosition;
            var lr = t.localRotation;
            var ls = t.localScale;

            var components = new List<ComponentData>();
            foreach (var component in live.GetComponents<Component>())
            {
                if (component == null || component == t)
                {
                    continue;
                }

                components.Add(SerializedFieldBridge.ReadComponent(component, resolveSceneRef));
            }

            var children = new GameObjectNode[t.childCount];
            for (var i = 0; i < t.childCount; i++)
            {
                children[i] = ReadAddedChildNode(t.GetChild(i).gameObject, resolveSceneRef);
            }

            return new GameObjectNode
            {
                Name = live.name,
                Tag = live.tag,
                Layer = live.layer,
                Active = live.activeSelf,
                IsStatic = live.isStatic,
                Transform = new TransformData
                {
                    Kind = "Transform",
                    Position = new Vec3(lp.x, lp.y, lp.z),
                    Rotation = new Quat(lr.x, lr.y, lr.z, lr.w),
                    Scale = new Vec3(ls.x, ls.y, ls.z),
                },
                Components = components.ToArray(),
                Children = children,
            };
        }
    }
}
