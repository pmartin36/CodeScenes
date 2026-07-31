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
                var component = Create(temp, type);
                if (component != null)
                {
                    // Read back through the SAME field walk the live read uses, so template and
                    // creation cannot diverge.
                    var fields = SerializedFieldBridge.CollectFields(new SerializedObject(component));
                    TemplatesByTypeName[fullName] = new FieldMap(fields);
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
    }
}
