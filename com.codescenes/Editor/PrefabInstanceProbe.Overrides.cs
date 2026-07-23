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
    /// M10 (specs/11-m10-prefab-overrides.md, research.md b6-t1) + m-nested-props b5-t2
    /// (specs/24-nested-prefab-overrides.md): classifies each
    /// <see cref="PrefabUtility.GetPropertyModifications"/>/<see cref="PrefabUtility.GetAddedComponents"/>/
    /// <see cref="PrefabUtility.GetRemovedComponents"/> entry into modelled (root name/transform, already
    /// read elsewhere — skipped here), ROOT-target (structured — <see cref="SnapshotNode.Overrides"/>/
    /// <see cref="SnapshotNode.AddedComponents"/>/<see cref="SnapshotNode.RemovedComponents"/>), or
    /// NESTED-target (below the root). A resolvable nested target (via the lazily-built
    /// source-&gt;live map, <c>PrefabInstanceProbe.Nested.cs</c>) ALSO lowers to the structured
    /// collections with the live sub-object's own SubKey + ChildPath; only a genuinely unresolvable
    /// target still falls back to opaque (<see cref="PrefabInstanceProbe.FormatModification"/> — a
    /// safety net, never a silent drop). Root-target overrides carry <c>SubKey=(0,0)</c> (default
    /// <see cref="PrefabInstanceKey"/>) with <c>ComponentType=FullName</c> — see research.md REFINED
    /// note; this is the ONLY reader that must mirror the
    /// <c>BuilderParser.Instance</c>/<c>ReconcilerInstances</c> root-target convention.
    /// </summary>
    internal static partial class PrefabInstanceProbe
    {
        private enum ModTargetKind
        {
            Modelled,
            RootComponent,
            Nested,
        }

        // Unity bookkeeping property paths on a sub-object — never surfaced as a structured override
        // or leaked to opaque either (mirrors, per-sub-object, the implicit root exclusion the
        // Modelled bucket already gives the instance root's own name/transform).
        private static readonly HashSet<string> NestedBookkeepingPaths = new() { "m_Name", "m_RootOrder" };

        private readonly struct OverrideReadResult
        {
            internal readonly ValueNode.Unsupported? OpaqueOverrides;
            internal readonly PropertyOverride[] StructuredOverrides;
            internal readonly AddedComponent[] AddedComponents;
            internal readonly OverrideTarget[] RemovedComponents;
            internal readonly AddedGameObject[] AddedGameObjects;
            internal readonly OverrideTarget[] RemovedGameObjects;

            internal OverrideReadResult(
                ValueNode.Unsupported? opaqueOverrides,
                PropertyOverride[] structuredOverrides,
                AddedComponent[] addedComponents,
                OverrideTarget[] removedComponents,
                AddedGameObject[] addedGameObjects,
                OverrideTarget[] removedGameObjects)
            {
                OpaqueOverrides = opaqueOverrides;
                StructuredOverrides = structuredOverrides;
                AddedComponents = addedComponents;
                RemovedComponents = removedComponents;
                AddedGameObjects = addedGameObjects;
                RemovedGameObjects = removedGameObjects;
            }
        }

        private static OverrideReadResult ReadOverrides(GameObject go, GameObject? sourceRoot, Func<UnityEngine.Object, string?>? resolveSceneRef)
        {
            var mods = PrefabUtility.GetPropertyModifications(go) ?? Array.Empty<PropertyModification>();

            var opaqueTokens = new List<string>();
            var records = new List<ModificationRecord>();
            var baseValues = new List<ValueNode?>();

            // Lazy: a plain/unmodified instance (the per-keystroke common case) has no below-root mod
            // and never triggers the descent (research.md b5-t2 performance constraint).
            var lazySourceToLive = new Lazy<Dictionary<UnityEngine.Object, GameObject>>(() => BuildSourceToLiveMap(go));

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
                        if (NestedBookkeepingPaths.Contains(mod.propertyPath))
                        {
                            continue;
                        }

                        if (!lazySourceToLive.Value.TryGetValue(mod.target, out var liveTarget) || liveTarget == null)
                        {
                            // Genuinely unresolvable below-root target — safety net, never silently drop.
                            opaqueTokens.Add(FormatModification(sourceRoot, mod));
                            continue;
                        }

                        var nestedHasObjectReference = mod.objectReference != null;
                        records.Add(new ModificationRecord
                        {
                            Target = new OverrideTarget
                            {
                                SubKey = SubKeyOf(liveTarget),
                                ComponentType = mod.target is GameObject ? "" : mod.target.GetType().FullName,
                                ChildPath = RelativePath(go, liveTarget),
                            },
                            PropertyPath = mod.propertyPath,
                            Value = nestedHasObjectReference ? null : mod.value,
                            ObjectReference = nestedHasObjectReference
                                ? AssetReferenceResolver.ReadObjectReferenceValue(mod.objectReference!, resolveSceneRef)
                                : null,
                        });
                        baseValues.Add(nestedHasObjectReference ? null : ReadBaseValue(mod));
                        continue;

                    default:
                        var hasObjectReference = mod.objectReference != null;
                        records.Add(new ModificationRecord
                        {
                            Target = new OverrideTarget { ComponentType = mod.target.GetType().FullName },
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

            return new OverrideReadResult(
                opaque,
                structured,
                ReadAddedComponents(go, sourceRoot, resolveSceneRef),
                ReadRemovedComponents(go, sourceRoot, lazySourceToLive),
                ReadAddedGameObjects(go, resolveSceneRef),
                ReadRemovedGameObjects(go));
        }

        // PropertyModification.target is the CORRESPONDING OBJECT IN THE SOURCE ASSET (not the live
        // instance). The root GameObject/name and the root Transform (position/rotation/scale/order)
        // are already modelled elsewhere (SnapshotNode.Name/Transform) — Modelled. A Component whose
        // own transform IS the source root's transform is a ROOT-target override — structured. Anything
        // else (a target below the root) is Nested — resolved via the source->live map (b5-t2).
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

        // PrefabUtility.GetAddedComponents(root) -> ROOT-target entries (instanceComponent.transform ==
        // root.transform, Target = default/empty OverrideTarget) AND BELOW-root entries (b5-t2):
        // instanceComponent is already LIVE, so its owning sub-object's SubKey/ChildPath is derived
        // directly — no source->live map needed for this reader.
        private static AddedComponent[] ReadAddedComponents(GameObject go, GameObject? sourceRoot, Func<UnityEngine.Object, string?>? resolveSceneRef)
        {
            if (sourceRoot == null)
            {
                return Array.Empty<AddedComponent>();
            }

            var added = PrefabUtility.GetAddedComponents(go);
            if (added == null || added.Count == 0)
            {
                return Array.Empty<AddedComponent>();
            }

            var result = new List<AddedComponent>();
            foreach (UnityAddedComponent entry in added)
            {
                var instanceComponent = entry.instanceComponent;
                if (instanceComponent == null)
                {
                    continue;
                }

                var owner = instanceComponent.transform.gameObject;
                var target = owner == go
                    ? new OverrideTarget()
                    : new OverrideTarget
                    {
                        SubKey = SubKeyOf(owner),
                        ComponentType = "",
                        ChildPath = RelativePath(go, owner),
                    };

                result.Add(new AddedComponent
                {
                    Target = target,
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
        internal static Component[] RootAddedComponents(GameObject go) => AddedComponentsOn(go, go);

        /// <summary>
        /// m-nested-props b6-t1 (specs/24-nested-prefab-overrides.md write mechanics): generalizes
        /// <see cref="RootAddedComponents"/> from "owned by the instance root" to "owned by any resolved
        /// sub-object" — the added-component lookup <c>InstanceOverrideExecutor.Apply(RevertAddedComponent)</c>
        /// needs at depth. <paramref name="outermostRoot"/> is passed to <c>GetAddedComponents</c> (the
        /// whole-instance API); <paramref name="owner"/> filters to the specific live sub-object.
        /// </summary>
        internal static Component[] AddedComponentsOn(GameObject outermostRoot, GameObject owner)
        {
            var added = PrefabUtility.GetAddedComponents(outermostRoot);
            if (added == null || added.Count == 0)
            {
                return Array.Empty<Component>();
            }

            var result = new List<Component>();
            foreach (UnityAddedComponent entry in added)
            {
                var instanceComponent = entry.instanceComponent;
                if (instanceComponent == null || instanceComponent.transform != owner.transform)
                {
                    continue;
                }

                result.Add(instanceComponent);
            }

            return result.ToArray();
        }

        // PrefabUtility.GetRemovedComponents(root) -> ROOT-target entries (assetComponent.transform ==
        // sourceRoot.transform, SubKey=(0,0)) AND BELOW-root entries (b5-t2): assetComponent is
        // SOURCE-side (it no longer exists live), so its owning sub-object's LIVE GameObject — and
        // thence its SubKey — is resolved via the lazily-built source->live map. Unresolvable below-root
        // removed components are dropped (no opaque surface exists for this collection; matches the
        // pre-b5-t2 below-root behavior, never regressing the root case).
        private static OverrideTarget[] ReadRemovedComponents(GameObject go, GameObject? sourceRoot, Lazy<Dictionary<UnityEngine.Object, GameObject>> lazySourceToLive)
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
                if (assetComponent == null)
                {
                    continue;
                }

                if (assetComponent.transform == sourceRoot.transform)
                {
                    result.Add(new OverrideTarget { ComponentType = assetComponent.GetType().FullName });
                    continue;
                }

                var assetOwner = assetComponent.transform.gameObject;
                if (lazySourceToLive.Value.TryGetValue(assetOwner, out var liveOwner) && liveOwner != null)
                {
                    result.Add(new OverrideTarget
                    {
                        SubKey = SubKeyOf(liveOwner),
                        ComponentType = assetComponent.GetType().FullName,
                        ChildPath = RelativePath(sourceRoot, assetOwner),
                    });
                }
            }

            return result.ToArray();
        }
    }
}
