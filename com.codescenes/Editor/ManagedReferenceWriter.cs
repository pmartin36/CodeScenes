#nullable enable
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Executes a <c>SetManagedReference</c> op: the ONE site that instantiates a
    /// <c>[SerializeReference]</c> polymorphic instance from a Core plan. Resolves the concrete
    /// type, assigns <c>managedReferenceValue</c> (a whole-instance replace, never a member-level
    /// merge), and fills the instance's own fields by reusing
    /// <see cref="SerializedFieldBridge.WriteField"/> for plain values, resolving a direct
    /// <see cref="ValueNode.ObjectRef"/>/<see cref="ValueNode.AssetRef"/> child through the same
    /// resolvers the top-level <c>SetReference</c>/<c>SetAssetRef</c> ops use
    /// (<see cref="ObjectReferenceResolver.WriteReference"/> /
    /// <see cref="AssetReferenceResolver.WriteAssetRef"/>), and recursing into itself for a nested
    /// <see cref="ValueNode.ManagedReference"/> member (so a ref nested inside a nested managed
    /// reference resolves through the same recursion). A <c>ConcreteType</c> with no viable
    /// construction path leaves the live field unchanged and surfaces a located error rather than
    /// half-building an instance.
    /// </summary>
    internal static class ManagedReferenceWriter
    {
        public static void Write(
            SerializedObject so, string path, TypeRef? concreteType, FieldMap fields,
            Component owner, string logicalId,
            PlanExecutor.ExecutionResult result, IdentityMap map, Scene scene)
        {
            var prop = so.FindProperty(path);
            if (prop == null)
            {
                Debug.LogWarning($"[SceneBuilder] managed-ref property '{path}' not found on '{so.targetObject}'.");
                return;
            }

            WriteInstance(prop, concreteType, fields, owner, logicalId, result, map, scene);
        }

        private static void WriteInstance(
            SerializedProperty prop, TypeRef? concreteType, FieldMap fields, Component owner, string logicalId,
            PlanExecutor.ExecutionResult result, IdentityMap map, Scene scene)
        {
            if (concreteType == null)
            {
                prop.managedReferenceValue = null;
                return;
            }

            var type = ComponentTypeResolver.Resolve(concreteType);
            object? instance = null;
            if (type != null)
            {
                try
                {
                    instance = System.Activator.CreateInstance(type);
                }
                catch
                {
                    instance = null;
                }
            }

            if (instance == null)
            {
                ConflictSurfacing.LogLocatedError(owner.gameObject, logicalId,
                    $"'{owner.name}' field '{prop.propertyPath}': the [SerializeReference] type " +
                    $"'{concreteType.FullName}' has no viable construction path (unresolved or no " +
                    "parameterless constructor); the reference was NOT changed.");
                return;
            }

            prop.managedReferenceValue = instance;
            // Flush so the newly-assigned instance's child SerializedProperty nodes exist before
            // the field writes / recursion below try to target them.
            prop.serializedObject.ApplyModifiedProperties();

            var resolvedType = type!; // instance != null implies type != null (Activator needs a live Type).
            foreach (var kv in fields)
            {
                var serializedName = SerializedMemberMap.TrySerializedName(resolvedType, kv.Key, out var m) ? m : kv.Key;
                var child = prop.FindPropertyRelative(serializedName);
                if (child == null)
                {
                    Debug.LogWarning(
                        $"[SceneBuilder] managed-ref member '{kv.Key}' not found on '{resolvedType.FullName}' " +
                        $"at '{prop.propertyPath}'.");
                    continue;
                }

                switch (kv.Value)
                {
                    case ValueNode.ManagedReference nested:
                        WriteInstance(child, nested.ConcreteType, nested.Fields, owner, logicalId, result, map, scene);
                        break;
                    case ValueNode.ObjectRef objRef:
                        ObjectReferenceResolver.WriteReference(
                            prop.serializedObject, child.propertyPath, objRef.TargetLogicalId, owner, logicalId,
                            result.GameObjectsByLogicalId, result.ComponentsByLogicalId, map, scene);
                        break;
                    case ValueNode.AssetRef assetRef:
                        AssetReferenceResolver.WriteAssetRef(
                            prop.serializedObject, child.propertyPath, assetRef.Ref?.Guid, assetRef.Ref?.FileId ?? 0,
                            owner, map);
                        break;
                    default:
                        SerializedFieldBridge.WriteField(prop.serializedObject, child.propertyPath, kv.Value);
                        break;
                }
            }
        }
    }
}
