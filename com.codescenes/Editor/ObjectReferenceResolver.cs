#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// The M5 cross-object-reference boundary between Core and Unity's live scene graph. Executes a
    /// deferred <c>SetReference</c> op (see <see cref="PlanExecutor"/>'s post-loop reference pass):
    /// resolves a <c>TargetLogicalId</c> to a live in-scene <see cref="UnityEngine.Object"/> via the
    /// execution's LogicalId-&gt;object maps (falling back to the <see cref="IdentityMap"/>'s
    /// GlobalObjectId for an already-existing object not touched this Materialize), coerces it to the
    /// field's declared reference type (GameObject&lt;-&gt;Component via <c>GetComponent</c>/<c>.gameObject</c>
    /// — e.g. a native <c>HingeJoint.connectedBody</c> field whose authored handle names a GameObject
    /// resolves to that GameObject's <c>Rigidbody</c>), and assigns <c>objectReferenceValue</c>. Mirrors
    /// <see cref="AssetReferenceResolver.WriteAssetRef"/>'s structure: null/empty target clears the
    /// slot; a target that resolves to nothing is a loud, located error — never a silent null.
    /// </summary>
    public static class ObjectReferenceResolver
    {
        /// <summary>
        /// Executes a <c>SetReference</c> op against <paramref name="so"/> at <paramref name="path"/>.
        /// A null/empty <paramref name="targetLogicalId"/> clears the field
        /// (<c>objectReferenceValue = null</c>); otherwise the target is resolved to a live in-scene
        /// object, coerced to the field's declared reference type, and assigned. A target that resolves
        /// to no live object is a loud, located error. Caller commits via
        /// <c>SerializedObject.ApplyModifiedProperties</c>.
        /// </summary>
        public static void WriteReference(
            SerializedObject so, string path, string? targetLogicalId, Component owner,
            IReadOnlyDictionary<string, GameObject> gameObjectsByLogicalId,
            IReadOnlyDictionary<string, Component> componentsByLogicalId,
            IdentityMap map, Scene scene)
        {
            var prop = AssetReferenceResolver.FindOrCreateProperty(so, path);
            if (prop == null)
            {
                Debug.LogWarning($"[SceneBuilder] Reference property '{path}' not found on '{so.targetObject}'.");
                return;
            }

            if (string.IsNullOrEmpty(targetLogicalId))
            {
                // None / clear form.
                prop.objectReferenceValue = null;
                return;
            }

            var wanted = ExpectedRefType(prop);
            var obj = ResolveTarget(targetLogicalId!, wanted, gameObjectsByLogicalId, componentsByLogicalId, map, scene);
            if (obj == null)
            {
                var ownerName = owner != null && owner.gameObject != null ? owner.gameObject.name : "<unknown>";
                var componentType = owner != null ? owner.GetType().Name : "<unknown>";
                throw new InvalidOperationException(
                    $"[SceneBuilder] {ownerName} > {componentType}.{FieldNameOf(path)}: " +
                    $"reference target '{targetLogicalId}' not found");
            }

            prop.objectReferenceValue = obj;
        }

        /// <summary>
        /// Parses <see cref="SerializedProperty.type"/> (<c>"PPtr&lt;$X&gt;"</c>) into the field's
        /// wanted reference <see cref="Type"/>. This is the UNIVERSAL source — unlike
        /// <see cref="SerializedFieldBridge.ResolveFieldType"/> it works for NATIVE serialized fields
        /// too (e.g. <c>HingeJoint.connectedBody</c>, which has no managed C# <c>FieldInfo</c>). Also
        /// reused (M5, read side) by <see cref="IsSceneObjectField"/> to classify a null field without
        /// re-parsing PPtr.
        /// </summary>
        internal static Type? ExpectedRefType(SerializedProperty prop)
        {
            var t = prop.type;
            if (string.IsNullOrEmpty(t) || !t.StartsWith("PPtr<$", StringComparison.Ordinal) || !t.EndsWith(">", StringComparison.Ordinal))
            {
                return null;
            }

            var name = t.Substring(6, t.Length - 7);
            if (name == "GameObject")
            {
                return typeof(GameObject);
            }

            return ComponentTypeResolver.Resolve(name);
        }

        /// <summary>
        /// True when <paramref name="p"/>'s declared PPtr type is a scene object (GameObject or
        /// Component subtype) rather than a project asset (Material, Mesh, ...). Drives the read-side
        /// null-branch classification (M5): a null scene-typed field reads as <c>ObjectRef(null)</c>,
        /// a null asset-typed field stays <c>AssetRef(null)</c>.
        /// </summary>
        internal static bool IsSceneObjectField(SerializedProperty p)
        {
            var t = ExpectedRefType(p);
            return t != null && (t == typeof(GameObject) || typeof(Component).IsAssignableFrom(t));
        }

        /// <summary>
        /// Builds a read-side scene-object identity resolver bound to <paramref name="map"/>'s
        /// GameObject entries (mirrors <c>Reconciler</c>'s goid&lt;-&gt;LogicalId dictionary
        /// construction — the same shape, so read identity agrees with reconcile). The returned
        /// delegate normalizes a Component target to its owning GameObject before the lookup (a
        /// Component has no IdentityMap entry of its own): HIT → the owning GameObject's LogicalId;
        /// MISS (not yet mapped, e.g. newly created) → the target's raw <see cref="GlobalObjectId"/>
        /// string, so a later Sync converges it via the reconciler's pending-target classification;
        /// not a GameObject/Component (e.g. an in-memory, non-scene object) → null.
        /// </summary>
        public static Func<UnityEngine.Object, string?> BuildSceneRefResolver(IdentityMap map)
        {
            var goidToLogicalId = map.Entries
                .Where(e => e.Kind == "GameObject" && !string.IsNullOrEmpty(e.GlobalObjectId))
                .ToDictionary(e => e.GlobalObjectId, e => e.LogicalId);

            return obj =>
            {
                var go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go == null)
                {
                    return null;
                }

                var goid = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
                return goidToLogicalId.TryGetValue(goid, out var logicalId) ? logicalId : goid;
            };
        }

        /// <summary>
        /// Resolves <paramref name="targetLogicalId"/> to a live in-scene object: the execution's
        /// live maps first (a target created THIS Materialize has no GlobalObjectId yet and can ONLY
        /// be found here); falling back to the <see cref="IdentityMap"/>'s GlobalObjectId for an
        /// already-existing, unmodified-this-Materialize object.
        /// </summary>
        private static UnityEngine.Object? ResolveTarget(
            string targetLogicalId, Type? wantedType,
            IReadOnlyDictionary<string, GameObject> gameObjectsByLogicalId,
            IReadOnlyDictionary<string, Component> componentsByLogicalId,
            IdentityMap map, Scene scene)
        {
            if (componentsByLogicalId.TryGetValue(targetLogicalId, out var comp))
            {
                return Coerce(comp, wantedType);
            }

            if (gameObjectsByLogicalId.TryGetValue(targetLogicalId, out var go))
            {
                return Coerce(go, wantedType);
            }

            foreach (var entry in map.Entries)
            {
                if (entry.LogicalId != targetLogicalId || string.IsNullOrEmpty(entry.GlobalObjectId))
                {
                    continue;
                }

                if (GlobalObjectId.TryParse(entry.GlobalObjectId, out var goid))
                {
                    var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(goid);
                    if (obj != null && InScene(obj, scene))
                    {
                        return Coerce(obj, wantedType);
                    }
                }

                break;
            }

            return null;
        }

        /// <summary>
        /// Adapts a resolved GameObject/Component to the field's wanted reference type: a Component
        /// target whose field wants the owning GameObject unwraps via <c>.gameObject</c>; a GameObject
        /// target whose field wants a Component resolves via <c>GetComponent(wantedType)</c> (the
        /// native-field case, e.g. <c>HingeJoint.connectedBody</c>). An object already matching (or an
        /// unresolvable/unknown wanted type) passes through unchanged.
        /// </summary>
        private static UnityEngine.Object? Coerce(UnityEngine.Object obj, Type? wantedType)
        {
            if (wantedType == null || wantedType.IsInstanceOfType(obj))
            {
                return obj;
            }

            if (wantedType == typeof(GameObject))
            {
                return (obj as Component)?.gameObject;
            }

            if (typeof(Component).IsAssignableFrom(wantedType))
            {
                var go = obj as GameObject ?? (obj as Component)?.gameObject;
                return go != null ? go.GetComponent(wantedType) : null;
            }

            return obj;
        }

        private static bool InScene(UnityEngine.Object obj, Scene scene)
        {
            if (!scene.IsValid())
            {
                return true;
            }

            var go = obj as GameObject ?? (obj as Component)?.gameObject;
            return go != null && go.scene == scene;
        }

        private static string FieldNameOf(string path)
        {
            var bracket = path.LastIndexOf('[');
            return bracket < 0 ? path : path.Substring(0, bracket);
        }
    }
}
