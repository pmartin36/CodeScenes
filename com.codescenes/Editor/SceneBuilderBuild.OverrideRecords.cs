#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// m-nested-props b7-t1 (specs/24-nested-prefab-overrides.md): builds the FULL, root-vs-nested
    /// discriminated <see cref="InstanceOverrideRecord"/>[] persisted onto a
    /// <c>Kind="PrefabInstance"</c> <see cref="IdentityMapEntry"/>, and splices a stable
    /// <see cref="IdentityMapEntry"/> per added child (+ its components) so a create-with-payload
    /// child keeps its <see cref="GlobalObjectId"/> across reload. Extracted out of
    /// <see cref="SceneBuilderBuild.WithGlobalObjectIds"/> to keep that method lean (research.md
    /// b7-t1).
    /// </summary>
    public static partial class SceneBuilderBuild
    {
        // Root-vs-nested discrimination (the keystone, research.md b7-t1): the read side stamps a
        // ROOT-target override's Target with the DEFAULT OverrideTarget (ChildPath=="", SubKey=(0,0));
        // a NESTED target carries its own real SubKey + non-empty ChildPath. A root record's SubKey is
        // therefore the INSTANCE's own key (instanceRead.Key), never the (unset) target SubKey; a
        // nested record's SubKey is the target's own resolved SubKey.
        private static PrefabInstanceKey RecordSubKey(OverrideTarget target, PrefabInstanceKey instanceKey) =>
            target.ChildPath == "" ? instanceKey : target.SubKey;

        /// <summary>
        /// Every override/added/removed collection on <paramref name="read"/>, lowered to ONE sorted
        /// <see cref="InstanceOverrideRecord"/>[] (root and nested alike). <paramref name="model"/> +
        /// <paramref name="execution"/> supply the AddedGameObject records (built from the model, not
        /// the read side, which discards the live child handle — research.md b7-t1).
        /// </summary>
        private static InstanceOverrideRecord[] BuildOverrideRecords(
            PrefabInstanceProbe.InstanceReadResult read,
            PrefabInstanceNode? model,
            PlanExecutor.ExecutionResult execution)
        {
            var records = new List<InstanceOverrideRecord>();

            foreach (var o in read.StructuredOverrides)
            {
                records.Add(new InstanceOverrideRecord
                {
                    SubKey = RecordSubKey(o.Target, read.Key),
                    ComponentType = o.Target.ComponentType,
                    PropertyPath = o.PropertyPath,
                    Kind = "Property",
                    BaseValue = AsString(o.BaseValue),
                });
            }

            foreach (var a in read.AddedComponents)
            {
                records.Add(new InstanceOverrideRecord
                {
                    SubKey = RecordSubKey(a.Target, read.Key),
                    // Not a.Target.ComponentType — that's "" for an added-component target. The
                    // component's own type lives on the ComponentData (research.md b7-t1).
                    ComponentType = a.Component.Type.FullName,
                    PropertyPath = "",
                    Kind = "AddedComponent",
                    BaseValue = null,
                });
            }

            foreach (var t in read.RemovedComponents)
            {
                records.Add(new InstanceOverrideRecord
                {
                    SubKey = RecordSubKey(t, read.Key),
                    ComponentType = t.ComponentType,
                    PropertyPath = "",
                    Kind = "RemovedComponent",
                    BaseValue = null,
                });
            }

            foreach (var t in read.RemovedGameObjects)
            {
                // Source-side asset key already resolved by the read — no live object, no Entries[]
                // splice for this Kind.
                records.Add(new InstanceOverrideRecord
                {
                    SubKey = t.SubKey,
                    ComponentType = "",
                    PropertyPath = "",
                    Kind = "RemovedGameObject",
                    BaseValue = null,
                });
            }

            if (model != null)
            {
                foreach (var added in model.AddedGameObjects)
                {
                    if (!execution.GameObjectsByLogicalId.TryGetValue(added.Node.LogicalId, out var liveChild)
                        || liveChild == null)
                    {
                        // No live match yet (executor not applied) — no phantom record.
                        continue;
                    }

                    records.Add(new InstanceOverrideRecord
                    {
                        SubKey = PrefabInstanceProbe.SubKeyOf(liveChild),
                        ComponentType = "",
                        PropertyPath = "",
                        Kind = "AddedGameObject",
                        BaseValue = null,
                    });
                }
            }

            return records
                .OrderBy(r => r.SubKey.TargetPrefabId)
                .ThenBy(r => r.SubKey.TargetObjectId)
                .ThenBy(r => r.ComponentType, StringComparer.Ordinal)
                .ThenBy(r => r.PropertyPath, StringComparer.Ordinal)
                .ThenBy(r => r.Kind, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Mirrors the added-ROOT-component Entries[] splice (<see
        /// cref="SceneBuilderBuild.WithGlobalObjectIds"/>) for an added CHILD GameObject: since
        /// <c>CollectIdentityEntries</c> (<see cref="BuilderParser"/>) walks only
        /// <c>node.Children</c>, never <c>node.AddedGameObjects</c>, an added child otherwise has NO
        /// Entries[] entry and its <see cref="GlobalObjectId"/> churns every reload. Splices one
        /// <c>Kind="GameObject"</c> entry per added child (+ one <c>Kind="Component"</c> entry per its
        /// components), walking the model subtree so a multi-level added subtree is fully covered. A
        /// model AddedGameObjects entry with no live match (executor not applied) is skipped — no
        /// phantom entry.
        /// </summary>
        private static IEnumerable<IdentityMapEntry> AddedChildEntries(
            PrefabInstanceNode model,
            string instanceLogicalId,
            PlanExecutor.ExecutionResult execution)
        {
            foreach (var added in model.AddedGameObjects)
            {
                if (!execution.GameObjectsByLogicalId.TryGetValue(added.Node.LogicalId, out var liveChild)
                    || liveChild == null)
                {
                    continue;
                }

                foreach (var entry in AddedChildSubtreeEntries(added.Node, instanceLogicalId, execution))
                {
                    yield return entry;
                }
            }
        }

        private static IEnumerable<IdentityMapEntry> AddedChildSubtreeEntries(
            GameObjectNode node,
            string parentLogicalId,
            PlanExecutor.ExecutionResult execution)
        {
            if (!execution.GameObjectsByLogicalId.TryGetValue(node.LogicalId, out var liveGo) || liveGo == null)
            {
                yield break;
            }

            yield return new IdentityMapEntry
            {
                LogicalId = node.LogicalId,
                GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(liveGo).ToString(),
                Kind = "GameObject",
                Name = node.Name,
                ParentLogicalId = parentLogicalId,
            };

            foreach (var c in node.Components)
            {
                if (!execution.ComponentsByLogicalId.TryGetValue(c.LogicalId, out var liveComp) || liveComp == null)
                {
                    continue;
                }

                yield return new IdentityMapEntry
                {
                    LogicalId = c.LogicalId,
                    GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(liveComp).ToString(),
                    Kind = "Component",
                    ComponentType = c.Type.FullName,
                    ParentLogicalId = node.LogicalId,
                };
            }

            foreach (var child in node.Children)
            {
                foreach (var entry in AddedChildSubtreeEntries(child, node.LogicalId, execution))
                {
                    yield return entry;
                }
            }
        }
    }
}
