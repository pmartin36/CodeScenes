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
    internal static partial class InstanceOverrideExecutor
    {
        private const string MemberSigil = "member:";

        public static void Apply(SetInstanceOverride op, PlanExecutor.ExecutionResult result, IdentityMap map, Scene scene)
        {
            var root = InstanceRoot(op.LogicalId, result);
            var owner = ResolveTargetObject(root, op.Target);
            var type = TypeOf(op.Target);
            var comp = owner != null && type != null ? owner.GetComponent(type) : null;
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
            var owner = ResolveTargetObject(root, op.Target);
            var type = ComponentTypeResolver.Resolve(op.Component.Type);
            if (owner == null || type == null)
            {
                return;
            }

            var comp = ComponentDefaultTemplate.Create(owner, type);
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
            var owner = ResolveTargetObject(root, op.Target);
            var type = TypeOf(op.Target);
            var comp = owner != null && type != null ? owner.GetComponent(type) : null;
            if (comp != null)
            {
                UnityEngine.Object.DestroyImmediate(comp);
            }
        }

        public static void Apply(RevertInstanceOverride op, PlanExecutor.ExecutionResult result)
        {
            var root = InstanceRoot(op.LogicalId, result);
            var owner = ResolveTargetObject(root, op.Target);
            var type = TypeOf(op.Target);
            var comp = owner != null && type != null ? owner.GetComponent(type) : null;
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
            var owner = ResolveTargetObject(root, op.Target);
            if (root == null || owner == null)
            {
                return;
            }

            var typeFullName = StripOrdinal(op.ComponentLogicalId);
            var comp = PrefabInstanceProbe.AddedComponentsOn(root, owner)
                .FirstOrDefault(c => c.GetType().FullName == typeFullName);

            if (comp != null)
            {
                PrefabUtility.RevertAddedComponent(comp, InteractionMode.AutomatedAction);
                result.ComponentsByLogicalId.Remove(op.ComponentLogicalId);
            }
        }

        public static void Apply(RevertRemovedComponent op, PlanExecutor.ExecutionResult result)
        {
            var root = InstanceRoot(op.LogicalId, result);
            var owner = ResolveTargetObject(root, op.Target);
            if (root == null || owner == null)
            {
                return;
            }

            var typeFullName = op.Target.ComponentType;
            var removed = PrefabUtility.GetRemovedComponents(root);
            if (removed == null)
            {
                return;
            }

            var ownerSource = owner == root ? null : PrefabUtility.GetCorrespondingObjectFromSource(owner);

            foreach (UnityRemovedComponent entry in removed)
            {
                if (entry.assetComponent == null || entry.assetComponent.GetType().FullName != typeFullName)
                {
                    continue;
                }

                var matches = owner == root || (UnityEngine.Object)entry.assetComponent.gameObject == ownerSource;
                if (matches)
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

        // m-nested-props b6-t1 (specs/24-nested-prefab-overrides.md write mechanics): resolves the LIVE
        // component/GameObject owner an override Target names — the generalization from "always the
        // outermost instance root" (M10) to "any resolved sub-object at depth". A root Target
        // (ChildPath=="") short-circuits to root byte-unchanged (M10 regression). SubKey is preferred
        // once populated (durable, post-bootstrap b7-t2); a default SubKey (pre-bootstrap hand-authored
        // Target) falls back to the ChildPath walk. Reuses PrefabInstanceProbe's resolvers verbatim —
        // never re-derives GlobalObjectId here.
        private static readonly PrefabInstanceKey DefaultSubKey = new();

        private static GameObject? ResolveTargetObject(GameObject? root, OverrideTarget target)
        {
            if (root == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(target.ChildPath))
            {
                return root;
            }

            if (!target.SubKey.Equals(DefaultSubKey))
            {
                var bySubKey = PrefabInstanceProbe.ResolveSubObjectBySubKey(root, target.SubKey);
                if (bySubKey != null)
                {
                    return bySubKey;
                }
            }

            return PrefabInstanceProbe.ResolveSubObjectByChildPath(root, target.ChildPath);
        }

        private static Type? TypeOf(OverrideTarget target) => ComponentTypeResolver.Resolve(target.ComponentType);

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
