#nullable enable
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Assembles a Core <see cref="SceneSnapshot"/> from the live scene in O(changed), not O(scene):
    /// unchanged GameObjects reuse their previously-built <see cref="SnapshotNode"/> (no component
    /// read, no id resolve); only GameObjects named in the change set are re-read via
    /// <see cref="SceneSnapshotReader"/>. The output must be byte-equivalent (via CanonicalJson) to a
    /// cold <see cref="SceneSnapshotReader.Read"/> for the same scene state.
    /// </summary>
    public sealed class ChangeScopedSnapshot
    {
        /// <summary>The identity cache backing every id resolve this assembler performs — the counting seam.</summary>
        public GlobalObjectIdCache Ids { get; } = new GlobalObjectIdCache();

        private Dictionary<EntityId, SnapshotNode>? _nodeByGoEntityId;

        /// <summary>Full re-walk, warming <see cref="Ids"/> via one batch call. Establishes the baseline for future incremental assembles.</summary>
        public SceneSnapshot AssembleCold(Scene scene)
        {
            Ids.Clear();
            Ids.WarmBatch(CollectAllGameObjects(scene));

            var nodeByGoEntityId = new Dictionary<EntityId, SnapshotNode>();

            SnapshotNode BuildNode(GameObject go)
            {
                var node = SceneSnapshotReader.ReadNode(go, Ids.Resolve);
                CacheDescendants(go, node, nodeByGoEntityId);
                return node;
            }

            var roots = new List<SnapshotNode>();
            foreach (var go in scene.GetRootGameObjects())
            {
                roots.Add(BuildNode(go));
            }

            _nodeByGoEntityId = nodeByGoEntityId;
            return new SceneSnapshot { SchemaVersion = 1, Roots = roots.ToArray() };
        }

        /// <summary>
        /// Re-walks the current hierarchy, rebuilding only the nodes owning an id in
        /// <paramref name="changedEntityIds"/> (or new since the last assemble); every other node is
        /// reused unchanged from the prior assemble. Keyed on <see cref="UnityEngine.EntityId"/>, NOT
        /// <c>int</c> — <c>Object.GetInstanceID()</c> is a compile ERROR on 6000.5.3f1.
        /// </summary>
        public SceneSnapshot AssembleIncremental(Scene scene, IReadOnlyCollection<EntityId> changedEntityIds)
        {
            if (_nodeByGoEntityId == null)
            {
                return AssembleCold(scene);
            }

            var changedGo = new HashSet<EntityId>();
            foreach (var entityId in changedEntityIds)
            {
                var obj = EditorUtility.EntityIdToObject(entityId);
                var go = obj as GameObject;
                if (go == null && obj is Component component)
                {
                    go = component.gameObject;
                }

                if (go != null)
                {
                    changedGo.Add(go.GetEntityId());
                }
            }

            Ids.Invalidate(changedGo);

            var priorNodes = _nodeByGoEntityId;
            var nodeByGoEntityId = new Dictionary<EntityId, SnapshotNode>();

            SnapshotNode BuildNode(GameObject go)
            {
                var t = go.transform;
                var children = new SnapshotNode[t.childCount];
                for (var i = 0; i < t.childCount; i++)
                {
                    children[i] = BuildNode(t.GetChild(i).gameObject);
                }

                var entityId = go.GetEntityId();
                SnapshotNode node;
                if (changedGo.Contains(entityId) || !priorNodes.TryGetValue(entityId, out var cached))
                {
                    node = SceneSnapshotReader.ReadNodeShallow(go, children, Ids.Resolve);
                }
                else
                {
                    node = cached with { Children = children };
                }

                nodeByGoEntityId[entityId] = node;
                return node;
            }

            var roots = new List<SnapshotNode>();
            foreach (var go in scene.GetRootGameObjects())
            {
                roots.Add(BuildNode(go));
            }

            _nodeByGoEntityId = nodeByGoEntityId;
            return new SceneSnapshot { SchemaVersion = 1, Roots = roots.ToArray() };
        }

        private static void CacheDescendants(GameObject go, SnapshotNode node, Dictionary<EntityId, SnapshotNode> nodeByGoEntityId)
        {
            nodeByGoEntityId[go.GetEntityId()] = node;

            var t = go.transform;
            for (var i = 0; i < t.childCount; i++)
            {
                CacheDescendants(t.GetChild(i).gameObject, node.Children[i], nodeByGoEntityId);
            }
        }

        private static List<Object> CollectAllGameObjects(Scene scene)
        {
            var result = new List<Object>();

            void Walk(GameObject go)
            {
                result.Add(go);
                var t = go.transform;
                for (var i = 0; i < t.childCount; i++)
                {
                    Walk(t.GetChild(i).gameObject);
                }
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                Walk(root);
            }

            return result;
        }
    }
}
