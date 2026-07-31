using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;

namespace SceneBuilder.Core.Reconcile
{
    // Scene->code append of a prefab-instance root. Split out of ReconcilerAppends.cs
    // per the project's file-size budget (Reconciler.cs is at 854 lines) — third partial-class
    // file, no visibility change.
    public static partial class Reconciler
    {
        // Mirrors the plain-node branch in DetectAppends (index/parent-handle resolution,
        // headsHandle, AddedEntry shape) minus child recursion (a prefab instance's internal
        // hierarchy is out of scope — M10) and minus the Tag/Layer/Active/Static flag fields
        // (InstanceHandle exposes no such calls).
        private static void HandleInstanceNode(
            SnapshotNode node,
            int siblingIndex,
            string? parentLogicalId,
            bool parentIsMapped,
            SceneModel expected,
            IReadOnlyDictionary<string, string> goidToLogicalId,
            IReadOnlyDictionary<string, GameObjectNode> modelByLogicalId,
            HashSet<string> reserved,
            IReadOnlyDictionary<string, string> prefabPathByGuid,
            // Optional Guid->PropertyName reverse lookup. Present + a catalog hit => emit
            // the typed `Instance(Prefabs.X)` form; absent or a miss => string fallback (unchanged).
            FacadeCatalog? facadeCatalog,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            Dictionary<string, int> nextIndexByParentKey,
            List<SourceEdit> edits,
            List<IdentityMapEntry> addedEntries,
            List<Conflict> conflicts,
            SnapshotNode[] siblings)
        {
            var isMapped = !string.IsNullOrEmpty(node.GlobalObjectId) && goidToLogicalId.ContainsKey(node.GlobalObjectId);
            if (isMapped || !parentIsMapped)
            {
                // Resolves on a later sync, mirroring the plain leaf case (a create candidate under
                // an unmapped parent is stranded until the parent itself is appended).
                return;
            }

            var parentKey = parentLogicalId ?? string.Empty;
            if (!nextIndexByParentKey.TryGetValue(parentKey, out var index))
            {
                index = parentLogicalId == null
                    ? expected.Roots.Length
                    : modelByLogicalId.TryGetValue(parentLogicalId, out var parentModel)
                        ? parentModel.Children.Length
                        : 0;
            }

            nextIndexByParentKey[parentKey] = index + 1;

            // The parent's receiver, via the ONE handle-introduction path (shared with the plain
            // append/reparent/component-attach emission), exactly as the plain branch does.
            var (parentHandle, introduceParentHandle) = resolveOwnerHandle(parentLogicalId);

            if (node.SourcePrefabGuid == null || !prefabPathByGuid.TryGetValue(node.SourcePrefabGuid, out var path))
            {
                // Never emit a statement with no path. Ensuring the source prefab is in
                // identityMap.Assets before Reconcile runs is the adapter's job.
                var provisionalId = LogicalIdResolver.Synthesize(parentHandle, node.Name, index);
                conflicts.Add(new Conflict
                {
                    Kind = ConflictKind.DanglingReference,
                    LogicalId = provisionalId,
                    GlobalObjectId = node.GlobalObjectId,
                    Reason = $"Prefab instance '{node.Name}' references source prefab guid " +
                        $"'{node.SourcePrefabGuid}', which has no known asset path in the identity map.",
                });

                return;
            }

            // Two same-prefab instances share the stem Name -> head a `var` handle so the
            // duplicate-name healer (EnsureNoAmbiguousDuplicateNames) never has to inject an
            // unsupported positional rewrite into an Instance statement.
            var headsHandle = siblings.Count(n => n.Name == node.Name) > 1;

            string newLogicalId;
            string? ownHandle = null;

            if (headsHandle)
            {
                ownHandle = HandleNaming.Derive(node.Name, reserved);
                reserved.Add(ownHandle);
                newLogicalId = ownHandle;
            }
            else
            {
                newLogicalId = LogicalIdResolver.Synthesize(parentHandle, node.Name, index);
            }

            // SourcePrefabPath stays populated regardless (identity/fallback source);
            // SourcePropertyName is the typed-emission preference layered on top, set only on a
            // catalog hit.
            string? sourcePropertyName = facadeCatalog != null
                && facadeCatalog.TryGetPropertyName(node.SourcePrefabGuid, out var propertyName)
                    ? propertyName
                    : null;

            // The THIRD create-payload call site — a prefab-instance root, authored as `.Instance(...)`,
            // can never host `.RectTransform(...)` (InstanceHandle has no such member), so
            // `canHostRectTransformCall: false`; SplitCreatedPayload reports the unlocalizable
            // layout as a Conflict instead of silently dropping it or writing the derived
            // m_LocalPosition into `pos:`. `node.SourcePrefabTransform` is the
            // prefab ASSET's own layout (populated by the adapter at read time) — the ONE site where
            // the baseline is read, so a layout that already matches the asset reports nothing.
            var (transformPayload, _) = SplitCreatedPayload(
                node.Transform,
                canHostRectTransformCall: false,
                prefabBaseline: node.SourcePrefabTransform,
                newLogicalId,
                node.GlobalObjectId,
                conflicts);

            edits.Add(new AppendStatement
            {
                NewLogicalId = newLogicalId,
                ParentAnchor = parentLogicalId,
                NewSiblingIndex = siblingIndex,
                Name = node.Name,
                SourcePrefabPath = path,
                SourcePropertyName = sourcePropertyName,
                Transform = transformPayload != new TransformData() ? transformPayload : null,
                Active = null,
                Tag = null,
                Layer = null,
                IsStatic = null,
                Handle = ownHandle,
                ParentHandle = parentHandle,
                IntroduceParentHandle = introduceParentHandle,
            });

            addedEntries.Add(new IdentityMapEntry
            {
                LogicalId = newLogicalId,
                GlobalObjectId = node.GlobalObjectId,
                Kind = "PrefabInstance",
                ParentLogicalId = parentHandle,
                Name = node.Name,
                SourcePrefabGuid = node.SourcePrefabGuid,
                PrefabKey = node.PrefabKey,
            });
        }

