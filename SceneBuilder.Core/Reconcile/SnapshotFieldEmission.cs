using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    /// <summary>
    /// The one owner of "emit a snapshot component's whole field set into an appended authoring
    /// call". Dispatches every field through <see cref="ComponentReconciler.ClassifySnapshotRef"/>:
    /// an <c>Unemittable</c> or <c>Dangling</c> value is omitted and reported; a <c>Pending</c> value
    /// is omitted silently (converges on the guaranteed second sync); a <c>Resolvable</c>/
    /// <c>NotObjectRef</c> value stays in the emitted set and is pre-rendered into
    /// <c>FieldExpressions</c> when <see cref="ComponentReconciler.NeedsPreRender"/> demands it.
    /// Shared by <see cref="ComponentReconciler.EmitComponentAppend"/> (the plain-component append)
    /// and <c>Reconciler.BuildAddInstanceComponent</c> (the prefab-instance added-component append)
    /// so the classification runs exactly once for both.
    /// </summary>
    internal static class SnapshotFieldEmission
    {
        internal static (FieldMap Fields, Dictionary<string, string>? FieldExpressions) EmitFieldSet(
            FieldMap fields,
            string typeFullName,
            string componentLogicalId,
            string ownerNodeLogicalId,
            ISet<string> resolvableTargets,
            ISet<string> pendingTargets,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            bool reportUnresolvable,
            AssetCatalog? assetCatalog,
            List<SourceEdit> edits,
            List<Conflict> conflicts)
        {
            List<KeyValuePair<string, ValueNode>>? filteredFields = null;
            Dictionary<string, string>? fieldExpressions = null;

            for (var i = 0; i < fields.Count; i++)
            {
                var (fieldKey, value) = fields[i];
                var classification = ComponentReconciler.ClassifySnapshotRef(value, resolvableTargets, pendingTargets);

                if (classification.Resolution == ComponentReconciler.RefResolution.Unemittable)
                {
                    filteredFields ??= new List<KeyValuePair<string, ValueNode>>(fields.Take(i));
                    if (reportUnresolvable)
                    {
                        conflicts.Add(ConflictDetector.UnrepresentableListItem(componentLogicalId, typeFullName, fieldKey, null));
                    }

                    continue;
                }

                if (classification.Resolution == ComponentReconciler.RefResolution.Dangling)
                {
                    filteredFields ??= new List<KeyValuePair<string, ValueNode>>(fields.Take(i));
                    if (reportUnresolvable)
                    {
                        conflicts.Add(ConflictDetector.DanglingReference(componentLogicalId, fieldKey, classification.DanglingTarget, null));
                    }

                    continue;
                }

                if (classification.Resolution == ComponentReconciler.RefResolution.Pending)
                {
                    filteredFields ??= new List<KeyValuePair<string, ValueNode>>(fields.Take(i));
                    continue;
                }

                filteredFields?.Add(new KeyValuePair<string, ValueNode>(fieldKey, value));

                if (ComponentReconciler.NeedsPreRender(value, assetCatalog))
                {
                    fieldExpressions ??= new Dictionary<string, string>();
                    fieldExpressions[fieldKey] = ComponentReconciler.RenderFieldValue(
                        value, typeFullName, resolveOwnerHandle, edits, ownerNodeLogicalId, assetCatalog);
                }
            }

            return (filteredFields != null ? new FieldMap(filteredFields) : fields, fieldExpressions);
        }
    }
}
