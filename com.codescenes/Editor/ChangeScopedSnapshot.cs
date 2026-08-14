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
    /// cold <see cref="SceneSnapshotReader.Read"/> for the same scene state. The M5 scene-object
    /// identity resolver (see <see cref="SceneRefResolver"/>) is a required parameter of every
    /// assemble call, not sticky instance state — each caller states its own answer for that
    /// assemble. The node cache is keyed on the resolver's <see cref="SceneRefResolver.Generation"/>:
    /// an incremental assemble whose generation differs from the one the cache was built under
    /// degrades to a full cold assemble rather than serve nodes resolved under a stale
    /// <see cref="IdentityMap"/>.
    /// </summary>
    public sealed class ChangeScopedSnapshot
    {
        /// <summary>The identity cache backing every id resolve this assembler performs — the counting seam.</summary>
        public GlobalObjectIdCache Ids { get; } = new GlobalObjectIdCache();

        private Dictionary<EntityId, SnapshotNode>? _nodeByGoEntityId;
        private string? _cacheGeneration;

        /// <summary>Full re-walk, warming <see cref="Ids"/> via one batch call. Establishes the baseline for future incremental assembles.</summary>
        public SceneSnapshot AssembleCold(Scene scene, SceneRefResolver sceneRef)
        {
            Ids.Clear();
            Ids.WarmBatch(CollectAllObjects(scene));
            var bound = sceneRef.Bind(Ids);

            var nodeByGoEntityId = new Dictionary<EntityId, SnapshotNode>();

            SnapshotNode BuildNode(GameObject go)
            {
                var node = SceneSnapshotReader.ReadNode(go, Ids.Resolve, bound.Resolve, bound.ResolveListener);
                CacheDescendants(go, node, nodeByGoEntityId);
                return node;
            }

            var roots = new List<SnapshotNode>();
            foreach (var go in scene.GetRootGameObjects())
            {
                roots.Add(BuildNode(go));
            }

            _nodeByGoEntityId = nodeByGoEntityId;
            _cacheGeneration = sceneRef.Generation;
            return SceneSnapshotReader.FromRoots(roots.ToArray());
        }

        /// <summary>
        /// Re-walks the current hierarchy, rebuilding only the nodes owning an id in
        /// <paramref name="changedEntityIds"/> (or new since the last assemble); every other node is
        /// reused unchanged from the prior assemble. Keyed on <see cref="UnityEngine.EntityId"/>, NOT
        /// <c>int</c> — <c>Object.GetInstanceID()</c> is a compile ERROR on 6000.5.3f1.
        /// </summary>
        public SceneSnapshot AssembleIncremental(Scene scene, IReadOnlyCollection<EntityId> changedEntityIds, SceneRefResolver sceneRef)
        {
            if (_nodeByGoEntityId == null || _cacheGeneration != sceneRef.Generation)
            {
                return AssembleCold(scene, sceneRef);
            }

            var changedGo = new HashSet<EntityId>();
            var idsToInvalidate = new List<EntityId>();
            foreach (var entityId in changedEntityIds)
            {
                var obj = EditorUtility.EntityIdToObject(entityId);
                var go = obj as GameObject;
                if (go == null && obj is Component component)
                {
                    go = component.gameObject;
                }

                if (go != null && changedGo.Add(go.GetEntityId()))
                {
                    // Invalidate the GameObject's own id AND its components' ids (same rule as
                    // ObjectsOwnedBy/CollectAllObjects): a changed GameObject re-resolves to a
                    // fresh id, and its components must not keep whatever was cached before.
                    foreach (var owned in ObjectsOwnedBy(go))
                    {
                        idsToInvalidate.Add(owned.GetEntityId());
                    }
                }
            }

            Ids.Invalidate(idsToInvalidate);
            var bound = sceneRef.Bind(Ids);

            var priorNodes = _nodeByGoEntityId;
            var nodeByGoEntityId = new Dictionary<EntityId, SnapshotNode>();

            SnapshotNode BuildNode(GameObject go)
            {
                var t = go.transform;
                SnapshotNode[] children;
                if (PrefabInstanceProbe.IsInstanceRoot(go))
                {
                    children = System.Array.Empty<SnapshotNode>();
                }
                else
                {
                    children = new SnapshotNode[t.childCount];
                    for (var i = 0; i < t.childCount; i++)
                    {
                        children[i] = BuildNode(t.GetChild(i).gameObject);
                    }
                }

                var entityId = go.GetEntityId();
                SnapshotNode node;
                if (changedGo.Contains(entityId) || !priorNodes.TryGetValue(entityId, out var cached))
                {
                    node = SceneSnapshotReader.ReadNodeShallow(go, children, Ids.Resolve, bound.Resolve, bound.ResolveListener);
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
            _cacheGeneration = sceneRef.Generation;
            return SceneSnapshotReader.FromRoots(roots.ToArray());
        }

        private static void CacheDescendants(GameObject go, SnapshotNode node, Dictionary<EntityId, SnapshotNode> nodeByGoEntityId)
        {
            nodeByGoEntityId[go.GetEntityId()] = node;

            // A prefab-instance ROOT's node.Children is always empty (its internals are never
            // enumerated) — do not descend into the live hierarchy under it, or node.Children[i]
            // indexes out of range.
            if (PrefabInstanceProbe.IsInstanceRoot(go))
            {
                return;
            }

            var t = go.transform;
            for (var i = 0; i < t.childCount; i++)
            {
                CacheDescendants(t.GetChild(i).gameObject, node.Children[i], nodeByGoEntityId);
            }
        }

        // Every GameObject AND every non-Transform component the cold read (ReadNodeShallow) will
        // resolve an id for, so the cold warm stays one batched call instead of N per-component
        // slow resolves on the per-keystroke sync path.
        private static List<Object> CollectAllObjects(Scene scene)
        {
            var result = new List<Object>();

            void Walk(GameObject go)
            {
                result.AddRange(ObjectsOwnedBy(go));

                // Never descend into a prefab instance's internals (never enumerated by the reader).
                if (PrefabInstanceProbe.IsInstanceRoot(go))
                {
                    return;
                }

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

        // Every id-bearing object a single GameObject owns: itself, plus every non-null,
        // non-Transform component ReadNodeShallow resolves an id for. A prefab instance root's
        // components are never enumerated by the reader (and would be a per-keystroke
        // GetGlobalObjectIdSlow cost — CLAUDE.md sync-performance constraint), so only the
        // GameObject itself counts there. The single source of this rule so the warm cache
        // (CollectAllObjects) and the invalidate path (AssembleIncremental) cannot drift apart.
        private static IEnumerable<Object> ObjectsOwnedBy(GameObject go)
        {
            yield return go;

            if (PrefabInstanceProbe.IsInstanceRoot(go))
            {
                yield break;
            }

            var t = go.transform;
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null || component == t)
                {
                    continue;
                }

                yield return component;
            }
        }
    }
}
