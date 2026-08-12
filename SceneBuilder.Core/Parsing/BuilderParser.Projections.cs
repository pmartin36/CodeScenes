using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Parsing
{
    // Tree-projection helpers extracted from BuilderParser.cs for file-size discipline
    // (mirrors the .Instance/.Spatial/.Recognition partial split). These walk the finished
    // NodeBuilder tree to build the ParseResult side-tables; invoked only from ParseCore.
    public static partial class BuilderParser
    {
        // ---- IdentityMap construction -----------------------------------------------------

        // Builds one IdentityMapEntry per parsed node, pre-order (document/declared order),
        // carrying over each node's GlobalObjectId from `existingMap` when its resolved
        // LogicalId matches a persisted entry (so a re-parse never wipes saved ids). A
        // PrefabInstance node also carries over its PrefabKey/SourcePrefabGuid the same way (m6-b4-t2):
        // a moved-but-not-renamed instance (authored handle, LogicalId unchanged) is rebuilt from
        // scratch by CollectIdentityEntries on every parse, so those fields must be re-fetched from
        // `existingMap`, not just GlobalObjectId.
        private static IdentityMap BuildIdentityMap(List<NodeBuilder> roots, IdentityMap? existingMap)
        {
            var globalObjectIdByLogicalId = existingMap?.Entries
                .ToDictionary(e => e.LogicalId, e => e.GlobalObjectId)
                ?? new Dictionary<string, string>();

            var existingInstanceEntriesByLogicalId = existingMap?.Entries
                .Where(e => e.Kind == "PrefabInstance")
                .ToDictionary(e => e.LogicalId)
                ?? new Dictionary<string, IdentityMapEntry>();

            var entries = new List<IdentityMapEntry>();
            for (var i = 0; i < roots.Count; i++)
            {
                CollectIdentityEntries(roots[i], null, i, globalObjectIdByLogicalId, existingInstanceEntriesByLogicalId, entries);
            }

            return new IdentityMap
            {
                SchemaVersion = 1,
                Scene = existingMap?.Scene ?? "",
                Entries = entries.ToArray(),
                Assets = existingMap?.Assets ?? Array.Empty<AssetEntry>(),
            };
        }

        private static void CollectIdentityEntries(NodeBuilder node, string? parentLogicalId, int siblingIndex, Dictionary<string, string> globalObjectIdByLogicalId, Dictionary<string, IdentityMapEntry> existingInstanceEntriesByLogicalId, List<IdentityMapEntry> entries)
        {
            if (node.IsInstance)
            {
                var existingInstanceEntry = existingInstanceEntriesByLogicalId.GetValueOrDefault(node.LogicalId);
                entries.Add(BuildInstanceIdentityEntry(node, parentLogicalId, siblingIndex, globalObjectIdByLogicalId, existingInstanceEntry));

                for (var i = 0; i < node.Children.Count; i++)
                {
                    CollectIdentityEntries(node.Children[i], node.LogicalId, i, globalObjectIdByLogicalId, existingInstanceEntriesByLogicalId, entries);
                }

                return;
            }

            entries.Add(new IdentityMapEntry
            {
                LogicalId = node.LogicalId,
                GlobalObjectId = globalObjectIdByLogicalId.TryGetValue(node.LogicalId, out var globalObjectId) ? globalObjectId : "",
                Kind = "GameObject",
                ComponentType = null,
                ParentLogicalId = parentLogicalId,
                Name = node.Name,
                SiblingIndex = siblingIndex,
            });

            foreach (var component in node.Components)
            {
                entries.Add(new IdentityMapEntry
                {
                    LogicalId = component.LogicalId,
                    GlobalObjectId = globalObjectIdByLogicalId.TryGetValue(component.LogicalId, out var componentGlobalObjectId) ? componentGlobalObjectId : "",
                    Kind = "Component",
                    ComponentType = component.TypeFullName,
                    ParentLogicalId = node.LogicalId,
                });
            }

            for (var i = 0; i < node.Children.Count; i++)
            {
                CollectIdentityEntries(node.Children[i], node.LogicalId, i, globalObjectIdByLogicalId, existingInstanceEntriesByLogicalId, entries);
            }
        }

        // ---- Anchor construction -----------------------------------------------------------

        // Builds one LogicalId->SourceSpan entry per parsed node, pre-order, keyed by each node's
        // FINAL LogicalId (post `.Id(...)` resolution) — mirrors BuildIdentityMap/CollectIdentityEntries.
        private static IReadOnlyDictionary<string, SourceSpan> BuildAnchors(List<NodeBuilder> roots)
        {
            var anchors = new Dictionary<string, SourceSpan>();
            foreach (var root in roots)
            {
                CollectAnchors(root, anchors);
            }

            return anchors;
        }

        private static void CollectAnchors(NodeBuilder node, Dictionary<string, SourceSpan> anchors)
        {
            anchors[node.LogicalId] = node.AnchorSpan;

            foreach (var child in node.Children)
            {
                CollectAnchors(child, anchors);
            }
        }

        // Builds one NodeAnchor per parsed node, pre-order, NEVER collapsed by LogicalId — mirrors
        // BuildAnchors/CollectAnchors but appends to a List (not a dict-indexer assignment), so two
        // nodes resolving to the same LogicalId (a colliding hand-authored `.Id(...)`) both survive.
        private static IReadOnlyList<NodeAnchor> BuildNodeAnchors(List<NodeBuilder> roots)
        {
            var nodeAnchors = new List<NodeAnchor>();
            foreach (var root in roots)
            {
                CollectNodeAnchors(root, nodeAnchors);
            }

            return nodeAnchors;
        }

        private static void CollectNodeAnchors(NodeBuilder node, List<NodeAnchor> nodeAnchors)
        {
            nodeAnchors.Add(new NodeAnchor
            {
                LogicalId = node.LogicalId,
                Name = node.Name,
                Span = node.AnchorSpan,
                Handle = node.Handle,
                IdCallSpan = node.IdCallSpan,
            });

            foreach (var child in node.Children)
            {
                CollectNodeAnchors(child, nodeAnchors);
            }
        }

        // Builds one LogicalId->handle(var) name entry per parsed node with an AUTHORED handle
        // (NodeBuilder.Handle set only at the two ctx.Handles[handleName]=node registration
        // spots), pre-order, keyed by each node's FINAL LogicalId — mirrors
        // BuildAnchors/CollectAnchors, but OMITS nodes without an authored Handle.
        private static IReadOnlyDictionary<string, string> BuildHandles(List<NodeBuilder> roots)
        {
            var handles = new Dictionary<string, string>();
            foreach (var root in roots)
            {
                CollectHandles(root, handles);
            }

            return handles;
        }

        private static void CollectHandles(NodeBuilder node, Dictionary<string, string> handles)
        {
            if (node.Handle != null)
            {
                handles[node.LogicalId] = node.Handle;
            }

            foreach (var child in node.Children)
            {
                CollectHandles(child, handles);
            }
        }

        // Builds one component-LogicalId->SourceSpan entry per parsed component, pre-order,
        // kept SEPARATE from BuildAnchors/Anchors (GameObject-only).
        private static IReadOnlyDictionary<string, SourceSpan> BuildComponentAnchors(List<NodeBuilder> roots)
        {
            var anchors = new Dictionary<string, SourceSpan>();
            foreach (var root in roots)
            {
                CollectComponentAnchors(root, anchors);
            }

            return anchors;
        }

        private static void CollectComponentAnchors(NodeBuilder node, Dictionary<string, SourceSpan> anchors)
        {
            foreach (var component in node.Components)
            {
                anchors[component.LogicalId] = component.AnchorSpan;
            }

            foreach (var child in node.Children)
            {
                CollectComponentAnchors(child, anchors);
            }
        }

        // Builds one entry per parsed component whose source construct is a CHAINED call
        // (`!StatementAnchored`), value = the component's LogicalId — mirrors
        // BuildComponentAnchors/CollectComponentAnchors exactly, but collects a HashSet of ids
        // instead of an anchor dictionary. Feeds ComponentReconciler's REORDER-pass guard (b3-t5).
        private static IReadOnlyCollection<string> BuildChainedComponents(List<NodeBuilder> roots)
        {
            var chained = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                CollectChainedComponents(root, chained);
            }

            return chained;
        }

        private static void CollectChainedComponents(NodeBuilder node, HashSet<string> chained)
        {
            foreach (var component in node.Components)
            {
                if (!component.StatementAnchored)
                {
                    chained.Add(component.LogicalId);
                }
            }

            foreach (var child in node.Children)
            {
                CollectChainedComponents(child, chained);
            }
        }

        // Builds one componentLogicalId -> (fieldKey -> value SourceSpan) entry per parsed
        // component, pre-order (mirrors BuildComponentAnchors/CollectComponentAnchors) — feed-
        // forward for b5's span-local field-argument patching.
        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> BuildFieldArgumentSpans(List<NodeBuilder> roots)
        {
            var spans = new Dictionary<string, IReadOnlyDictionary<string, SourceSpan>>();
            foreach (var root in roots)
            {
                CollectFieldArgumentSpans(root, spans);
            }

            return spans;
        }

        private static void CollectFieldArgumentSpans(NodeBuilder node, Dictionary<string, IReadOnlyDictionary<string, SourceSpan>> spans)
        {
            foreach (var component in node.Components)
            {
                var fieldSpans = new Dictionary<string, SourceSpan>();
                foreach (var (key, span) in component.FieldValueSpans)
                {
                    fieldSpans[key] = span;
                }

                spans[component.LogicalId] = fieldSpans;
            }

            foreach (var child in node.Children)
            {
                CollectFieldArgumentSpans(child, spans);
            }
        }

        // Builds one componentLogicalId -> (fieldKey -> ORDERED per-listener call SourceSpan list)
        // entry per parsed component, pre-order (mirrors BuildFieldArgumentSpans/
        // CollectFieldArgumentSpans) — feed-forward for Reconcile's in-place listener patch/remove,
        // keyed by array index within its field.
        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SourceSpan>>> BuildListenerCallSpans(List<NodeBuilder> roots)
        {
            var spans = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SourceSpan>>>();
            foreach (var root in roots)
            {
                CollectListenerCallSpans(root, spans);
            }

            return spans;
        }

        private static void CollectListenerCallSpans(NodeBuilder node, Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SourceSpan>>> spans)
        {
            foreach (var component in node.Components)
            {
                var byField = new Dictionary<string, List<SourceSpan>>();
                foreach (var (key, span) in component.ListenerCallSpans)
                {
                    if (!byField.TryGetValue(key, out var list))
                    {
                        list = new List<SourceSpan>();
                        byField[key] = list;
                    }

                    list.Add(span);
                }

                spans[component.LogicalId] = byField.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SourceSpan>)kv.Value);
            }

            foreach (var child in node.Children)
            {
                CollectListenerCallSpans(child, spans);
            }
        }

        // ---- Flag presence construction -----------------------------------------------------

        // Builds one LogicalId->FlagPresence entry per parsed node, pre-order, keyed by each
        // node's FINAL LogicalId (post `.Id(...)` resolution) — mirrors BuildAnchors/CollectAnchors.
        private static IReadOnlyDictionary<string, FlagPresence> BuildFlagPresence(List<NodeBuilder> roots)
        {
            var presence = new Dictionary<string, FlagPresence>();
            foreach (var root in roots)
            {
                CollectFlagPresence(root, presence);
            }

            return presence;
        }

        private static void CollectFlagPresence(NodeBuilder node, Dictionary<string, FlagPresence> presence)
        {
            presence[node.LogicalId] = new FlagPresence(node.HasTag, node.HasLayer, node.HasActive, node.HasStatic);

            foreach (var child in node.Children)
            {
                CollectFlagPresence(child, presence);
            }
        }
    }
}