        // Scene->code membership diff for a MAPPED PrefabInstance root's override
        // collections. Reversed-role mirror of InstanceOverrideDiff.Emit (materialize
        // direction): here the SNAPSHOT is truth. snapshot-only entries append the matching
        // SourceEdit; model-only entries (reverted in Unity) DROP the authored call — never a
        // value-edit-to-default. Stale keys (InstanceOverrideDiff.DetectStaleOverrides) are excluded
        // from both.
        private static void ReconcileInstanceOverrides(
            PrefabInstanceNode model,
            SnapshotNode snapshot,
            string instanceLogicalId,
            IReadOnlyDictionary<string, SourceSpan>? anchors,
            // Guid->FacadeEntry reverse lookup for a below-root override's
            // typed `.On(sel => sel.A.B, ...)` selector. Threaded here from Reconciler.cs (already
            // in scope at the call) so nested emit prefers the typed form, falling back to the
            // string selector on a miss/absent catalog.
            FacadeCatalog? facadeCatalog,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            List<SourceEdit> edits,
            List<Conflict> conflicts,
            List<AssetEntry> addedAssets,
            // Catalogued AssetRef fields render as their typed `Assets.<...>` member chain —
            // threaded to BuildOverrideSetSpec/BuildAddInstanceComponent's RenderFieldValue calls.
            AssetCatalog? assetCatalog = null,
            // The ONE per-type default-field index, built ONCE by Reconciler.Reconcile —
            // threaded to ReconcileAddedComponents/BuildAddInstanceComponent for its own
            // instance-emission site.
            ComponentDefaultOmission.Index? defaults = null)
        {
            if (anchors != null && !anchors.ContainsKey(instanceLogicalId))
            {
                conflicts.Add(ConflictDetector.MissingAnchor(instanceLogicalId, snapshot.GlobalObjectId));
                return;
            }

            var staleKeys = new HashSet<(OverrideTarget Target, string PropertyPath)>();
            InstanceOverrideDiff.DetectStaleOverrides(model, snapshot, conflicts, staleKeys);

            var prefabGuid = snapshot.SourcePrefabGuid;

            ReconcileOverrides(model, snapshot, instanceLogicalId, staleKeys, facadeCatalog, prefabGuid, resolveOwnerHandle, edits, conflicts, addedAssets, assetCatalog);
            ReconcileAddedComponents(model, snapshot, instanceLogicalId, facadeCatalog, prefabGuid, resolveOwnerHandle, edits, addedAssets, assetCatalog, defaults);
            ReconcileRemovedComponents(model, snapshot, instanceLogicalId, facadeCatalog, prefabGuid, edits);
            ReconcileAddedGameObjects(model, snapshot, instanceLogicalId, facadeCatalog, prefabGuid, edits, conflicts, defaults);
            ReconcileRemovedGameObjects(model, snapshot, instanceLogicalId, facadeCatalog, prefabGuid, edits);
        }

