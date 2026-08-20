#nullable enable
using System;
using System.Collections.Generic;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// The one owner of "what serialized state is not author intent" (spec 32 C6). Both channels that
    /// can turn a serialized property path into builder source — <see cref="SerializedFieldBridge.CollectFields"/>
    /// (live component read + <see cref="ComponentDefaultTemplate"/>'s template build) and
    /// <see cref="PrefabInstanceProbe"/>'s <c>ReadOverrides</c> (<see cref="UnityEditor.PrefabUtility.GetPropertyModifications"/>)
    /// — call <see cref="IsExcluded"/> instead of declaring their own list.
    ///
    /// The rule: a field a user cannot set in the Inspector is not author intent and must not reach
    /// builder source. It is judged against the real Inspector, by name, per DECLARING type — never
    /// by <see cref="UnityEditor.SerializedProperty"/>'s hidden flag, which hides fields users genuinely
    /// author (<c>Canvas.m_SortingOrder</c>, <c>Rigidbody.m_Constraints</c>, <c>m_Enabled</c>) and shows
    /// fields they cannot (<c>SpriteRenderer.m_Size</c>). A property whose root segment is excluded for
    /// its declaring type (or a base type in its chain) is excluded regardless of sub-path
    /// (<c>m_Size.x</c> matches an <c>m_Size</c> entry).
    ///
    /// <c>SpriteRenderer.m_WasSpriteAssigned</c> has no Inspector control at all — Unity sets it itself
    /// on sprite assignment. <c>SpriteRenderer.m_Size</c> is recomputed from the sprite's bounds
    /// whenever a sprite is assigned. Both are TYPE-QUALIFIED, not bare names: nine other component
    /// types declare their own, genuinely authorable, <c>m_Size</c> (<c>BoxCollider</c>,
    /// <c>BoxCollider2D</c>, <c>CapsuleCollider2D</c>, <c>Halo</c>, <c>LODGroup</c>,
    /// <c>OcclusionArea</c>, <c>OcclusionPortal</c>, <c>Tilemap</c>, <c>UnityEngine.UI.Scrollbar</c>) — a
    /// bare-name exclusion would silently stop every one of them round-tripping.
    ///
    /// <c>UnityEngine.Renderer</c> (and separately <c>UnityEngine.Rendering.SortingGroup</c>) serializes
    /// BOTH <c>m_SortingLayer</c> and <c>m_SortingLayerID</c>, while <c>Canvas</c> serializes only the
    /// ID — one Inspector "Sorting Layer" control writing two fields on the types that have both.
    /// <c>m_SortingLayer</c> is a <c>short</c>-typed INDEX into the project's sorting-layer order,
    /// derived by Unity from <c>m_SortingLayerID</c>; writing it alone through
    /// <see cref="UnityEditor.SerializedProperty"/> and <c>ApplyModifiedProperties</c> is discarded and
    /// both fields read back as their default. It is excluded here, keyed on the DECLARING type
    /// (<c>UnityEngine.Renderer</c>, <c>UnityEngine.Rendering.SortingGroup</c>) so every current and
    /// future renderer inherits the rule; <c>m_SortingLayerID</c>, the field the control actually
    /// writes, keeps round-tripping.
    /// </summary>
    internal static class SerializedFieldExclusions
    {
        // Internal Unity bookkeeping — never surfaced as a field (would corrupt diffs). The last three
        // carry the native hidden flag, so an inspector-visibility walk would filter them for free; the
        // walk here enumerates every SERIALIZED property instead (see CollectFields), so they are named
        // explicitly.
        private static readonly HashSet<string> Bookkeeping = new()
        {
            "m_Script",
            "m_ObjectHideFlags",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_GameObject",
            "m_EditorHideFlags",
            "m_EditorClassIdentifier",
            "m_Name",
        };

        // Renderer state Unity REGENERATES from a lightmap bake / static batching / editor selection.
        // It is serialized and hidden, but it is build output, not authored data: round-tripping it
        // would write bake results into the builder source and then push those stale results back onto
        // the scene on the next code->scene rebuild. Excluded by name, deliberately narrow — every
        // OTHER hidden property is captured.
        private static readonly HashSet<string> DerivedBuildState = new()
        {
            "m_LightmapIndex",
            "m_LightmapIndexDynamic",
            "m_LightmapTilingOffset",
            "m_LightmapTilingOffsetDynamic",
            "m_StaticBatchInfo",
            "m_StaticBatchRoot",
            "m_SelectedEditorRenderState",
        };

        // Components authored through a dedicated closed-keyword call (`.FitSize(height: 2f)`,
        // `.SurfaceSnap(down: true)`) rather than `.Component<T>(c => c.Set(...))`. Their authoring
        // grammar has a FIXED argument set, so a field outside it cannot be rendered back into source:
        // the writer emits `.FitSize(m_Enabled: false)` and the parser then rejects its own output.
        // m_Enabled is the only property the serialized walk reaches on them that the grammar cannot
        // express (disabling a FitSize releases its channel — that is what syncs, via the transform).
        private static readonly HashSet<string> ClosedGrammarComponents = new()
        {
            SpatialComponents.FitSizeTypeName,
            SpatialComponents.SurfaceSnapTypeName,
            SpatialComponents.BetweenTypeName,
        };

        // Fields with no Inspector control (or a control that recomputes them) on specific declaring
        // types, keyed by the DECLARING type's full name. Matched against a component's type AND its
        // base chain, so a derived type inherits the rule and a same-named field on an unrelated type
        // does not.
        private static readonly Dictionary<string, HashSet<string>> NotInspectorAuthorable = new()
        {
            ["UnityEngine.SpriteRenderer"] = new() { "m_WasSpriteAssigned", "m_Size" },
            ["UnityEngine.Renderer"] = new() { "m_SortingLayer" },
            ["UnityEngine.Rendering.SortingGroup"] = new() { "m_SortingLayer" },
        };

        // Per-domain memo of the base-chain union for a given concrete type, so a repeated read (every
        // keystroke, via CollectFields) does not re-walk the base chain each time.
        private static readonly Dictionary<Type, HashSet<string>> EffectiveByType = new();

        /// <summary>
        /// True when <paramref name="propertyPath"/> (matched on its ROOT segment, up to the first
        /// '.' or '[') must never reach builder source for <paramref name="ownerType"/>. Required, with
        /// no defaulted overload: a caller that only has a path cannot compile against this method and
        /// silently skip the type-qualified rule.
        /// </summary>
        internal static bool IsExcluded(Type? ownerType, string propertyPath)
        {
            var root = RootSegment(propertyPath);

            if (Bookkeeping.Contains(root) || DerivedBuildState.Contains(root))
            {
                return true;
            }

            if (ownerType != null)
            {
                var fullName = ownerType.FullName;
                if (fullName != null && ClosedGrammarComponents.Contains(fullName) && root == "m_Enabled")
                {
                    return true;
                }

                if (TypeQualifiedExclusions(ownerType).Contains(root))
                {
                    return true;
                }
            }

            return false;
        }

        private static string RootSegment(string propertyPath)
        {
            var dot = propertyPath.IndexOf('.');
            var bracket = propertyPath.IndexOf('[');
            var end = dot < 0 ? bracket : bracket < 0 ? dot : Math.Min(dot, bracket);
            return end < 0 ? propertyPath : propertyPath.Substring(0, end);
        }

        private static HashSet<string> TypeQualifiedExclusions(Type ownerType)
        {
            if (EffectiveByType.TryGetValue(ownerType, out var cached))
            {
                return cached;
            }

            var effective = new HashSet<string>();
            for (var t = ownerType; t != null; t = t.BaseType)
            {
                if (t.FullName != null && NotInspectorAuthorable.TryGetValue(t.FullName, out var entries))
                {
                    effective.UnionWith(entries);
                }
            }

            EffectiveByType[ownerType] = effective;
            return effective;
        }

        /// <summary>
        /// The adapter side of the Core crossing (spec 35 D2): the SAME predicate the live read uses
        /// (<see cref="SerializedFieldBridge.CollectFields"/>), asked with a Core
        /// <see cref="TypeRef"/> instead of a <see cref="Type"/>. Stateless singleton - the code->scene
        /// diff must answer for component types that are not in the live scene at all (the create
        /// path), which neither the resolve nor the rule needs. Populated onto every snapshot at the
        /// one construction factory, <see cref="SceneSnapshotReader.FromRoots"/>; no producer opts in.
        /// An unresolvable type name degrades to the type-independent rules only (Bookkeeping /
        /// DerivedBuildState) - the conservative answer, and unreachable on both live paths because
        /// ComponentTypeNormalizer throws on an unresolved type before any diff.
        /// </summary>
        internal sealed class Policy : IFieldExclusionPolicy
        {
            internal static readonly Policy Instance = new();

            private Policy()
            {
            }

            public bool IsExcluded(TypeRef componentType, string fieldRoot) =>
                SerializedFieldExclusions.IsExcluded(ComponentTypeResolver.Resolve(componentType), fieldRoot);
        }
    }
}
