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
    /// </summary>
    public static class SceneSnapshotReader
    {
        public static SceneSnapshot Read(Scene scene)
        {
            var roots = new List<SnapshotNode>();
            foreach (var go in scene.GetRootGameObjects())
            {
                roots.Add(ReadNode(go));
            }

            return new SceneSnapshot { SchemaVersion = 1, Roots = roots.ToArray() };
        }

        private static SnapshotNode ReadNode(GameObject go)
        {
            var t = go.transform;

            var children = new List<SnapshotNode>(t.childCount);
            for (var i = 0; i < t.childCount; i++)
            {
                children.Add(ReadNode(t.GetChild(i).gameObject));
            }

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
                GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(),
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
                Children = children.ToArray(),
            };
        }
    }
}