        // Splits each membership diff by Target.ChildPath. Root
        // (ChildPath=="") keeps the unchanged M10 flat chained-call emit; nested (ChildPath!="")
        // is handled through the scoped `.On` machinery (ReconcilerInstances.Nested.cs). The diff
        // KEY stays (SubKey, ComponentType[, PropertyPath]) either way — never re-keyed by
        // ChildPath — so a sub-object rename (same key, new ChildPath) compares EQUAL and emits
        // neither a drop nor a re-add (spec #2).
        private static void ReconcileOverrides(
            PrefabInstanceNode model,
            SnapshotNode snapshot,
            string instanceLogicalId,
            HashSet<(OverrideTarget Target, string PropertyPath)> staleKeys,
            FacadeCatalog? facadeCatalog,
            string? prefabGuid,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            List<SourceEdit> edits,
            List<Conflict> conflicts,
            List<AssetEntry> addedAssets,
            AssetCatalog? assetCatalog = null)
        {
            var modelByKey = new Dictionary<(OverrideTarget Target, string PropertyPath), PropertyOverride>();
            foreach (var modelOverride in model.Overrides)
            {
                modelByKey[(modelOverride.Target, modelOverride.PropertyPath)] = modelOverride;
            }

            var snapshotKeys = new HashSet<(OverrideTarget Target, string PropertyPath)>();
            var rootSets = new List<OverrideSetSpec>();
            var nestedSetsByChildPath = new Dictionary<string, List<OverrideSetSpec>>();

            foreach (var snapshotOverride in snapshot.Overrides)
            {
                var key = (snapshotOverride.Target, snapshotOverride.PropertyPath);
                snapshotKeys.Add(key);

                if (staleKeys.Contains(key) || modelByKey.ContainsKey(key))
                {
                    continue;
                }

                var setSpec = BuildOverrideSetSpec(snapshotOverride, resolveOwnerHandle, edits, addedAssets, assetCatalog);
                var childPath = snapshotOverride.Target.ChildPath;

                if (string.IsNullOrEmpty(childPath))
                {
                    rootSets.Add(setSpec);
                    continue;
                }

                if (!nestedSetsByChildPath.TryGetValue(childPath, out var nestedSets))
                {
                    nestedSets = new List<OverrideSetSpec>();
                    nestedSetsByChildPath[childPath] = nestedSets;
                }

                nestedSets.Add(setSpec);
            }

            if (rootSets.Count > 0)
            {
                edits.Add(new AppendInstanceOverride { Anchor = instanceLogicalId, Sets = rootSets });
            }

            foreach (var (childPath, nestedSets) in nestedSetsByChildPath)
            {
                edits.Add(BuildAppendScopedOn(instanceLogicalId, childPath, facadeCatalog, prefabGuid, new ScopedOverrideOp { Sets = nestedSets }));
            }

            foreach (var modelOverride in model.Overrides)
            {
                var key = (modelOverride.Target, modelOverride.PropertyPath);
                if (staleKeys.Contains(key) || snapshotKeys.Contains(key))
                {
                    continue;
                }

                var childPath = modelOverride.Target.ChildPath;

                edits.Add(string.IsNullOrEmpty(childPath)
                    ? new DropInstanceCall
                    {
                        Anchor = instanceLogicalId,
                        Kind = InstanceCallKind.Override,
                        PropertyPath = modelOverride.PropertyPath,
                    }
                    : new DropScopedOnCall
                    {
                        Anchor = instanceLogicalId,
                        SelectorMatchKey = childPath,
                        Kind = InstanceCallKind.Override,
                        PropertyPath = modelOverride.PropertyPath,
                    });
            }
        }

