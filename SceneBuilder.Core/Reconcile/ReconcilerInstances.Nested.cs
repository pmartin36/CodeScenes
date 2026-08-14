using System;
using System.Collections.Generic;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // Below-root ("nested", Target.ChildPath != "") membership diff for a
    // MAPPED PrefabInstance — the scoped-`.On` twin of ReconcilerInstances.cs's root emit, plus the
    // child-GameObject add/remove machinery. Split into its
    // own partial-class file per the M10 partial-file precedent.
    public static partial class Reconciler
    {
        // Builds the ONE AppendScopedOn edit for a single nested op at `childPath`. Multiple ops
        // for the same (Anchor, SelectorMatchKey) fold into one `.On(...)` block at apply time
        // (SourcePatchApplier.ScopedOn.cs's ResolveScopedOnAppends) — no grouping needed here.
        private static AppendScopedOn BuildAppendScopedOn(
            string instanceLogicalId,
            string childPath,
            FacadeCatalog? facadeCatalog,
            string? prefabGuid,
            ScopedOp op)
        {
            TryRenderScopedSelector(facadeCatalog, prefabGuid, childPath, out var selectorExpr);

            return new AppendScopedOn
            {
                Anchor = instanceLogicalId,
                SelectorMatchKey = childPath,
                SelectorExpr = selectorExpr,
                Ops = new[] { op },
            };
        }

        // Reverse-resolves a resolved child path ("Turret/Barrel", RealName-joined) to a typed
        // member-chain selector ("sel => sel.Turret.Barrel", PropertyName-joined) by walking the
        // FacadeCatalog entry's FacadeNode.Children by RealName (mirrors
        // FacadeCatalog.TryResolveSelector's forward walk, reversed). A miss at any segment, an
        // absent catalog, or no entry for prefabGuid falls back to the string selector form.
        private static bool TryRenderScopedSelector(FacadeCatalog? facadeCatalog, string? prefabGuid, string childPath, out string selectorExpr)
        {
            if (facadeCatalog != null && !string.IsNullOrEmpty(prefabGuid) && facadeCatalog.TryGetEntryByGuid(prefabGuid, out var entry))
            {
                var node = entry.Root;
                var propertyNames = new List<string>();
                var matchedAll = true;

                foreach (var segment in childPath.Split('/'))
                {
                    FacadeChild? matched = null;
                    foreach (var child in node.Children)
                    {
                        if (child.RealName == segment)
                        {
                            matched = child;
                            break;
                        }
                    }

                    if (matched == null)
                    {
                        matchedAll = false;
                        break;
                    }

                    propertyNames.Add(matched.PropertyName);
                    node = matched.Node;
                }

                if (matchedAll)
                {
                    selectorExpr = "sel => sel." + string.Join(".", propertyNames);
                    return true;
                }
            }

            selectorExpr = SourceExpr.StringLiteral(childPath);
            return false;
        }

        // Scene->code diff for AddedGameObjects: key is (Parent, Node.Name), mirroring
        // InstanceOverrideDiff.EmitAddedGameObjects (materialize direction) with roles reversed —
        // here the SNAPSHOT is truth. A snapshot-only entry appends a top-level `.AddChild(...)`
        // (never inside `.On` — a newly-added child has no resolved facade identity yet); a
        // model-only entry (reverted in Unity) drops the authored call.
        private static void ReconcileAddedGameObjects(
            PrefabInstanceNode model,
            SnapshotNode snapshot,
            string instanceLogicalId,
            FacadeCatalog? facadeCatalog,
            string? prefabGuid,
            List<SourceEdit> edits,
            List<Conflict> conflicts,
            // Scene-derived liveness sets + owner-handle resolver + asset bookkeeping, threaded here
            // so a snapshot-only added child's own component fields get the SAME Unemittable/Dangling/
            // Pending/Resolvable classification the plain-component and added-component append paths
            // already run (ProjectAddedChildNode -> SnapshotFieldEmission.EmitFieldSet).
            ISet<string> resolvableTargets,
            ISet<string> pendingTargets,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            List<AssetEntry> addedAssets,
            AssetCatalog? assetCatalog,
            // A snapshot-only added child's components carry
            // every serialized field the same as any other emission site; filtered here the same way
            // before the AppendInstanceAddChild.Node is built.
            ComponentDefaultOmission.Index? defaults = null)
        {
            var modelKeys = new HashSet<(OverrideTarget Parent, string Name)>();
            foreach (var modelAdded in model.AddedGameObjects)
            {
                modelKeys.Add((modelAdded.Parent, modelAdded.Node.Name));
            }

            var snapshotKeys = new HashSet<(OverrideTarget Parent, string Name)>();
            foreach (var snapshotAdded in snapshot.AddedGameObjects)
            {
                var key = (snapshotAdded.Parent, snapshotAdded.Node.Name);
                snapshotKeys.Add(key);

                if (modelKeys.Contains(key))
                {
                    continue;
                }

                // The PARENT is a resolved source sub-object, so it carries a façade identity — emit
                // the typed selector when the catalog resolves it (root parent "" falls back to the
                // empty string literal). The new child NAME stays a string (specs/27).
                TryRenderScopedSelector(facadeCatalog, prefabGuid, snapshotAdded.Parent.ChildPath, out var parentSelectorExpr);

                // The SAME create-with-payload split the
                // plain-append path uses (Reconciler.SplitCreatedPayload) — an added UI child's
                // layout must ride the SAME AddChild closure a component payload already uses,
                // never be silently dropped. `canHostRectTransformCall: true` — `.AddChild(...)`
                // returns a NodeHandle, which declares `.RectTransform(...)`. `AddedGameObject.Node`
                // is a GameObjectNode, which carries no GlobalObjectId.
                // `.AddChild(...)` is NodeHandle-hosted
                // (canHostRectTransformCall: true makes the baseline unreachable) — state
                // `prefabBaseline: null` rather than omit it.
                var (_, rectTransformPayload) = SplitCreatedPayload(
                    snapshotAdded.Node.Transform,
                    canHostRectTransformCall: true,
                    prefabBaseline: null,
                    instanceLogicalId,
                    globalObjectId: null,
                    conflicts);

                var (projectedNode, childExprs) = ProjectAddedChildNode(
                    snapshotAdded.Node, defaults, instanceLogicalId, resolvableTargets, pendingTargets,
                    resolveOwnerHandle, assetCatalog, addedAssets, edits, conflicts);

                edits.Add(new AppendInstanceAddChild
                {
                    Anchor = instanceLogicalId,
                    ParentPath = snapshotAdded.Parent.ChildPath,
                    ParentSelectorExpr = parentSelectorExpr,
                    Name = snapshotAdded.Node.Name,
                    Node = projectedNode,
                    FieldExpressions = childExprs,
                    RectTransform = rectTransformPayload,
                });
            }

            foreach (var modelAdded in model.AddedGameObjects)
            {
                var key = (modelAdded.Parent, modelAdded.Node.Name);
                if (snapshotKeys.Contains(key))
                {
                    continue;
                }

                edits.Add(new DropInstanceCall
                {
                    Anchor = instanceLogicalId,
                    Kind = InstanceCallKind.AddChild,
                    PropertyPath = modelAdded.Node.Name,
                });
            }
        }

        // Applies ComponentDefaultOmission the same way every other emission site does, then routes
        // each component's remaining fields through the SAME classified choke point
        // BuildAddInstanceComponent uses (SnapshotFieldEmission.EmitFieldSet) before a snapshot-only
        // added child is carried into an AppendInstanceAddChild edit — one of the four emission
        // sites, not covered by ComponentReconciler.EmitComponentAppend. reportUnresolvable is
        // unconditionally true: an added child has no field-value-diff pass, so this call is the
        // ONLY report for a dangling/unemittable field here. Scope stays node.Components only —
        // nested node.Children components are not rendered by RenderAddChildClosure and stay out of
        // scope. Returns the filtered node plus a per-component pre-rendered handle list, aligned
        // 1:1 with the returned node's Components (a null slot = that component needed no
        // pre-rendered field; the whole list null = no component did). Per-component, NOT merged
        // into one flat map — a flat map would let two components with a same-named field (e.g. two
        // scripts each with `target`) collide and silently render the wrong handle.
        private static (GameObjectNode Node, IReadOnlyList<IReadOnlyDictionary<string, string>?>? FieldExpressions) ProjectAddedChildNode(
            GameObjectNode node,
            ComponentDefaultOmission.Index? defaults,
            string instanceLogicalId,
            ISet<string> resolvableTargets,
            ISet<string> pendingTargets,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            AssetCatalog? assetCatalog,
            List<AssetEntry> addedAssets,
            List<SourceEdit> edits,
            List<Conflict> conflicts)
        {
            if (node.Components.Length == 0)
            {
                return (node, null);
            }

            IReadOnlyDictionary<string, string>?[]? perComponent = null;
            var keys = ComponentReconciler.ComputeComponentKeys(node.Components);
            var filtered = new ComponentData[node.Components.Length];
            for (var i = 0; i < node.Components.Length; i++)
            {
                var component = node.Components[i];
                var componentLogicalId = ComponentTargetResolution.ComposeLogicalId(
                    instanceLogicalId, keys[i].TypeFullName, keys[i].Ordinal);
                var typeFullName = component.Type.FullName;
                var fields = ComponentDefaultOmission.OmitDefaults(typeFullName, component.Fields, defaults, componentLogicalId, conflicts);

                var (emittedFields, componentExpressions) = SnapshotFieldEmission.EmitFieldSet(
                    fields, typeFullName, componentLogicalId, instanceLogicalId,
                    resolvableTargets, pendingTargets, resolveOwnerHandle,
                    reportUnresolvable: true, assetCatalog, edits, conflicts);

                foreach (var (_, value) in emittedFields)
                {
                    ComponentReconciler.CollectAssetEntries(value, addedAssets);
                }

                filtered[i] = component with { Fields = emittedFields };

                if (componentExpressions != null)
                {
                    perComponent ??= new IReadOnlyDictionary<string, string>?[node.Components.Length];
                    perComponent[i] = componentExpressions;
                }
            }

            return (node with { Components = filtered }, perComponent);
        }

        // Scene->code diff for RemovedGameObjects: mirrors ReconcileRemovedComponents exactly
        // (HashSet<OverrideTarget>, equality ignores ChildPath).
        private static void ReconcileRemovedGameObjects(
            PrefabInstanceNode model,
            SnapshotNode snapshot,
            string instanceLogicalId,
            FacadeCatalog? facadeCatalog,
            string? prefabGuid,
            List<SourceEdit> edits)
        {
            var modelTargets = new HashSet<OverrideTarget>(model.RemovedGameObjects);
            var snapshotTargets = new HashSet<OverrideTarget>(snapshot.RemovedGameObjects);

            foreach (var target in snapshot.RemovedGameObjects)
            {
                if (modelTargets.Contains(target))
                {
                    continue;
                }

                // The removed child is a resolved source sub-object — emit the typed selector when
                // the catalog resolves it, else the string path (specs/27).
                TryRenderScopedSelector(facadeCatalog, prefabGuid, target.ChildPath, out var selectorExpr);
                edits.Add(new AppendInstanceRemoveChild { Anchor = instanceLogicalId, ChildPath = target.ChildPath, SelectorExpr = selectorExpr });
            }

            foreach (var target in model.RemovedGameObjects)
            {
                if (snapshotTargets.Contains(target))
                {
                    continue;
                }

                edits.Add(new DropInstanceCall
                {
                    Anchor = instanceLogicalId,
                    Kind = InstanceCallKind.RemoveChild,
                    PropertyPath = target.ChildPath,
                });
            }
        }
    }
}
