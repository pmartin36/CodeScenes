#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using UnityAddedComponent = UnityEditor.SceneManagement.AddedComponent;
using UnityRemovedComponent = UnityEditor.SceneManagement.RemovedComponent;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// M10 (specs/11-m10-prefab-overrides.md, research.md b6-t1): classifies each
    /// <see cref="PrefabUtility.GetPropertyModifications"/>/<see cref="PrefabUtility.GetAddedComponents"/>/
    /// <see cref="PrefabUtility.GetRemovedComponents"/> entry into modelled (root name/transform, already
    /// read elsewhere — skipped here), ROOT-target (structured — <see cref="SnapshotNode.Overrides"/>/
    /// <see cref="SnapshotNode.AddedComponents"/>/<see cref="SnapshotNode.RemovedComponents"/>), or
    /// NESTED-target (below the root — deferred, stays opaque via
    /// <see cref="PrefabInstanceProbe.FormatModification"/>, M6). Root-target overrides use the
    /// <c>"type:"+FullName</c> sigil (NOT a real GUID:fileID pair) — see research.md REFINED note; this is
    /// the ONLY reader that must mirror the <c>BuilderParser.Instance</c>/<c>ReconcilerInstances</c> sigil
    /// convention.
    /// </summary>
    internal static partial class PrefabInstanceProbe
    {
        private enum ModTargetKind
        {
            Modelled,
            RootComponent,
            Nested,
        }

        private readonly struct OverrideReadResult
        {
            internal readonly ValueNode.Unsupported? OpaqueOverrides;
            internal readonly PropertyOverride[] StructuredOverrides;
            internal readonly AddedComponent[] AddedComponents;
            internal readonly OverrideTarget[] RemovedComponents;

            internal OverrideReadResult(
                ValueNode.Unsupported? opaqueOverrides,
                PropertyOverride[] structuredOverrides,
                AddedComponent[] addedComponents,
                OverrideTarget[] removedComponents)
            {
                OpaqueOverrides = opaqueOverrides;
                StructuredOverrides = structuredOverrides;
                AddedComponents = addedComponents;
                RemovedComponents = removedComponents;
            }
        }

        private static OverrideReadResult ReadOverrides(GameObject go, GameObject? sourceRoot, Func<UnityEngine.Object, string?>? resolveSceneRef)
        {
            var mods = PrefabUtility.GetPropertyModifications(go) ?? Array.Empty<PropertyModification>();

            var opaqueTokens = new List<string>();
            var records = new List<ModificationRecord>();
            var baseValues = new List<ValueNode?>();

            foreach (var mod in mods)
            {
                if (mod.target == null)
                {
                    continue;
                }

                var kind = ClassifyTarget(sourceRoot, mod.target);
                switch (kind)
                {
                    case ModTargetKind.Modelled:
                        continue;

                    case ModTargetKind.Nested:
                        opaqueTokens.Add(FormatModification(sourceRoot, mod));
                        continue;

                    default:
                        var hasObjectReference = mod.objectReference != null;
                        records.Add(new ModificationRecord
                        {
                            Target = new OverrideTarget { PrefabId = "type:" + mod.target.GetType().FullName, ObjectId = 0 },
                            PropertyPath = mod.propertyPath,
                            Value = hasObjectReference ? null : mod.value,
                            ObjectReference = hasObjectReference
                                ? AssetReferenceResolver.ReadObjectReferenceValue(mod.objectReference!, resolveSceneRef)
                                : null,
                        });
                        baseValues.Add(hasObjectReference ? null : ReadBaseValue(mod));
                        continue;
                }
            }

            var structured = OverrideMapper.ToOverrides(records);
            for (var i = 0; i < structured.Length; i++)
            {
                if (baseValues[i] != null)
                {
                    structured[i] = structured[i] with { BaseValue = baseValues[i] };
                }
            }

            ValueNode.Unsupported? opaque = null;
            if (opaqueTokens.Count > 0)
            {
                opaqueTokens.Sort(StringComparer.Ordinal);
                opaque = new ValueNode.Unsupported(string.Join("\n", opaqueTokens));
            }

            return new OverrideReadResult(opaque, structured, ReadAddedComponents(go, sourceRoot, resolveSceneRef), ReadRemovedComponents(go, sourceRoot));
        }

        // PropertyModification.target is the CORRESPONDING OBJECT IN THE SOURCE ASSET (not the live
        // instance). The root GameObject/name and the root Transform (position/rotation/scale/order)
        // are already modelled elsewhere (SnapshotNode.Name/Transform) — Modelled. A Component whose
        // own transform IS the source root's transform is a ROOT-target override — structured. Anything
        // else (a target below the root) is Nested — deferred, stays opaque (M6).
        private static ModTargetKind ClassifyTarget(GameObject? sourceRoot, UnityEngine.Object target)
        {
            if (sourceRoot == null)
            {
                return ModTargetKind.Nested;
            }

            if (target == (UnityEngine.Object)sourceRoot || target == sourceRoot.transform)
            {
                return ModTargetKind.Modelled;
            }

            var targetTransform = (target as Component)?.transform;
            if (targetTransform != null && targetTransform == sourceRoot.transform)
            {
                return ModTargetKind.RootComponent;
            }

            return ModTargetKind.Nested;
        }

        // The source prefab's CURRENT default at mod.propertyPath, stringified in the SAME encoding as
        // PropertyModification.value — the value b3-t3's stale-override diff compares Value against
        // (InstanceOverrideDiff.cs:47-54). objectReference mods never reach here (no BaseValue, see
        // ReadOverrides).
        private static ValueNode? ReadBaseValue(PropertyModification mod)
        {
            if (mod.target == null)
            {
                return null;
            }

            var so = new SerializedObject(mod.target);
            var prop = so.FindProperty(mod.propertyPath);
            var stringified = StringifyDefault(prop);
            return stringified != null ? new ValueNode.Primitive(PrimitiveKind.String, stringified) : null;
        }

        // Unity's own PropertyModification.value encoding (no public "property -> mod string" API):
        // integer/enum/layer as invariant int, bool as "1"/"0", float as invariant, string raw. A
        // float's exact invariant formatting is a known limitation (not exercised by v0 stale tests,
        // research.md).
        private static string? StringifyDefault(SerializedProperty? prop)
        {
            if (prop == null)
            {
                return null;
            }

            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Enum:
                    return prop.intValue.ToString(CultureInfo.InvariantCulture);

                case SerializedPropertyType.Boolean:
                    return prop.boolValue ? "1" : "0";

                case SerializedPropertyType.Float:
                    return prop.floatValue.ToString(CultureInfo.InvariantCulture);

                case SerializedPropertyType.String:
                    return prop.stringValue;

                case SerializedPropertyType.Character:
                    return prop.intValue.ToString(CultureInfo.InvariantCulture);

                default:
                    return null;
            }
        }

        // PrefabUtility.GetAddedComponents(root) -> root-target entries only (instanceComponent.transform
        // == root.transform); a nested added component is deferred (banner #5, not authored here). The
        // SINGLE root-vs-nested filter — b6-t2's SAVE-side re-read (SceneBuilderBuild.WithGlobalObjectIds)
        // reuses it via <see cref="RootAddedComponents"/> so the boundary is not duplicated.
        private static AddedComponent[] ReadAddedComponents(GameObject go, GameObject? sourceRoot, Func<UnityEngine.Object, string?>? resolveSceneRef)
        {
            if (sourceRoot == null)
            {
                return Array.Empty<AddedComponent>();
            }

            var rootAdded = RootAddedComponents(go);
            if (rootAdded.Length == 0)
            {
                return Array.Empty<AddedComponent>();
            }

            var result = new List<AddedComponent>();
            foreach (var instanceComponent in rootAdded)
            {
                result.Add(new AddedComponent
                {
                    Target = new OverrideTarget(),
                    Component = SerializedFieldBridge.ReadComponent(instanceComponent, resolveSceneRef),
                });
            }

            return result.ToArray();
        }

        /// <summary>
        /// The live ROOT-target added components on <paramref name="go"/> (an instance root) —
        /// <c>instanceComponent.transform == go.transform</c>, the ONE root-vs-nested added-component
        /// filter (M10). Used both by <see cref="ReadAddedComponents"/> (the scene-&gt;code snapshot
        /// read) and by the SAVE-side re-read (b6-t2, <c>SceneBuilderBuild.WithGlobalObjectIds</c>)
        /// that correlates the model's authored <c>AddedComponents[]</c> to their live counterparts to
        /// persist a stable <c>GlobalObjectId</c> per added component.
        /// </summary>
        internal static Component[] RootAddedComponents(GameObject go)
        {
            var added = PrefabUtility.GetAddedComponents(go);
            if (added == null || added.Count == 0)
            {
                return Array.Empty<Component>();
            }

            var result = new List<Component>();
            foreach (UnityAddedComponent entry in added)
            {
                var instanceComponent = entry.instanceComponent;
                if (instanceComponent == null || instanceComponent.transform != go.transform)
                {
                    continue;
                }

                result.Add(instanceComponent);
            }

            return result.ToArray();
        }

        // PrefabUtility.GetRemovedComponents(root) -> root-target entries only (assetComponent.transform
        // == sourceRoot.transform); a nested removed component is deferred (banner #5, not authored here).
        private static OverrideTarget[] ReadRemovedComponents(GameObject go, GameObject? sourceRoot)
        {
            if (sourceRoot == null)
            {
                return Array.Empty<OverrideTarget>();
            }

            var removed = PrefabUtility.GetRemovedComponents(go);
            if (removed == null || removed.Count == 0)
            {
                return Array.Empty<OverrideTarget>();
            }

            var result = new List<OverrideTarget>();
            foreach (UnityRemovedComponent entry in removed)
            {
                var assetComponent = entry.assetComponent;
                if (assetComponent == null || assetComponent.transform != sourceRoot.transform)
                {
                    continue;
                }

                result.Add(new OverrideTarget { PrefabId = "type:" + assetComponent.GetType().FullName, ObjectId = 0 });
            }

            return result.ToArray();
        }
    }
}