        private static void ReconcileAddedComponents(
            PrefabInstanceNode model,
            SnapshotNode snapshot,
            string instanceLogicalId,
            FacadeCatalog? facadeCatalog,
            string? prefabGuid,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            List<SourceEdit> edits,
            List<AssetEntry> addedAssets,
            AssetCatalog? assetCatalog = null,
            ComponentDefaultOmission.Index? defaults = null)
        {
            var modelKeys = new HashSet<(OverrideTarget Target, string TypeFullName)>();
            foreach (var modelComponent in model.AddedComponents)
            {
                modelKeys.Add((modelComponent.Target, modelComponent.Component.Type.FullName));
            }

            var snapshotKeys = new HashSet<(OverrideTarget Target, string TypeFullName)>();
            foreach (var snapshotComponent in snapshot.AddedComponents)
            {
                var typeFullName = snapshotComponent.Component.Type.FullName;
                var key = (snapshotComponent.Target, typeFullName);
                snapshotKeys.Add(key);

                if (modelKeys.Contains(key))
                {
                    continue;
                }

                var appendAddComponent = BuildAddInstanceComponent(snapshotComponent, instanceLogicalId, resolveOwnerHandle, edits, addedAssets, assetCatalog, defaults);
                var childPath = snapshotComponent.Target.ChildPath;

                if (string.IsNullOrEmpty(childPath))
                {
                    edits.Add(appendAddComponent);
                    continue;
                }

                edits.Add(BuildAppendScopedOn(instanceLogicalId, childPath, facadeCatalog, prefabGuid, new ScopedAddComponentOp
                {
                    TypeFullName = appendAddComponent.TypeFullName,
                    Fields = appendAddComponent.Fields,
                    FieldExpressions = appendAddComponent.FieldExpressions,
                }));
            }

            foreach (var modelComponent in model.AddedComponents)
            {
                var typeFullName = modelComponent.Component.Type.FullName;
                var key = (modelComponent.Target, typeFullName);
                if (snapshotKeys.Contains(key))
                {
                    continue;
                }

                var childPath = modelComponent.Target.ChildPath;

                edits.Add(string.IsNullOrEmpty(childPath)
                    ? new DropInstanceCall
                    {
                        Anchor = instanceLogicalId,
                        Kind = InstanceCallKind.AddComponent,
                        TypeFullName = typeFullName,
                    }
                    : new DropScopedOnCall
                    {
                        Anchor = instanceLogicalId,
                        SelectorMatchKey = childPath,
                        Kind = InstanceCallKind.AddComponent,
                        TypeFullName = typeFullName,
                    });
            }
        }

        private static void ReconcileRemovedComponents(
            PrefabInstanceNode model,
            SnapshotNode snapshot,
            string instanceLogicalId,
            FacadeCatalog? facadeCatalog,
            string? prefabGuid,
            List<SourceEdit> edits)
        {
            var modelTargets = new HashSet<OverrideTarget>(model.RemovedComponents);
            var snapshotTargets = new HashSet<OverrideTarget>(snapshot.RemovedComponents);

            foreach (var target in snapshot.RemovedComponents)
            {
                if (modelTargets.Contains(target))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(target.ChildPath))
                {
                    edits.Add(new AppendInstanceRemoveComponent
                    {
                        Anchor = instanceLogicalId,
                        TypeFullName = target.ComponentType,
                    });
                    continue;
                }

                edits.Add(BuildAppendScopedOn(instanceLogicalId, target.ChildPath, facadeCatalog, prefabGuid, new ScopedRemoveComponentOp
                {
                    TypeFullName = target.ComponentType,
                }));
            }

