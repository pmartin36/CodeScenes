#nullable enable
using System;
using System.Collections.Generic;
using SceneBuilder.Core.Model;
using UnityEditor;
using UnityEngine;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// The ONE component-creation primitive AND the ONE place a per-type default field
    /// template is built. <see cref="Create"/> is <c>AddComponent</c> plus a named
    /// <c>EditorCreationDefaults</c> overlay for values Unity's own creation MENU assigns after
    /// construction (e.g. a menu-created Canvas is ScreenSpaceOverlay; a raw
    /// <c>AddComponent&lt;Canvas&gt;</c> is WorldSpace).
    /// </summary>
    internal static class ComponentDefaultTemplate
    {
        // The named, measured overlay table: values Unity's own creation
        // MENU assigns after construction that plain AddComponent does not. A named entry recording
        // the MEASUREMENT that put it here, not an inferred rule — one entry today. Applied via
        // SerializedProperty + ApplyModifiedPropertiesWithoutUndo (never a hard-coded ValueNode, and
        // never an Undo record; ObjectFactory is disqualified for that reason).
        private static readonly Dictionary<string, Action<Component>> EditorCreationDefaults = new()
        {
            // A Canvas created through Unity's GameObject/UI/Canvas menu is ScreenSpaceOverlay
            // (m_RenderMode = 0); AddComponent<Canvas> alone leaves it WorldSpace (2) — spec C3.
            ["UnityEngine.Canvas"] = component =>
            {
                var so = new SerializedObject(component);
                var renderMode = so.FindProperty("m_RenderMode");
                if (renderMode != null)
                {
                    renderMode.intValue = 0;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            },
        };

        // Registration attempted (success or failure) per Type.FullName, so a type that cannot be
        // constructed standalone is not retried on every ReadComponent call in this domain.
        private static readonly HashSet<string> Attempted = new();

        // The per-domain template cache TryGet answers from — absent for a type never registered or
        // whose construction failed.
        private static readonly Dictionary<string, FieldMap> TemplatesByTypeName = new();

        // The per-domain UnityEvent member-spelling registry TryGetMemberSpellings answers from,
        // keyed by Type.FullName. First-wins per type: a type's set of spellable UnityEvent fields is
        // a fixed fact of its declaration, so the first component of that type read in this domain
        // settles it. Populated by SerializedFieldBridge.CollectFields, drained by
        // SceneSnapshotReader.FromRoots into SceneSnapshot.MemberSpellings.
        private static readonly Dictionary<string, IReadOnlyList<(string SerializedPath, string PublicName)>> MemberSpellingsByTypeName = new();

        // spec 54: the per-domain safe-member registry TryGetSafeMembers answers from, keyed by
        // Type.FullName. First-wins per type, same reasoning as MemberSpellingsByTypeName.
        // Populated by SerializedFieldBridge.CollectFields, drained by SceneSnapshotReader.FromRoots
        // into SceneSnapshot.SafeMembers.
        private static readonly Dictionary<string, IReadOnlyList<string>> SafeMembersByTypeName = new();

        /// <summary>
        /// THE component-creation primitive: <c>AddComponent</c> plus the <see cref="EditorCreationDefaults"/>
        /// overlay. Returns null when the type cannot be added standalone. Never registers Undo.
        /// Gate-enforced, not just documented: <c>ComponentDefaultTemplateTests.Package_NoRawAddComponentOutsideCreationPrimitive</c>
        /// fails the build if any other package source file calls <c>AddComponent</c> directly.
        /// </summary>
        internal static Component? Create(GameObject owner, Type type)
        {
            var component = owner.AddComponent(type);
            if (component == null)
            {
                return null;
            }

            if (type.FullName != null && EditorCreationDefaults.TryGetValue(type.FullName, out var applyDefaults))
            {
                applyDefaults(component);
            }

            return component;
        }

        /// <summary>
        /// Builds + caches this type's default template (via <see cref="Create"/> on a throwaway
        /// <see cref="HideFlags.HideAndDontSave"/> GameObject) and registers it under
        /// <see cref="Type.FullName"/>. Idempotent, cached per domain. Called by
        /// <see cref="SerializedFieldBridge.ReadComponent"/> for the REGISTRATION, not to filter.
        /// </summary>
        internal static void Register(Type type)
        {
            var fullName = type.FullName;
            if (fullName == null || !Attempted.Add(fullName))
            {
                return;
            }

            GameObject? temp = null;
            try
            {
                temp = new GameObject { hideFlags = HideFlags.HideAndDontSave };

                // Harvesting a type's defaults AddComponents it onto this bare throwaway object, which
                // fires the component's OnValidate on an object with no siblings. Any user MonoBehaviour
                // whose OnValidate logs (e.g. AlignTo warning it has no Renderer to align against)
                // would leak that to the console on every build that touches such a type. Suppress
                // logging NARROWLY around the probe only, and always restore — so every current and
                // future logging OnValidate inherits the fix here, not per-component.
                var logger = Debug.unityLogger;
                var prevLogEnabled = logger.logEnabled;
                logger.logEnabled = false;
                try
                {
                    var component = Create(temp, type);
                    if (component != null)
                    {
                        // Read back through the SAME field walk the live read uses, so template and
                        // creation cannot diverge. A freshly-created component's reference fields are all
                        // null, so a null resolver is the correct, explicit answer here — likewise a
                        // fresh UnityEvent field has no listeners to resolve, so a null listener resolver
                        // projects the same UnityEventListeners([]) a real resolver would.
                        var fields = SerializedFieldBridge.CollectFields(
                            new SerializedObject(component), resolveSceneRef: null, resolveListenerRef: null);
                        TemplatesByTypeName[fullName] = new FieldMap(fields);
                    }
                }
                finally
                {
                    logger.logEnabled = prevLogEnabled;
                }
            }
            catch
            {
                // Construction failed (e.g. requires siblings, not addable standalone) — leave
                // unregistered; TryGet then answers false and nothing is treated as "at default"
                // for this type — never drop user data.
            }
            finally
            {
                if (temp != null)
                {
                    UnityEngine.Object.DestroyImmediate(temp);
                }
            }
        }

        /// <summary>
        /// Moved verbatim from <c>SerializedFieldBridge.TryGetDefaultTemplate</c> — the registry
        /// <see cref="SceneSnapshotReader.FromRoots"/> reads. Absent for a type whose construction
        /// failed or was never registered.
        /// </summary>
        internal static bool TryGet(string typeFullName, out FieldMap fields) =>
            TemplatesByTypeName.TryGetValue(typeFullName, out fields!);

        /// <summary>
        /// Records <paramref name="typeFullName"/>'s serialized-UnityEvent-field public spellings,
        /// first-wins. Called by <see cref="SerializedFieldBridge.CollectFields"/> for every component
        /// read (real or throwaway-template); ignored when <paramref name="spellings"/> is empty, so a
        /// type with no UnityEvent fields never occupies an entry.
        /// </summary>
        internal static void RegisterMemberSpellings(
            string typeFullName, IReadOnlyList<(string SerializedPath, string PublicName)> spellings)
        {
            if (spellings.Count == 0 || MemberSpellingsByTypeName.ContainsKey(typeFullName))
            {
                return;
            }

            MemberSpellingsByTypeName[typeFullName] = spellings;
        }

        /// <summary>
        /// The registry <see cref="SceneSnapshotReader.FromRoots"/> drains into
        /// <see cref="SceneSnapshot.MemberSpellings"/>. Absent for a type with no spellable serialized
        /// UnityEvent field.
        /// </summary>
        internal static bool TryGetMemberSpellings(
            string typeFullName, out IReadOnlyList<(string SerializedPath, string PublicName)> spellings) =>
            MemberSpellingsByTypeName.TryGetValue(typeFullName, out spellings!);

        /// <summary>
        /// spec 54: records <paramref name="typeFullName"/>'s safe-member names (public selectable
        /// members proven to compile as a typed selector), first-wins. Called by
        /// <see cref="SerializedFieldBridge.CollectFields"/> for every component read, INCLUDING a read
        /// that proves zero safe members — an entry (even empty) marks the type INSPECTED, which
        /// <see cref="TryGetSafeMembers"/> and <see cref="SceneSnapshotReader"/> rely on to distinguish
        /// "type read, no safe member" from "type never read" (the self-heal's positive-proof gate).
        /// </summary>
        internal static void RegisterSafeMembers(string typeFullName, IReadOnlyList<string> members)
        {
            if (SafeMembersByTypeName.ContainsKey(typeFullName))
            {
                return;
            }

            SafeMembersByTypeName[typeFullName] = members;
        }

        /// <summary>
        /// The registry <see cref="SceneSnapshotReader.FromRoots"/> drains into
        /// <see cref="SceneSnapshot.SafeMembers"/>/<see cref="SceneSnapshot.InspectedTypes"/>. Absent
        /// for a type never read; present (possibly with zero members) for any type that was.
        /// </summary>
        internal static bool TryGetSafeMembers(string typeFullName, out IReadOnlyList<string> members) =>
            SafeMembersByTypeName.TryGetValue(typeFullName, out members!);
    }
}
