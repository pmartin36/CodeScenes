#nullable enable annotations
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Authoring;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Reads the live scene into a Core <see cref="SceneSnapshot"/>, stamping every object with its
    /// durable <see cref="GlobalObjectId"/> — the identity the reconciler keys on to know what changed.
    /// The node-construction body is shared (via <see cref="ReadNodeShallow"/>) with
    /// <see cref="ChangeScopedSnapshot"/> so cold and incremental assembly are byte-identical by
    /// construction.
    /// </summary>
    public static class SceneSnapshotReader
    {
        private static readonly Func<GameObject, string> DefaultResolver =
            go => GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();

        public static SceneSnapshot Read(Scene scene) => Read(scene, DefaultResolver, resolveSceneRef: null);

        /// <summary>
        /// Cold read with a scene-object identity resolver (M5, see
        /// <see cref="ObjectReferenceResolver.BuildSceneRefResolver"/>) threaded to every
        /// object-reference field — the sync-cold read path. <paramref name="resolveSceneRef"/> null
        /// (the default, build-path <see cref="Read(Scene)"/> overload) leaves scene-object refs
        /// Unsupported, M4-preserved.
        /// </summary>
        public static SceneSnapshot Read(Scene scene, Func<UnityEngine.Object, string?>? resolveSceneRef) =>
            Read(scene, DefaultResolver, resolveSceneRef);

        private static SceneSnapshot Read(Scene scene, Func<GameObject, string> resolveId, Func<UnityEngine.Object, string?>? resolveSceneRef)
        {
            var roots = new List<SnapshotNode>();
            foreach (var go in scene.GetRootGameObjects())
            {
                roots.Add(ReadNode(go, resolveId, resolveSceneRef));
            }

            return new SceneSnapshot { SchemaVersion = 1, Roots = roots.ToArray() };
        }

        internal static SnapshotNode ReadNode(GameObject go, Func<GameObject, string> resolveId, Func<UnityEngine.Object, string?>? resolveSceneRef)
        {
            var t = go.transform;

            // A prefab-instance ROOT reads as one opaque unit — never descend into its internals
            // (diff correctness + sync performance, research.md b5-t2).
            SnapshotNode[] children;
            if (PrefabInstanceProbe.IsInstanceRoot(go))
            {
                children = Array.Empty<SnapshotNode>();
            }
            else
            {
                children = new SnapshotNode[t.childCount];
                for (var i = 0; i < t.childCount; i++)
                {
                    children[i] = ReadNode(t.GetChild(i).gameObject, resolveId, resolveSceneRef);
                }
            }

            return ReadNodeShallow(go, children, resolveId, resolveSceneRef);
        }

        /// <summary>
        /// Builds a single node's own fields (name/tag/layer/active/isStatic/transform/components +
        /// resolved id) from already-built <paramref name="children"/>. The ONE place components and
        /// the id are read — shared by the cold walk (<see cref="ReadNode"/>) and by
        /// <see cref="ChangeScopedSnapshot"/>'s incremental rebuild of dirty nodes.
        /// <paramref name="resolveSceneRef"/> is REQUIRED (not defaulted) so every caller — including
        /// <see cref="ChangeScopedSnapshot"/> — must explicitly decide whether object-reference fields
        /// resolve to scene identity (M5) or stay Unsupported (build path: pass null).
        /// </summary>
        internal static SnapshotNode ReadNodeShallow(GameObject go, SnapshotNode[] children, Func<GameObject, string> resolveId, Func<UnityEngine.Object, string?>? resolveSceneRef)
        {
            var t = go.transform;
            var lp = t.localPosition;
            var lr = t.localRotation;
            var ls = t.localScale;

            var isInstanceRoot = PrefabInstanceProbe.IsInstanceRoot(go);

            // A prefab-instance ROOT reads as one opaque unit: its internal components are never
            // enumerated (the whole instance is one unit; see PrefabInstanceProbe / research.md b5-t2).
            var components = new List<ComponentData>();
            if (!isInstanceRoot)
            {
                // Read every component EXCEPT the GameObject's own transform (handled separately
                // above, and excluded from Components[] by the Core model). Missing scripts (null)
                // are skipped.
                foreach (var component in go.GetComponents<Component>())
                {
                    if (component == null || component == t)
                    {
                        continue;
                    }

                    components.Add(SerializedFieldBridge.ReadComponent(component, resolveSceneRef));
                }
            }

            string? sourcePrefabGuid = null;
            PrefabInstanceKey? prefabKey = null;
            ValueNode.Unsupported? opaqueOverrides = null;
            if (isInstanceRoot)
            {
                var instance = PrefabInstanceProbe.ReadInstanceRoot(go);
                sourcePrefabGuid = instance.SourcePrefabGuid;
                prefabKey = instance.Key;
                opaqueOverrides = instance.Overrides;
            }

            return new SnapshotNode
            {
                GlobalObjectId = resolveId(go),
                Name = go.name,
                Tag = go.tag,
                Layer = go.layer,
                Active = go.activeSelf,
                IsStatic = go.isStatic,
                Transform = new TransformData
                {
                    Kind = "Transform",
                    Position = new Vec3(lp.x, lp.y, lp.z),
                    Rotation = new Quat(lr.x, lr.y, lr.z, lr.w),
                    Scale = new Vec3(ls.x, ls.y, ls.z),
                    DrivenChannels = DeriveDrivenChannels(go),
                },
                Components = components.ToArray(),
                Children = isInstanceRoot ? Array.Empty<SnapshotNode>() : children,
                SourcePrefabGuid = sourcePrefabGuid,
                PrefabKey = prefabKey,
                OpaqueOverrides = opaqueOverrides,
            };
        }

        /// <summary>
        /// ORs together the driven channels of every ACTIVE-AND-ENABLED FitSize/SurfaceSnap on
        /// <paramref name="go"/> — the same guard those components' own <c>Evaluate()</c> use
        /// (<c>isActiveAndEnabled</c>), so "reader says driven" always agrees with "component
        /// actually drives". A disabled/inactive component contributes nothing (releases its
        /// channel so a manual edit syncs normally). Mirrors the parse-time mapping in
        /// <c>SpatialComponents.FitSizeMask</c>/<c>SurfaceSnapMask</c> so desired and actual never diverge.
        /// </summary>
        private static ChannelMask DeriveDrivenChannels(GameObject go)
        {
            var mask = ChannelMask.None;

            foreach (var sizer in go.GetComponents<FitSize>())
            {
                if (sizer.isActiveAndEnabled)
                {
                    mask |= SpatialComponents.FitSizeMask;
                }
            }

            foreach (var snapper in go.GetComponents<SurfaceSnap>())
            {
                if (snapper.isActiveAndEnabled)
                {
                    mask |= SpatialComponents.SurfaceSnapMask(
                        snapper.vertical != SurfaceSnap.Vertical.None,
                        snapper.horizontal != SurfaceSnap.Horizontal.None,
                        snapper.depth != SurfaceSnap.Depth.None);
                }
            }

            return mask;
        }
    }
}
