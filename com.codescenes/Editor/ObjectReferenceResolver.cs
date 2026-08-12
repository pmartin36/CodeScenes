#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// The M5 cross-object-reference boundary between Core and Unity's live scene graph. Executes a
    /// deferred <c>SetReference</c> op (see <see cref="PlanExecutor"/>'s post-loop reference pass):
    /// resolves a <c>TargetLogicalId</c> to a live in-scene <see cref="UnityEngine.Object"/> via the
    /// execution's LogicalId-&gt;object maps (falling back to the <see cref="IdentityMap"/>'s
    /// GlobalObjectId for an already-existing object not touched this Materialize), coerces it to the
    /// field's declared reference type (GameObject&lt;-&gt;Component via <c>GetComponent</c>/<c>.gameObject</c>
    /// — e.g. a native <c>Canvas.m_Camera</c> field whose authored handle names a GameObject resolves to
    /// that GameObject's <c>Camera</c>), and assigns <c>objectReferenceValue</c>. Mirrors
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
            SerializedObject so, string path, string? targetLogicalId, Component owner, string ownerLogicalId,
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

            var wanted = ExpectedRefType(prop, out var ambiguousCandidates);
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

            // Unity's own setter rejects an object that is not the field's declared type, leaving the slot
            // null. That can only survive Coerce above when the declared type could NOT be determined, which
            // is what an ambiguous short name produces: the type was not guessed, so nothing coerced the
            // target and the authored reference is dropped. Report it located rather than in silence.
            if (prop.objectReferenceValue == null && ambiguousCandidates.Count >= 2 && owner != null)
            {
                ConflictSurfacing.SurfaceLocatedError(
                    Conflict.AmbiguousTypeName(
                        ownerLogicalId,
                        owner.GetType().FullName,
                        FieldNameOf(path),
                        ambiguousCandidates[0].Name,
                        ambiguousCandidates.Select(t => t.FullName ?? t.Name).ToList()),
                    owner.gameObject);
            }
        }

        /// <summary>
        /// Resolves the field's wanted reference <see cref="Type"/> from <see cref="SerializedProperty.type"/>.
        /// <c>SerializedProperty.type</c> spells a MANAGED (C#) reference field as <c>"PPtr&lt;$X&gt;"</c>
        /// and a NATIVE one as <c>"PPtr&lt;X&gt;"</c> (no <c>$</c>, e.g. <c>HingeJoint.connectedBody</c>,
        /// which has no managed <c>FieldInfo</c>); <see cref="PPtrTypeName"/> parses both into the bare
        /// token X, then a 3-rung ladder resolves it: (1) the managed <c>FieldInfo</c> via
        /// <see cref="SerializedMemberMap.ResolveManagedFieldType"/>, accepted only when it is a
        /// <see cref="UnityEngine.Object"/> subtype — exact, answers every <c>$</c> token; (2) the literal
        /// <c>GameObject</c> token; (3) <see cref="ComponentTypeResolver.ResolveObjectTypeByShortName"/> —
        /// the single loaded <see cref="UnityEngine.Object"/> subtype named X, null when zero or 2+ match.
        /// Never logs; never touches <see cref="ComponentTypeResolver"/>'s shared full-name cache. Also
        /// reused (M5, read side) by <see cref="IsSceneObjectField"/> to classify a null field without
        /// re-parsing PPtr.
        /// </summary>
        internal static Type? ExpectedRefType(SerializedProperty prop, out IReadOnlyList<Type> ambiguousCandidates)
        {
            ambiguousCandidates = Array.Empty<Type>();

            var name = PPtrTypeName(prop.type);
            if (name == null)
            {
                return null;
            }

            var ownerType = prop.serializedObject.targetObject?.GetType();
            if (ownerType != null)
            {
                var managed = SerializedMemberMap.ResolveManagedFieldType(ownerType, prop.propertyPath);
                if (managed != null && typeof(UnityEngine.Object).IsAssignableFrom(managed))
                {
                    return managed;
                }
            }

            if (name == "GameObject")
            {
                return typeof(GameObject);
            }

            return ComponentTypeResolver.ResolveObjectTypeByShortName(name, out ambiguousCandidates);
        }

        /// <summary>
        /// Parses <see cref="SerializedProperty.type"/> into the bare PPtr token: <c>"PPtr&lt;$X&gt;"</c>
        /// (managed) and <c>"PPtr&lt;X&gt;"</c> (native) both yield <c>X</c>; anything else is <c>null</c>.
        /// THE one place a PPtr type string is parsed in the package.
        /// </summary>
        private static string? PPtrTypeName(string? propertyType)
        {
            if (string.IsNullOrEmpty(propertyType)
                || !propertyType!.StartsWith("PPtr<", StringComparison.Ordinal)
                || !propertyType.EndsWith(">", StringComparison.Ordinal))
            {
                return null;
            }

            var inner = propertyType.Substring(5, propertyType.Length - 6);
            if (inner.Length > 0 && inner[0] == '$')
            {
                inner = inner.Substring(1);
            }

            return inner.Length > 0 ? inner : null;
        }

        /// <summary>
        /// True when <paramref name="p"/>'s declared PPtr type is a scene object (GameObject or
        /// Component subtype) rather than a project asset (Material, Mesh, ...). Drives the read-side
        /// null-branch classification (M5): a null scene-typed field reads as <c>ObjectRef(null)</c>,
        /// a null asset-typed field stays <c>AssetRef(null)</c>.
        /// </summary>
        internal static bool IsSceneObjectField(SerializedProperty p)
        {
            var t = ExpectedRefType(p, out _);
            return t != null && (t == typeof(GameObject) || typeof(Component).IsAssignableFrom(t));
        }

        /// <summary>
        /// Builds a read-side scene-object identity resolver bound to <paramref name="map"/>'s node
        /// entries — a plain GameObject or a PrefabInstance root (<see cref="IdentityNodeIndex"/>,
        /// mirrors <c>Reconciler</c>'s goid&lt;-&gt;LogicalId dictionary construction — the same
        /// shape, so read identity agrees with reconcile). The returned delegate normalizes a
        /// Component target to its owning GameObject before the lookup (a Component has no
        /// IdentityMap entry of its own): HIT → the owning node's LogicalId; MISS (not yet mapped,
        /// e.g. newly created) → the target's raw <see cref="GlobalObjectId"/> string, so a later
        /// Sync converges it via the reconciler's pending-target classification; not a
        /// GameObject/Component (e.g. an in-memory, non-scene object) → null.
        /// </summary>
        public static Func<UnityEngine.Object, string?> BuildSceneRefResolver(IdentityMap map)
            => BuildFromIndex(IdentityNodeIndex.GlobalObjectIdToLogicalId(map));

        /// <summary>
        /// The one closure implementation behind <see cref="BuildSceneRefResolver"/>, taking an
        /// already-built goid-&gt;LogicalId projection so <see cref="SceneRefResolver.ForMap"/> can
        /// derive the resolve delegate and its generation from the SAME projection without building
        /// it twice.
        /// </summary>
        internal static Func<UnityEngine.Object, string?> BuildFromIndex(IReadOnlyDictionary<string, string> goidToLogicalId)
        {
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
        /// Stamps a LIVE listener reference (a persistent-call target or an object-mode argument)
        /// into the Unity-free <see cref="StampedReference"/> form
        /// <see cref="SceneBuilder.Core.Identity.ComponentTargetResolution.Classify"/> decides over.
        /// No Component-&gt;GameObject normalization: that belongs to reference FIELDS and stays in
        /// <see cref="BuildFromIndex"/>. <paramref name="resolveGlobalObjectId"/> is REQUIRED so a
        /// caller on the per-keystroke sync path passes a cache rather than silently paying
        /// <see cref="GlobalObjectId.GetGlobalObjectIdSlow"/>.
        /// </summary>
        public static StampedReference StampListenerReference(
            UnityEngine.Object? obj, Func<UnityEngine.Object, string> resolveGlobalObjectId)
        {
            if (obj == null)
            {
                return StampedReference.Dangling;
            }

            var classified = AssetReferenceResolver.ReadObjectReferenceValue(obj, resolveSceneRef: null);
            return classified is ValueNode.AssetRef { Ref: { } assetRef }
                ? StampedReference.Asset(assetRef)
                : StampedReference.Scene(resolveGlobalObjectId(obj));
        }

        /// <summary>
        /// Builds the read-side listener-target resolver a UnityEvent field read threads to
        /// <c>UnityEventReader</c>: stamps the live reference via <see cref="StampListenerReference"/>,
        /// then classifies it through <see cref="ComponentTargetResolution.Classify"/> /
        /// <see cref="ComponentTargetResolution.ToValueNode"/> — a component target resolves to that
        /// COMPONENT's own LogicalId, never its owning GameObject's.
        /// </summary>
        public static Func<UnityEngine.Object?, ValueNode?> BuildListenerResolver(IdentityMap map) =>
            BuildListenerResolver(ComponentTargetIndex.ForMap(map));

        /// <summary>
        /// The one closure implementation behind <see cref="BuildListenerResolver(IdentityMap)"/>,
        /// taking an already-built <see cref="ComponentTargetIndex"/> so <see cref="SceneRefResolver.ForMap"/>
        /// can derive the listener resolver from the SAME index its own generation folds, without
        /// building it twice.
        /// </summary>
        public static Func<UnityEngine.Object?, ValueNode?> BuildListenerResolver(ComponentTargetIndex index) =>
            obj => ComponentTargetResolution.ToValueNode(
                ComponentTargetResolution.Classify(
                    StampListenerReference(obj, o => GlobalObjectId.GetGlobalObjectIdSlow(o).ToString()),
                    index));

        /// <summary>
        /// Resolves a listener's component/GameObject <c>targetLogicalId</c> back to the live
        /// object it names, with NO type coercion (a listener has no declared field type to coerce
        /// to) -- reuses the shipped <see cref="ResolveTarget"/> ladder rather than a parallel copy.
        /// </summary>
        public static UnityEngine.Object? ResolveListenerTarget(
            string targetLogicalId,
            IReadOnlyDictionary<string, GameObject> gameObjectsByLogicalId,
            IReadOnlyDictionary<string, Component> componentsByLogicalId,
            IdentityMap map, Scene scene)
            => ResolveTarget(targetLogicalId, wantedType: null, gameObjectsByLogicalId, componentsByLogicalId, map, scene);

        /// <summary>
        /// Resolves <paramref name="targetLogicalId"/> to a live in-scene object: the execution's
        /// live maps first (a target created THIS Materialize has no GlobalObjectId yet and can ONLY
        /// be found here); falling back to the <see cref="IdentityMap"/>'s GlobalObjectId for an
        /// already-existing, unmodified-this-Materialize object.
        /// </summary>
        internal static UnityEngine.Object? ResolveTarget(
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
        /// native-field case, e.g. <c>Canvas.m_Camera</c>). An object already matching (or an
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
