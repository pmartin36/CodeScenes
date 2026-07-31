#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Authoring;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Executes a Core <see cref="Plan"/> (code-&gt;scene) IN PLACE against the currently-open scene
    /// (§5 step 5): resolves each op's LogicalId to the EXISTING live object via the IdentityMap
    /// (<c>LogicalId -&gt; GlobalObjectId -&gt; object</c>), updates survivors, creates only new
    /// objects, and destroys only mapped-and-removed ones. It never wipes or recreates the scene —
    /// surviving objects keep their <see cref="GlobalObjectId"/>s across rebuilds.
    /// </summary>
    public static class PlanExecutor
    {
        public sealed class ExecutionResult
        {
            /// <summary>Every resolved-or-created GameObject, keyed by LogicalId (for id capture).</summary>
            public Dictionary<string, GameObject> GameObjectsByLogicalId { get; } = new();

            /// <summary>Every resolved-or-created Component, keyed by component LogicalId (for id capture).</summary>
            public Dictionary<string, Component> ComponentsByLogicalId { get; } = new();
        }

        public static ExecutionResult Execute(Plan plan, IdentityMap map, Scene scene)
        {
            var result = new ExecutionResult();

            // Reverse index: GlobalObjectId string -> live GameObject (walk the scene like the reader).
            var goidToGameObject = new Dictionary<string, GameObject>();
            foreach (var root in scene.GetRootGameObjects())
            {
                IndexGameObjects(root, goidToGameObject);
            }

            // Pre-resolve EXISTING mapped GameObjects by LogicalId so update/parent ops target them.
            foreach (var entry in map.Entries)
            {
                if ((entry.Kind == "GameObject" || entry.Kind == "PrefabInstance")
                    && !string.IsNullOrEmpty(entry.GlobalObjectId)
                    && goidToGameObject.TryGetValue(entry.GlobalObjectId, out var go))
                {
                    result.GameObjectsByLogicalId[entry.LogicalId] = go;
                }
            }

            // Pre-resolve EXISTING mapped Components by LogicalId (owner -> type -> ordinal).
            var ordinalByOwnerType = new Dictionary<string, int>();
            foreach (var entry in map.Entries)
            {
                if (entry.Kind != "Component" || entry.ParentLogicalId == null)
                {
                    continue;
                }

                var ordinalKey = entry.ParentLogicalId + " " + entry.ComponentType;
                var ordinal = ordinalByOwnerType.TryGetValue(ordinalKey, out var c) ? c : 0;
                ordinalByOwnerType[ordinalKey] = ordinal + 1;

                if (!result.GameObjectsByLogicalId.TryGetValue(entry.ParentLogicalId, out var ownerGo))
                {
                    continue;
                }

                var type = ComponentTypeResolver.Resolve(entry.ComponentType);
                if (type == null)
                {
                    continue;
                }

                var comps = ownerGo.GetComponents(type);
                if (ordinal < comps.Length)
                {
                    result.ComponentsByLogicalId[entry.LogicalId] = comps[ordinal];
                }
            }

            // One SerializedObject per component receiving SetField ops; ApplyModifiedProperties once.
            var serializedByComponent = new Dictionary<string, SerializedObject>();

            // SetReference ops are deferred to a post-loop pass (below) so a target created LATER in
            // this same op list (e.g. a component AddComponent'd after the SetReference that targets
            // it) is guaranteed live before resolution — execution stays order-independent.
            var deferredReferences = new List<SetReference>();

            foreach (var op in plan.Ops)
            {
                switch (op)
                {
                    case CreateObject create:
                    {
                        var go = LiveTransformWrite.Create(create.Name, create.TransformKind);
                        if (scene.IsValid() && go.scene != scene)
                        {
                            SceneManager.MoveGameObjectToScene(go, scene);
                        }

                        result.GameObjectsByLogicalId[create.LogicalId] = go;
                        break;
                    }
                    case InstantiatePrefab instantiate:
                    {
                        var path = AssetDatabase.GUIDToAssetPath(instantiate.Guid);
                        if (string.IsNullOrEmpty(path))
                        {
                            throw new InvalidOperationException(
                                $"[SceneBuilder] Prefab asset {instantiate.Guid} (LogicalId '{instantiate.LogicalId}') " +
                                "not found — the asset is missing or not imported. Fix the reference or restore the asset.");
                        }

                        var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (prefabAsset == null)
                        {
                            throw new InvalidOperationException(
                                $"[SceneBuilder] Prefab asset {instantiate.Guid} (LogicalId '{instantiate.LogicalId}') " +
                                $"at path '{path}' is not a loadable GameObject/model prefab.");
                        }

                        var root = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset, scene);

                        Transform? instantiateParent = null;
                        if (instantiate.ParentLogicalId != null &&
                            result.GameObjectsByLogicalId.TryGetValue(instantiate.ParentLogicalId, out var instantiateParentGo))
                        {
                            instantiateParent = instantiateParentGo.transform;
                        }

                        root.transform.SetParent(instantiateParent, worldPositionStays: false);
                        root.transform.SetSiblingIndex(instantiate.SiblingIndex);

                        result.GameObjectsByLogicalId[instantiate.LogicalId] = root;
                        break;
                    }
                    case SetName setName:
                        if (result.GameObjectsByLogicalId.TryGetValue(setName.LogicalId, out var nameGo))
                            nameGo.name = setName.Name;
                        break;
                    case SetParent setParent:
                        if (result.GameObjectsByLogicalId.TryGetValue(setParent.LogicalId, out var child))
                        {
                            Transform? parent = null;
                            if (setParent.ParentLogicalId != null &&
                                result.GameObjectsByLogicalId.TryGetValue(setParent.ParentLogicalId, out var parentGo))
                            {
                                parent = parentGo.transform;
                            }

                            child.transform.SetParent(parent, worldPositionStays: false);
                        }

                        break;
                    case ReorderChild reorder:
                        if (result.GameObjectsByLogicalId.TryGetValue(reorder.LogicalId, out var reorderGo))
                            reorderGo.transform.SetSiblingIndex(reorder.SiblingIndex);
                        break;
                    case SetTag setTag:
                        if (result.GameObjectsByLogicalId.TryGetValue(setTag.LogicalId, out var tagGo))
                            TrySetTag(tagGo, setTag.Tag);
                        break;
                    case SetLayer setLayer:
                        if (result.GameObjectsByLogicalId.TryGetValue(setLayer.LogicalId, out var layerGo))
                            layerGo.layer = setLayer.Layer;
                        break;
                    case SetActive setActive:
                        if (result.GameObjectsByLogicalId.TryGetValue(setActive.LogicalId, out var activeGo))
                            activeGo.SetActive(setActive.Active);
                        break;
                    case SetStatic setStatic:
                        if (result.GameObjectsByLogicalId.TryGetValue(setStatic.LogicalId, out var staticGo))
                            staticGo.isStatic = setStatic.IsStatic;
                        break;
                    case AddComponent add:
                    {
                        var ownerLogicalId = OwnerOf(add.LogicalId);
                        if (ownerLogicalId != null
                            && result.GameObjectsByLogicalId.TryGetValue(ownerLogicalId, out var ownerGo))
                        {
                            var type = ComponentTypeResolver.Resolve(add.Type);
                            if (type != null)
                            {
                                var comp = ComponentDefaultTemplate.Create(ownerGo, type);
                                if (comp != null)
                                {
                                    result.ComponentsByLogicalId[add.LogicalId] = comp;
                                }
                            }
                        }

                        break;
                    }
                    case SetField setField:
                        if (result.ComponentsByLogicalId.TryGetValue(setField.LogicalId, out var targetComp))
                        {
                            if (!serializedByComponent.TryGetValue(setField.LogicalId, out var so))
                            {
                                so = new SerializedObject(targetComp);
                                serializedByComponent[setField.LogicalId] = so;
                            }

                            SerializedFieldBridge.WriteField(so, setField.Path, setField.Value);
                        }
                        else if (result.GameObjectsByLogicalId.TryGetValue(setField.LogicalId, out var fieldGo))
                        {
                            ApplyTransformField(fieldGo.transform, setField);
                        }

                        break;
                    case SetAssetRef setAssetRef:
                        if (result.ComponentsByLogicalId.TryGetValue(setAssetRef.LogicalId, out var assetComp))
                        {
                            if (!serializedByComponent.TryGetValue(setAssetRef.LogicalId, out var assetSo))
                            {
                                assetSo = new SerializedObject(assetComp);
                                serializedByComponent[setAssetRef.LogicalId] = assetSo;
                            }

                            AssetReferenceResolver.WriteAssetRef(
                                assetSo, setAssetRef.Path, setAssetRef.Guid, setAssetRef.FileId, assetComp, map);
                        }

                        break;
                    case SetReference setReference:
                        deferredReferences.Add(setReference);
                        break;
                    case RemoveComponent removeComponent:
                        if (result.ComponentsByLogicalId.TryGetValue(removeComponent.LogicalId, out var rc))
                        {
                            serializedByComponent.Remove(removeComponent.LogicalId);
                            UnityEngine.Object.DestroyImmediate(rc);
                            result.ComponentsByLogicalId.Remove(removeComponent.LogicalId);
                        }

                        break;
                    case ReorderComponent reorderComponent:
                        if (result.ComponentsByLogicalId.TryGetValue(reorderComponent.ComponentLogicalId, out var ro))
                            MoveComponentToIndex(ro, reorderComponent.ToIndex);
                        break;
                    case DestroyObject destroy:
                        if (result.GameObjectsByLogicalId.TryGetValue(destroy.LogicalId, out var destroyGo))
                        {
                            UnityEngine.Object.DestroyImmediate(destroyGo);
                            result.GameObjectsByLogicalId.Remove(destroy.LogicalId);
                        }

                        break;
                    case SetInstanceOverride setInstanceOverride:
                        InstanceOverrideExecutor.Apply(setInstanceOverride, result, map, scene);
                        break;
                    case AddInstanceComponent addInstanceComponent:
                        InstanceOverrideExecutor.Apply(addInstanceComponent, result, map, scene);
                        break;
                    case RemoveInstanceComponent removeInstanceComponent:
                        InstanceOverrideExecutor.Apply(removeInstanceComponent, result);
                        break;
                    case RevertInstanceOverride revertInstanceOverride:
                        InstanceOverrideExecutor.Apply(revertInstanceOverride, result);
                        break;
                    case RevertAddedComponent revertAddedComponent:
                        InstanceOverrideExecutor.Apply(revertAddedComponent, result);
                        break;
                    case RevertRemovedComponent revertRemovedComponent:
                        InstanceOverrideExecutor.Apply(revertRemovedComponent, result);
                        break;
                    case AddInstanceChild addInstanceChild:
                        InstanceOverrideExecutor.Apply(addInstanceChild, result, map, scene);
                        break;
                    case RemoveInstanceChild removeInstanceChild:
                        InstanceOverrideExecutor.Apply(removeInstanceChild, result);
                        break;
                    case RevertAddedChild revertAddedChild:
                        InstanceOverrideExecutor.Apply(revertAddedChild, result);
                        break;
                    case RevertRemovedChild revertRemovedChild:
                        InstanceOverrideExecutor.Apply(revertRemovedChild, result);
                        break;
                }
            }

            // Deferred SetReference pass (§M5): runs AFTER every CreateObject/AddComponent has
            // populated the result maps above, so a target added anywhere in this same op list
            // (including later in this loop) is guaranteed live before resolution.
            foreach (var sr in deferredReferences)
            {
                if (!result.ComponentsByLogicalId.TryGetValue(sr.LogicalId, out var refComp))
                {
                    continue;
                }

                if (!serializedByComponent.TryGetValue(sr.LogicalId, out var refSo))
                {
                    refSo = new SerializedObject(refComp);
                    serializedByComponent[sr.LogicalId] = refSo;
                }

                ObjectReferenceResolver.WriteReference(
                    refSo, sr.Path, sr.TargetLogicalId, refComp,
                    result.GameObjectsByLogicalId, result.ComponentsByLogicalId, map, scene);
            }

            // Commit all component field writes — once per component (§M3 adapter deliverable 1).
            foreach (var so in serializedByComponent.Values)
            {
                if (so.targetObject != null)
                {
                    so.ApplyModifiedProperties();
                }
            }

            return result;
        }

        private static void IndexGameObjects(GameObject go, Dictionary<string, GameObject> index)
        {
            // First-write-wins: a scene that has never been saved has no scene GUID, so EVERY object
            // in it degenerates to the same "Null"-identifier GlobalObjectId (Unity can only compute a
            // real per-object id once the scene is saved). Overwriting on a later, colliding write
            // would silently rebind an EARLIER LogicalId to the WRONG (most-recently-indexed) object;
            // keeping the first write is deterministic and a strict no-op for genuinely unique ids
            // (the only case that matters for a real, saved scene).
            var id = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
            if (!index.ContainsKey(id))
            {
                index[id] = go;
            }

            var t = go.transform;
            for (var i = 0; i < t.childCount; i++)
            {
                IndexGameObjects(t.GetChild(i).gameObject, index);
            }
        }

        // Component LogicalId is always `{ownerLogicalId}/{typeToken}#{ordinal}` (BuilderParser /
        // ComponentReconciler): the trailing type-and-ordinal segment is a single '/'-free run (a
        // C# type token never contains '/'), so the owner GameObject's LogicalId is everything
        // before the LAST '/', regardless of what the type token itself is. Splitting positionally
        // (rather than matching a literal "/" + typeFullName + "#" marker) is required because
        // ComponentTypeNormalizer rewrites Op.Type.FullName to the resolved/qualified CLR name
        // (e.g. "Rigidbody" -> "UnityEngine.Rigidbody") while the component's LogicalId keeps the
        // ORIGINAL authored token — a literal marker match would silently miss and no-op the add.
        private static string? OwnerOf(string componentLogicalId)
        {
            var idx = componentLogicalId.LastIndexOf('/');
            return idx < 0 ? null : componentLogicalId.Substring(0, idx);
        }

        private static void MoveComponentToIndex(Component component, int toIndex)
        {
            var all = component.gameObject.GetComponents<Component>();
            var current = Array.IndexOf(all, component);
            if (current < 0)
            {
                return;
            }

            // ToIndex is 0-based among non-Transform components; slot 0 on the live object is Transform.
            var target = toIndex + 1;
            while (current > target)
            {
                ComponentUtility.MoveComponentUp(component);
                current--;
            }

            while (current < target)
            {
                ComponentUtility.MoveComponentDown(component);
                current++;
            }
        }

        // Writes the full authored vector unconditionally — the code->scene direction no
        // longer masks driven channels (see spec 23). Scene->code suppression of driven channels
        // still lives entirely in Reconciler.MaskDriven / SceneSnapshotReader.DeriveDrivenChannels.
        private static void ApplyTransformField(Transform t, SetField op)
        {
            if (TryApplyRectTransformField(t, op))
            {
                return;
            }

            switch (op.Value)
            {
                case ValueNode.Vec3 v when op.Path == "m_LocalPosition":
                    t.localPosition = new Vector3(v.Value.X, v.Value.Y, v.Value.Z);
                    // This raw write bypasses SurfaceSnap.Evaluate() entirely (it may land on a
                    // frozen driven-channel placeholder far from the live resolved position, e.g. a
                    // rebuild after Reconciler.MaskDriven froze the source's Y). Reset the sibling
                    // component's self-write baseline so its NEXT Evaluate() treats this materialize
                    // write as its own fresh starting point ("the components drive from that start",
                    // spec 23) rather than sticky-detaching off a stale pre-rebuild baseline as if it
                    // were a large manual drag.
                    t.GetComponent<SurfaceSnap>()?.ResetBaseline();
                    break;
                case ValueNode.Vec3 v when op.Path == "m_LocalScale":
                    t.localScale = new Vector3(v.Value.X, v.Value.Y, v.Value.Z);
                    // Symmetric to the m_LocalPosition/SurfaceSnap reset above: this raw materialize
                    // write of the full authored scale is the plugin's own, not a user rescale — reset
                    // FitSize's self-write baseline so its next Evaluate() re-derives from it instead of
                    // back-solving a WRONG value/size into source on a rebuild of a persisted object.
                    t.GetComponent<FitSize>()?.ResetBaseline();
                    break;
                case ValueNode.Quat q when op.Path == "m_LocalRotation":
                    t.localRotation = new Quaternion(q.Value.X, q.Value.Y, q.Value.Z, q.Value.W);
                    break;
                default:
                    Debug.LogWarning($"[SceneBuilder] Unhandled SetField '{op.Path}' on '{op.LogicalId}'.");
                    break;
            }
        }

        // Claims ONLY the five RectTransform serialized paths carrying a Vec2. Returns false for every
        // other path/value so the existing Transform switch (and its "Unhandled SetField" warning) is
        // untouched.
        private static bool TryApplyRectTransformField(Transform t, SetField op)
        {
            if (op.Value is not ValueNode.Vec2 vec || !RectTransformFields.TryFromSerializedPath(op.Path, out _))
            {
                return false;
            }

            var rt = EnsureRectTransform(t, op.LogicalId);
            if (rt == null)
            {
                return true; // located error already surfaced; object left alive
            }

            LiveTransformWrite.SetRectField(rt, op.Path, vec.Value);
            return true;
        }

        // Returns the object's RectTransform, promoting a plain Transform IN PLACE via
        // ComponentDefaultTemplate.Create(go, typeof(RectTransform)) (GameObject EntityId +
        // GlobalObjectId + children + P/R/S all survive). Returns null when
        // Unity refuses (prefab-instance root: the creation primitive returns null with no Unity-side
        // log), after logging the located error. Never destroys the GameObject.
        private static RectTransform? EnsureRectTransform(Transform t, string logicalId)
        {
            if (t is RectTransform existing)
            {
                return existing;
            }

            var go = t.gameObject; // capture BEFORE the add: the old Transform is destroyed
            ComponentDefaultTemplate.Create(go, typeof(RectTransform)); // in-place promotion; may return null (prefab instance)
            if (go.transform is RectTransform promoted)
            {
                ConflictSurfacing.LogNote(logicalId,
                    $"'{go.name}' had a plain Transform and was promoted in place to a RectTransform (same object, " +
                    "no wipe); its UI layout is now authored by .RectTransform(...).");
                return promoted;
            }

            ConflictSurfacing.LogLocatedError(logicalId,
                $"'{go.name}' must become a RectTransform for its authored .RectTransform(...) layout, but Unity " +
                "refused the in-place promotion (a prefab-instance root cannot swap its Transform). The layout was " +
                "NOT applied; the object is unchanged.");
            return null;
        }

        private static void TrySetTag(GameObject go, string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag == "Untagged") return;
            try { go.tag = tag; }
            catch { Debug.LogWarning($"[SceneBuilder] Tag '{tag}' is not in the Tag Manager; skipped for '{go.name}'."); }
        }
    }
}
