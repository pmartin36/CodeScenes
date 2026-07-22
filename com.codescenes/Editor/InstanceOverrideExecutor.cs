#nullable enable
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using UnityRemovedComponent = UnityEditor.SceneManagement.RemovedComponent;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// M10 (specs/11-m10-prefab-overrides.md, research.md b7-t1): applies the six instance-override
    /// Plan ops IN PLACE against the live outermost prefab instance root resolved from
    /// <see cref="PlanExecutor.ExecutionResult.GameObjectsByLogicalId"/> — never re-instantiates, so
    /// the instance's <see cref="GlobalObjectId"/> is preserved. Writes reuse the M3/M4/M5 field
    /// bridges (<see cref="SerializedFieldBridge"/>/<see cref="AssetReferenceResolver"/>/
    /// <see cref="ObjectReferenceResolver"/>) so Unity records the resulting delta as a bold
    /// property/added/removed-component override exactly as the Inspector would (symmetric with
    /// b6-t1's read side); reverts use the matching <see cref="PrefabUtility"/> Revert* API.
    /// </summary>
    internal static class InstanceOverrideExecutor
    {
        private const string TypeSigilPrefix = "type:";
        private const string MemberSigil = "member:";

        public static void Apply(SetInstanceOverride op, PlanExecutor.ExecutionResult result, IdentityMap map, Scene scene)
        {
            var root = InstanceRoot(op.LogicalId, result);
            var type = TypeOf(op.Target);
            var comp = root != null && type != null ? root.GetComponent(type) : null;
            if (comp == null)
            {
                return;
            }

            var so = new SerializedObject(comp);
            var path = ResolveProperty(so, op.PropertyPath);
            WriteFieldValue(so, path, op.ObjectReference ?? op.Value, comp, result, map, scene);
            so.ApplyModifiedProperties();
        }

        public static void Apply(AddInstanceComponent op, PlanExecutor.ExecutionResult result, IdentityMap map, Scene scene)
        {
            var root = InstanceRoot(op.LogicalId, result);
            var type = ComponentTypeResolver.Resolve(op.Component.Type);
            if (root == null || type == null)
            {
                return;
            }

            var comp = root.AddComponent(type);
            if (comp == null)
            {
                return;
            }

            if (op.Component.Fields.Count > 0)
            {
                var so = new SerializedObject(comp);
                foreach (var (key, value) in op.Component.Fields)
                {
                    var path = ResolveProperty(so, key);
                    WriteFieldValue(so, path, value, comp, result, map, scene);
                }

                so.ApplyModifiedProperties();
            }

            result.ComponentsByLogicalId[op.Component.LogicalId] = comp;
        }

        public static void Apply(RemoveInstanceComponent op, PlanExecutor.ExecutionResult result)
        {
            var root = InstanceRoot(op.LogicalId, result);
            var type = TypeOf(op.Target);
            var comp = root != null && type != null ? root.GetComponent(type) : null;
            if (comp != null)
            {
                UnityEngine.Object.DestroyImmediate(comp);
            }
        }

        public static void Apply(RevertInstanceOverride op, PlanExecutor.ExecutionResult result)
        {
            var root = InstanceRoot(op.LogicalId, result);
            var type = TypeOf(op.Target);
            var comp = root != null && type != null ? root.GetComponent(type) : null;
            if (comp == null)
            {
                return;
            }

            var so = new SerializedObject(comp);
            var path = ResolveProperty(so, op.PropertyPath);
            var prop = so.FindProperty(path);
            if (prop != null)
            {
                PrefabUtility.RevertPropertyOverride(prop, InteractionMode.AutomatedAction);
            }
        }

        public static void Apply(RevertAddedComponent op, PlanExecutor.ExecutionResult result)
        {
            var root = InstanceRoot(op.LogicalId, result);
            if (root == null)
            {
                return;
            }

            Component? comp = null;
            if (result.ComponentsByLogicalId.TryGetValue(op.ComponentLogicalId, out var mapped) && mapped != null)
            {
                comp = mapped;
            }
            else
            {
                var typeFullName = StripOrdinal(op.ComponentLogicalId);
                comp = PrefabInstanceProbe.RootAddedComponents(root)
                    .FirstOrDefault(c => c.GetType().FullName == typeFullName);
            }

            if (comp != null)
            {
                PrefabUtility.RevertAddedComponent(comp, InteractionMode.AutomatedAction);
                result.ComponentsByLogicalId.Remove(op.ComponentLogicalId);
            }
        }

        public static void Apply(RevertRemovedComponent op, PlanExecutor.ExecutionResult result)
        {
            var root = InstanceRoot(op.LogicalId, result);
            if (root == null)
            {
                return;
            }

            var typeFullName = StripSigil(op.Target.PrefabId);
            var removed = PrefabUtility.GetRemovedComponents(root);
            if (removed == null)
            {
                return;
            }

            foreach (UnityRemovedComponent entry in removed)
            {
                if (entry.assetComponent != null && entry.assetComponent.GetType().FullName == typeFullName)
                {
                    PrefabUtility.RevertRemovedComponent(root, entry.assetComponent, InteractionMode.AutomatedAction);
                    return;
                }
            }
        }

        // Resolves the LIVE outermost instance-root GameObject for LogicalId — never re-instantiates
        // (preserves GlobalObjectId). GetOutermostPrefabInstanceRoot is a defensive no-op for a
        // GameObject that is already the outermost root (the normal case here, since LogicalId is
        // pre-resolved to the instance root by PlanExecutor).
        private static GameObject? InstanceRoot(string logicalId, PlanExecutor.ExecutionResult result)
        {
            if (!result.GameObjectsByLogicalId.TryGetValue(logicalId, out var go) || go == null)
            {
                return null;
            }

            var outermost = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            return outermost != null ? outermost : go;
        }

        private static Type? TypeOf(OverrideTarget target) => ComponentTypeResolver.Resolve(StripSigil(target.PrefabId));

        // Mirrors ReconcilerInstances.StripTypeSigil (SceneBuilder.Core/Reconcile/ReconcilerInstances.cs) —
        // the parser's "type:<FullName>" encoding for a root-only OverrideTarget (b6-t1/research.md
        // REFINED). Core's copy is private; this is the adapter-side mirror of the same convention.
        private static string StripSigil(string prefabId) =>
            prefabId.StartsWith(TypeSigilPrefix, StringComparison.Ordinal) ? prefabId.Substring(TypeSigilPrefix.Length) : prefabId;

        // ComponentLogicalId is "{ownerLogicalId}/{typeToken}#{ordinal}" (see PlanExecutor.OwnerOf) —
        // strip the owner prefix and ordinal suffix to get the bare type token for the type-based
        // added-component fallback match.
        private static string StripOrdinal(string componentLogicalId)
        {
            var slash = componentLogicalId.LastIndexOf('/');
            var afterSlash = slash < 0 ? componentLogicalId : componentLogicalId.Substring(slash + 1);
            var hash = afterSlash.LastIndexOf('#');
            return hash < 0 ? afterSlash : afterSlash.Substring(0, hash);
        }

        // AuthoredPathResolver does not descend into PrefabInstanceNode.Overrides[]/AddedComponents[]
        // (research.md INTEGRATION RISK), so a typed-selector override can still carry the transient
        // "member:<name>" form here. Mirrors AuthoredPathResolver.ResolvePath's member -> member-name
        // -> "m_"-mangled fallback, resolved against the LIVE instance component (the authoritative
        // SerializedObject) rather than a throwaway probe.
        private static string ResolveProperty(SerializedObject so, string path)
        {
            if (!path.StartsWith(MemberSigil, StringComparison.Ordinal))
            {
                return path;
            }

            var member = path.Substring(MemberSigil.Length);
            if (so.FindProperty(member) != null)
            {
                return member;
            }

            var mangled = "m_" + char.ToUpperInvariant(member[0]) + member.Substring(1);
            return so.FindProperty(mangled) != null ? mangled : member;
        }

        // The AssetRef/ObjectRef/plain routing shared by SetInstanceOverride and AddInstanceComponent
        // field writes — never a PropertyModification/SetPropertyModifications stringifier (research.md
        // REFINED: SetPropertyModifications replaces the WHOLE override list and would clobber every
        // override not in the passed array).
        private static void WriteFieldValue(
            SerializedObject so, string path, ValueNode value, Component owner,
            PlanExecutor.ExecutionResult result, IdentityMap map, Scene scene)
        {
            switch (value)
            {
                case ValueNode.AssetRef assetRef:
                    AssetReferenceResolver.WriteAssetRef(so, path, assetRef.Ref?.Guid, assetRef.Ref?.FileId ?? 0, owner, map);
                    break;
                case ValueNode.ObjectRef objectRef:
                    ObjectReferenceResolver.WriteReference(
                        so, path, objectRef.TargetLogicalId, owner,
                        result.GameObjectsByLogicalId, result.ComponentsByLogicalId, map, scene);
                    break;
                default:
                    SerializedFieldBridge.WriteField(so, path, value);
                    break;
            }
        }
    }
}
