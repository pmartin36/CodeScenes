#nullable enable annotations
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

        /// <summary>
        /// Cold read with a scene-object identity resolver (M5, see
        /// <see cref="ObjectReferenceResolver.BuildSceneRefResolver"/>) threaded to every
        /// object-reference field. Every caller must explicitly decide whether an object-reference
        /// field resolves to scene identity: pass a resolver (the sync-cold read path) to have an
        /// assigned in-scene reference resolve to a <see cref="ValueNode.ObjectRef"/>, or pass
        /// <paramref name="resolveSceneRef"/> null to leave scene-object refs Unsupported and pruned.
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

            return FromRoots(roots.ToArray());
        }

        /// <summary>
        /// The one <see cref="SceneSnapshot"/> construction factory every producer (cold read here,
        /// <see cref="ChangeScopedSnapshot"/>'s cold and incremental assembles) routes through, so the
        /// per-type default-field templates (<see cref="SceneSnapshot.ComponentDefaults"/>) are
        /// populated for every caller by construction rather than opted into per site. Walks the
        /// already-assembled tree (no Unity API calls beyond what building <paramref name="roots"/>
        /// already made), collects the type full name of EVERY <see cref="ComponentData"/> the
        /// snapshot carries on ANY channel — including a prefab instance's
        /// <see cref="SnapshotNode.AddedComponents"/> / <see cref="SnapshotNode.AddedGameObjects"/>,
        /// not just <see cref="SnapshotNode.Components"/> / <see cref="SnapshotNode.Children"/> — and
        /// asks <see cref="ComponentDefaultTemplate.TryGet"/> for each — O(components) over an
        /// already-built tree, no SerializedObject work of its own. Invariant:
        /// <see cref="ComponentDefaultTemplate.TryGet"/> answers only for a type whose template has
        /// already been registered in this domain; that holds because
        /// <c>ChangeScopedSnapshot.AssembleIncremental</c> falls back to a cold assemble whenever its
        /// node cache is null, and both caches are process state that dies together on a domain reload —
        /// so a cold read (which calls <see cref="SerializedFieldBridge.ReadComponent"/>, which calls
        /// <see cref="ComponentDefaultTemplate.Register"/>, for every component in the tree, including
        /// the ones reachable only through instance override channels) always precedes a
        /// <see cref="FromRoots"/> call in the same domain.
        /// </summary>
        internal static SceneSnapshot FromRoots(SnapshotNode[] roots)
        {
            var typeNames = new SortedSet<string>(StringComparer.Ordinal);
            CollectComponentTypeNames(roots, typeNames);

            var defaults = new List<ComponentData>(typeNames.Count);
            foreach (var typeName in typeNames)
            {
                if (ComponentDefaultTemplate.TryGet(typeName, out var fields))
                {
                    defaults.Add(new ComponentData { Type = new TypeRef(typeName), Fields = fields });
                }
            }

            return new SceneSnapshot { SchemaVersion = 1, Roots = roots, ComponentDefaults = defaults.ToArray() };
        }

        private static void CollectComponentTypeNames(SnapshotNode[] nodes, SortedSet<string> typeNames)
        {
            foreach (var node in nodes)
            {
                foreach (var component in node.Components)
                {
                    typeNames.Add(component.Type.FullName);
                }

                foreach (var added in node.AddedComponents)
                {
                    typeNames.Add(added.Component.Type.FullName);
                }

                foreach (var addedGo in node.AddedGameObjects)
                {
                    CollectComponentTypeNames(addedGo.Node, typeNames);
                }

                CollectComponentTypeNames(node.Children, typeNames);
            }
        }

        private static void CollectComponentTypeNames(GameObjectNode node, SortedSet<string> typeNames)
        {
            foreach (var component in node.Components)
            {
                typeNames.Add(component.Type.FullName);
            }

            foreach (var child in node.Children)
            {
                CollectComponentTypeNames(child, typeNames);
            }
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
        /// resolve to scene identity (M5, pass a resolver) or stay Unsupported (pass null — correct
        /// only for a caller with no scene-identity resolver to offer, e.g. a fresh-component template
        /// read).
        /// </summary>
        internal static SnapshotNode ReadNodeShallow(GameObject go, SnapshotNode[] children, Func<GameObject, string> resolveId, Func<UnityEngine.Object, string?>? resolveSceneRef)
        {
            var t = go.transform;

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
            var overrides = Array.Empty<PropertyOverride>();
            var addedComponents = Array.Empty<AddedComponent>();
            var removedComponents = Array.Empty<OverrideTarget>();
            var addedGameObjects = Array.Empty<AddedGameObject>();
            var removedGameObjects = Array.Empty<OverrideTarget>();
            TransformData? sourcePrefabTransform = null;
            if (isInstanceRoot)
            {
                var instance = PrefabInstanceProbe.ReadInstanceRoot(go, resolveSceneRef);
                sourcePrefabGuid = instance.SourcePrefabGuid;
                prefabKey = instance.Key;
                opaqueOverrides = instance.OpaqueOverrides;
                overrides = instance.StructuredOverrides;
                addedComponents = instance.AddedComponents;
                removedComponents = instance.RemovedComponents;
                addedGameObjects = instance.AddedGameObjects;
                removedGameObjects = instance.RemovedGameObjects;
                sourcePrefabTransform = instance.SourcePrefabTransform;
            }

            return new SnapshotNode
            {
                GlobalObjectId = resolveId(go),
                Name = go.name,
                Tag = go.tag,
                Layer = go.layer,
                Active = go.activeSelf,
                IsStatic = go.isStatic,
                Transform = LiveTransformRead.Read(go),
                Components = components.ToArray(),
                Children = isInstanceRoot ? Array.Empty<SnapshotNode>() : children,
                SourcePrefabGuid = sourcePrefabGuid,
                PrefabKey = prefabKey,
                OpaqueOverrides = opaqueOverrides,
                Overrides = overrides,
                AddedComponents = addedComponents,
                RemovedComponents = removedComponents,
                AddedGameObjects = addedGameObjects,
                RemovedGameObjects = removedGameObjects,
                SourcePrefabTransform = sourcePrefabTransform,
            };
        }
    }
}
