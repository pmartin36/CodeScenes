using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

namespace SceneBuilder.Core.Reconcile
{
    // Snapshot+map-driven component pass for MAPPED owners (owner already has a GameObject
    // IdentityMap entry). Mirrors Reconciler.DetectAppends/DetectRemovals architecturally but
    // does NOT consume Differ's component ChangeOps (those are Materialize-directed: desired
    // side = source -> scene; Reconcile needs the inverse, scene -> source. See
    // b2-t1/research.md Verdict on assumptions).
    internal static class ComponentReconciler
    {
        // b2-t1 fills add/remove/reorder; b2-t2 adds the field-value loop; b2-t3 adds the
        // conflict path. Keep the signature stable across all three.
        internal static void ReconcileComponents(
            string ownerLogicalId,
            ComponentData[] sourceComponents,
            ComponentData[] snapshotComponents,
            IdentityMap identityMap,
            IReadOnlyDictionary<string, SourceSpan>? componentAnchors,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>>? fieldArgumentSpans,
            List<SourceEdit> edits,
            List<IdentityMapEntry> addedEntries,
            List<string> removedLogicalIds,
            List<Conflict> conflicts,
            List<SkippedField> skippedFields)
        {
            var snapshotComps = ExcludeTransform(snapshotComponents);
            var snapshotKeys = ComputeComponentKeys(snapshotComps);
            var snapshotKeySet = new HashSet<(string TypeFullName, int Ordinal)>(snapshotKeys);

            // Managed Component entries already represented on this owner, grouped by type;
            // entry index within its group == its ordinal (mirrors Differ.cs:178-181,223).
            var managedEntriesByType = identityMap.Entries
                .Where(e => e.Kind == "Component" && e.ParentLogicalId == ownerLogicalId)
                .GroupBy(e => e.ComponentType ?? "")
                .ToDictionary(g => g.Key, g => g.ToList());

            var managedKeySet = new HashSet<(string TypeFullName, int Ordinal)>();
            foreach (var group in managedEntriesByType)
            {
                for (var ordinal = 0; ordinal < group.Value.Count; ordinal++)
                {
                    managedKeySet.Add((group.Key, ordinal));
                }
            }

            // (1) ADD: snapshot component with no managed Component entry at its (type,ordinal).
            for (var i = 0; i < snapshotComps.Length; i++)
            {
                var key = snapshotKeys[i];
                if (managedKeySet.Contains(key))
                {
                    continue;
                }

                var componentLogicalId = $"{ownerLogicalId}/{key.TypeFullName}#{key.Ordinal}";

                edits.Add(new AppendComponentStatement
                {
                    Anchor = ownerLogicalId,
                    ComponentLogicalId = componentLogicalId,
                    TypeFullName = key.TypeFullName,
                    Fields = snapshotComps[i].Fields,
                    OwnerHandle = null,
                    IntroduceOwnerHandle = false,
                });

                addedEntries.Add(new IdentityMapEntry
                {
                    LogicalId = componentLogicalId,
                    GlobalObjectId = "",
                    Kind = "Component",
                    ComponentType = key.TypeFullName,
                    ParentLogicalId = ownerLogicalId,
                });
            }

            // (2) REMOVE: managed Component entry with no snapshot component at its (type,ordinal).
            foreach (var group in managedEntriesByType)
            {
                for (var ordinal = 0; ordinal < group.Value.Count; ordinal++)
                {
                    var key = (TypeFullName: group.Key, Ordinal: ordinal);
                    if (snapshotKeySet.Contains(key))
                    {
                        continue;
                    }

                    var entry = group.Value[ordinal];

                    if (componentAnchors != null && !componentAnchors.ContainsKey(entry.LogicalId))
                    {
                        conflicts.Add(ConflictDetector.UnanchorableComponentEdit(entry.LogicalId, "remove"));
                        continue;
                    }

                    edits.Add(new RemoveStatement { Anchor = entry.LogicalId });
                    removedLogicalIds.Add(entry.LogicalId);
                }
            }

            // (3) REORDER: only when the represented set is unchanged (no add/remove above).
            var sourceComps = ExcludeTransform(sourceComponents);
            var sourceKeys = ComputeComponentKeys(sourceComps);
            if (managedKeySet.SetEquals(snapshotKeySet))
            {
                var sourceIndexByKey = new Dictionary<(string TypeFullName, int Ordinal), int>();
                for (var i = 0; i < sourceComps.Length; i++)
                {
                    sourceIndexByKey[sourceKeys[i]] = i;
                }

                for (var j = 0; j < snapshotComps.Length; j++)
                {
                    var key = snapshotKeys[j];
                    if (sourceIndexByKey.TryGetValue(key, out var sourceIndex) && sourceIndex != j)
                    {
                        var reorderedLogicalId = sourceComps[sourceIndex].LogicalId;

                        if (componentAnchors != null && !componentAnchors.ContainsKey(reorderedLogicalId))
                        {
                            conflicts.Add(ConflictDetector.UnanchorableComponentEdit(reorderedLogicalId, "reorder"));
                            continue;
                        }

                        edits.Add(new ReorderStatement { Anchor = reorderedLogicalId, NewSiblingIndex = j });
                    }
                }
            }

            // (4) FIELD-VALUE DIFF: components matched by key in BOTH source model and snapshot.
            // Orthogonal to add/remove/reorder above — runs for every matched pair regardless.
            var sourceByKey = new Dictionary<(string TypeFullName, int Ordinal), ComponentData>();
            for (var i = 0; i < sourceComps.Length; i++)
            {
                sourceByKey[sourceKeys[i]] = sourceComps[i];
            }

            for (var i = 0; i < snapshotComps.Length; i++)
            {
                if (!sourceByKey.TryGetValue(snapshotKeys[i], out var sourceComp))
                {
                    continue;
                }

                var snapshotComp = snapshotComps[i];
                foreach (var (fieldKey, snapVal) in snapshotComp.Fields)
                {
                    if (snapVal is ValueNode.Unsupported)
                    {
                        // Never overwrite; suppresses a would-be introduce too.
                        skippedFields.Add(new SkippedField
                        {
                            LogicalId = sourceComp.LogicalId,
                            Path = fieldKey,
                            Reason = "Unsupported",
                        });

                        continue;
                    }

                    if (sourceComp.Fields.TryGetValue(fieldKey, out var srcVal))
                    {
                        if (Equals(srcVal, snapVal))
                        {
                            continue;
                        }

                        if (fieldArgumentSpans != null
                            && fieldArgumentSpans.TryGetValue(sourceComp.LogicalId, out var compSpans)
                            && compSpans.TryGetValue(fieldKey, out var valueSpan))
                        {
                            edits.Add(new PatchComponentField
                            {
                                Anchor = sourceComp.LogicalId,
                                ValueSpan = valueSpan,
                                NewExpr = SourceExpr.ValueNodeLiteral(snapVal),
                            });
                        }
                        else if (fieldArgumentSpans != null)
                        {
                            // Span absent but the editor supplied field-argument-span data:
                            // non-localizable to a single source construct -> conflict, never
                            // silently dropped.
                            conflicts.Add(ConflictDetector.UnanchorableComponentEdit(sourceComp.LogicalId, $"patch field '{fieldKey}'"));
                        }

                        // fieldArgumentSpans == null: legacy no-op, no conflict.
                    }
                    else
                    {
                        // Newly-detected field: present in snapshot, absent from source.
                        edits.Add(new IntroduceComponentField
                        {
                            Anchor = sourceComp.LogicalId,
                            FieldKey = fieldKey,
                            Value = snapVal,
                        });
                    }
                }
            }
        }

        private static ComponentData[] ExcludeTransform(ComponentData[] components) =>
            components.Where(c => c.Type.FullName != "UnityEngine.Transform").ToArray();

        // Mirrors Differ.ComputeComponentKeys (Differ.cs:257-270) — Differ.cs is out of this
        // slice's touch scope, so the tiny ordinal helper is duplicated here rather than shared.
        private static (string TypeFullName, int Ordinal)[] ComputeComponentKeys(ComponentData[] components)
        {
            var keys = new (string TypeFullName, int Ordinal)[components.Length];
            var ordinalByType = new Dictionary<string, int>();
            for (var i = 0; i < components.Length; i++)
            {
                var typeFullName = components[i].Type.FullName;
                var ordinal = ordinalByType.TryGetValue(typeFullName, out var count) ? count : 0;
                ordinalByType[typeFullName] = ordinal + 1;
                keys[i] = (typeFullName, ordinal);
            }

            return keys;
        }
    }
}
