using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
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

        public static SceneSnapshot Read(Scene scene) => Read(scene, DefaultResolver);

        public static SceneSnapshot Read(Scene scene, Func<GameObject, string> resolveId)
        {
            var roots = new List<SnapshotNode>();
            foreach (var go in scene.GetRootGameObjects())
            {
                roots.Add(ReadNode(go, resolveId));
            }

            return new SceneSnapshot { SchemaVersion = 1, Roots = roots.ToArray() };
        }

        internal static SnapshotNode ReadNode(GameObject go, Func<GameObject, string> resolveId)
        {
            var t = go.transform;

            var children = new SnapshotNode[t.childCount];
            for (var i = 0; i < t.childCount; i++)
            {
                children[i] = ReadNode(t.GetChild(i).gameObject, resolveId);
            }

            return ReadNodeShallow(go, children, resolveId);
        }

        /// <summary>
        /// Builds a single node's own fields (name/tag/layer/active/isStatic/transform/components +
        /// resolved id) from already-built <paramref name="children"/>. The ONE place components and
        /// the id are read — shared by the cold walk (<see cref="ReadNode"/>) and by
        /// <see cref="ChangeScopedSnapshot"/>'s incremental rebuild of dirty nodes.
        /// </summary>
        internal static SnapshotNode ReadNodeShallow(GameObject go, SnapshotNode[] children, Func<GameObject, string> resolveId)
        {
            var t = go.transform;
            var lp = t.localPosition;
            var lr = t.localRotation;
            var ls = t.localScale;

            // Read every component EXCEPT the GameObject's own transform (handled separately above,
            // and excluded from Components[] by the Core model). Missing scripts (null) are skipped.
            var components = new List<ComponentData>();
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null || component == t)
                {
                    continue;
                }

                components.Add(SerializedFieldBridge.ReadComponent(component));
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
                },
                Components = components.ToArray(),
                Children = children,
            };
        }
    }
}
