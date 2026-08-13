#nullable enable
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.Serialization;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    // Reads a live [SerializeReference] SerializedProperty into a Core ValueNode (specs/10-m9-serializereference.md).
    // SharedMarker prefixes the Unsupported token emitted for a managed-ref instance shared by two
    // fields, or the back-edge of a self-referencing cycle -- distinct from
    // SceneBuilder.Core.Reconcile.ManagedReferenceMissingType.Marker, so reconcile can tell a
    // missing-type conflict apart from a report-only sharing/cycle skip.
    internal static class ManagedReferenceReader
    {
        public const string SharedMarker = "__csManagedRefShared:";

        // Reads one [SerializeReference] property. A propertyPath the CollectFields pre-scan
        // (ComputeUnsupportedManagedRefPaths) flagged as a sharing/cycle occurrence reads as
        // Unsupported(SharedMarker) WITHOUT descending -- this check never needs its own tree walk.
        // A genuine null (no recorded typename) reads as ManagedReference(null, empty); an
        // unresolvable typename reads as the missing-type marker. A resolved instance's own fields
        // are read through the SAME child-walk ReadNested uses, keyed on the concrete (not
        // declared) type, so nested managed refs and lists compose for free.
        public static ValueNode ReadField(
            SerializedProperty prop,
            System.Func<UnityEngine.Object, string?>? resolveSceneRef,
            ISet<string> unsupportedManagedRefPaths)
        {
            if (unsupportedManagedRefPaths.Contains(prop.propertyPath))
            {
                return new ValueNode.Unsupported(SharedMarker + prop.managedReferenceFullTypename);
            }

            var value = prop.managedReferenceValue;
            if (value == null)
            {
                var fullTypename = prop.managedReferenceFullTypename;
                if (!string.IsNullOrEmpty(fullTypename))
                {
                    // Fully qualified: UnityEditor's OWN ManagedReferenceMissingType (an inspector
                    // helper type) collides on bare name with this Core marker.
                    return SceneBuilder.Core.Reconcile.ManagedReferenceMissingType.Wrap(fullTypename);
                }

                // A recorded reference whose concrete type resolves to no loadable C# type reads back
                // with BOTH managedReferenceFullTypename AND managedReferenceId EMPTY/null-shaped too --
                // verified live: neither field on THIS SerializedProperty distinguishes "never assigned"
                // from "assigned, but the type is gone" once deserialization has already failed.
                // SerializationUtility's own missing-type inventory is the one API that still sees it,
                // so it is consulted as the fallback signal. Object-scoped, not field-scoped: a host
                // with ANOTHER [SerializeReference] field that is genuinely null would also route
                // through here while any field is in this state -- accepted, because the alternative
                // (assuming null) is exactly the bug this exists to close: a live, located value silently
                // treated as absent and patched away rather than surfaced as a conflict.
                var target = prop.serializedObject.targetObject;
                if (target != null && SerializationUtility.HasManagedReferencesWithMissingTypes(target))
                {
                    return SceneBuilder.Core.Reconcile.ManagedReferenceMissingType.Wrap(
                        ResolveMissingTypename(prop));
                }

                return new ValueNode.ManagedReference(null, FieldMap.Empty);
            }

            var concreteType = value.GetType();
            // Bare FullName, no assembly hint -- matches the authored TypeRef byte-for-byte
            // (BuilderParser.SerializeReference.cs), so an idempotent re-Materialize never
            // spuriously diffs a resolved-read TypeRef against the authored one.
            var typeRef = new TypeRef(concreteType.FullName!.Replace('+', '.'));
            var fields = SerializedFieldBridge.ReadManagedInstanceFields(prop, concreteType, resolveSceneRef, unsupportedManagedRefPaths);
            return new ValueNode.ManagedReference(typeRef, new FieldMap(fields));
        }

        // SerializedProperty exposes no per-field breakdown for a missing-type reference (its own
        // managedReferenceFullTypename reads back empty in that state) -- SerializationUtility's
        // object-level inventory is the only source for the type Unity itself recorded as missing.
        // Best-effort: a host with more than one missing-type reference cannot be attributed to THIS
        // field by this API, so the first entry names the conflict; the reconcile decision (a located
        // conflict, never a silent patch) does not depend on which one is named.
        private static string ResolveMissingTypename(SerializedProperty prop)
        {
            var target = prop.serializedObject.targetObject;
            if (target == null)
            {
                return "";
            }

            var missing = SerializationUtility.GetManagedReferencesWithMissingTypes(target);
            if (missing == null || missing.Length == 0)
            {
                return "";
            }

            var entry = missing[0];
            return string.IsNullOrEmpty(entry.namespaceName)
                ? entry.className
                : entry.namespaceName + "." + entry.className;
        }

        // A per-component pre-scan mirroring the shape the real read (ReadField/ReadList/
        // ReadManagedInstanceFields) descends: a managed-ref rid re-encountered while still an
        // ACTIVE ancestor on the current path is a cycle's back-edge -- only THAT occurrence's
        // propertyPath is flagged, the ancestor keeps reading as a normal ManagedReference. A rid
        // re-encountered in a DIFFERENT, already-closed subtree (a sibling field, not an ancestor)
        // is a shared instance -- BOTH occurrences are flagged, since neither owns it uniquely.
        public static ISet<string> ComputeUnsupportedManagedRefPaths(SerializedObject so)
        {
            var unsupported = new HashSet<string>();
            var firstSeenPath = new Dictionary<long, string>();
            var activeRids = new HashSet<long>();

            var it = so.GetIterator();
            var enterChildren = true;
            while (it.Next(enterChildren))
            {
                enterChildren = false; // top-level properties only; VisitSubtree owns its own descent
                VisitSubtree(it.Copy(), unsupported, firstSeenPath, activeRids);
            }

            return unsupported;
        }

        private static void VisitSubtree(
            SerializedProperty prop, HashSet<string> unsupported,
            Dictionary<long, string> firstSeenPath, HashSet<long> activeRids)
        {
            if (prop.propertyType == SerializedPropertyType.ManagedReference)
            {
                var rid = prop.managedReferenceId;
                if (rid == ManagedReferenceUtility.RefIdNull || rid == ManagedReferenceUtility.RefIdUnknown)
                {
                    return; // null / no live instance -- nothing to track
                }

                if (activeRids.Contains(rid))
                {
                    // A currently-open ancestor re-referenced by one of its own descendants -- the
                    // cycle's back-edge. Only THIS occurrence is flagged; the ancestor (still on the
                    // stack, not yet returned) keeps descending normally. Stop here (cycle-safe).
                    unsupported.Add(prop.propertyPath);
                    return;
                }

                if (firstSeenPath.TryGetValue(rid, out var priorPath))
                {
                    // The same instance already read to completion in a DIFFERENT subtree (a
                    // sibling, not an ancestor) -- a shared instance. Neither occurrence is the
                    // "real" owner, so both are flagged.
                    unsupported.Add(priorPath);
                    unsupported.Add(prop.propertyPath);
                    return;
                }

                firstSeenPath[rid] = prop.propertyPath;
                activeRids.Add(rid);
                DescendChildren(prop, unsupported, firstSeenPath, activeRids);
                activeRids.Remove(rid);
                return;
            }

            if (prop.propertyType != SerializedPropertyType.Generic)
            {
                return;
            }

            if (prop.isArray)
            {
                for (var i = 0; i < prop.arraySize; i++)
                {
                    VisitSubtree(prop.GetArrayElementAtIndex(i).Copy(), unsupported, firstSeenPath, activeRids);
                }
            }
            else
            {
                DescendChildren(prop, unsupported, firstSeenPath, activeRids);
            }
        }

        // Same childDepth-bounded walk ReadManagedInstanceFields/ReadNested use, applied here only
        // to find managed-ref descendants -- not to read or map any value.
        private static void DescendChildren(
            SerializedProperty prop, HashSet<string> unsupported,
            Dictionary<long, string> firstSeenPath, HashSet<long> activeRids)
        {
            var it = prop.Copy();
            var end = prop.GetEndProperty();
            var childDepth = prop.depth + 1;
            var enterChildren = true;

            while (it.Next(enterChildren) && !SerializedProperty.EqualContents(it, end))
            {
                enterChildren = false;
                if (it.depth != childDepth)
                {
                    continue;
                }

                VisitSubtree(it.Copy(), unsupported, firstSeenPath, activeRids);
            }
        }
    }
}