            foreach (var target in model.RemovedComponents)
            {
                if (snapshotTargets.Contains(target))
                {
                    continue;
                }

                edits.Add(string.IsNullOrEmpty(target.ChildPath)
                    ? new DropInstanceCall
                    {
                        Anchor = instanceLogicalId,
                        Kind = InstanceCallKind.RemoveComponent,
                        TypeFullName = target.ComponentType,
                    }
                    : new DropScopedOnCall
                    {
                        Anchor = instanceLogicalId,
                        SelectorMatchKey = target.ChildPath,
                        Kind = InstanceCallKind.RemoveComponent,
                        TypeFullName = target.ComponentType,
                    });
            }
        }

        private static OverrideSetSpec BuildOverrideSetSpec(
            PropertyOverride snapshotOverride,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            List<SourceEdit> edits,
            List<AssetEntry> addedAssets,
            AssetCatalog? assetCatalog = null)
        {
            var renderedValue = snapshotOverride.ObjectReference ?? snapshotOverride.Value;
            var typeFullName = snapshotOverride.Target.ComponentType;

            ComponentReconciler.CollectAssetEntries(renderedValue, addedAssets);

            return new OverrideSetSpec
            {
                TypeFullName = typeFullName,
                PropertyPath = snapshotOverride.PropertyPath,
                Value = snapshotOverride.Value,
                ValueExpression = ComponentReconciler.RenderFieldValue(renderedValue, typeFullName, resolveOwnerHandle, edits, assetCatalog),
            };
        }

        // The added-component twin of BuildOverrideSetSpec — carries the
        // snapshot component's FULL field set into the append, symmetric with materialize's
        // InstanceOverrideDiff.EmitAddedComponents. Scalars/AssetRef stay in Fields (rendered via
        // SourceExpr.ValueNodeLiteral at apply time); each ObjectRef field is pre-rendered into
        // FieldExpressions via ComponentReconciler.RenderFieldValue (same renderer BuildOverrideSetSpec
        // and AppendComponentStatement already use) since ValueNodeLiteral has no ObjectRef arm. Every
        // field's referenced assets are harvested via ComponentReconciler.CollectAssetEntries.
        private static AppendInstanceAddComponent BuildAddInstanceComponent(
            AddedComponent snapshotComponent,
            string instanceLogicalId,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            List<SourceEdit> edits,
            List<AssetEntry> addedAssets,
            AssetCatalog? assetCatalog = null,
            // An added-instance-component field set carries
            // every serialized field the same as EmitComponentAppend's before omission; filter it
            // the same way so a bare `.AddComponent<T>()` emits with no default-field noise.
            ComponentDefaultOmission.Index? defaults = null)
        {
            var component = snapshotComponent.Component;
            var typeFullName = component.Type.FullName;
            var fields = ComponentDefaultOmission.OmitDefaults(typeFullName, component.Fields, defaults);

            Dictionary<string, string>? fieldExpressions = null;
            foreach (var (fieldKey, value) in fields)
            {
                if (value is ValueNode.ObjectRef || ComponentReconciler.IsCataloguedAssetRef(value, assetCatalog))
                {
                    fieldExpressions ??= new Dictionary<string, string>();
                    fieldExpressions[fieldKey] = ComponentReconciler.RenderFieldValue(value, typeFullName, resolveOwnerHandle, edits, assetCatalog);
                }

                ComponentReconciler.CollectAssetEntries(value, addedAssets);
            }

            return new AppendInstanceAddComponent
            {
                Anchor = instanceLogicalId,
                TypeFullName = typeFullName,
                Fields = fields,
                FieldExpressions = fieldExpressions,
            };
        }
    }
}
