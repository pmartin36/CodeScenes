using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;

namespace SceneBuilder.Core.Reconcile
{
    // Detects conflicts that cannot be resolved into a SourceEdit, and owns the ONE definition of
    // the hazard behind most of them: sibling statements distinguishable only by their POSITION.
    //
    // Its consumers deliberately apply DIFFERENT policies to the same detection, which is why the
    // detection lives here rather than in any one of them:
    //   * BuilderParser.ParseCore  -> reports it on every parse (ParseResult.Ambiguities). The one
    //                                 call both directions reach, so neither can skip the check.
    //   * SceneBuilderBuild        -> REFUSES. Code->scene has no way to guess correctly.
    //   * Reconciler               -> HEALS, by injecting `.Id(...)` (scene->code writes the file).
    //   * DetectAmbiguousReorders  -> surfaces the one case an id cannot rescue: the positional
    //                                 mapping is already scrambled, so there is nothing sound to pin.
    internal static class ConflictDetector
    {
        // THE definition of "this node is distinguishable only by POSITION": its LogicalId encodes
        // its own name + sibling index, i.e. it has neither an authored handle (`var x = ...`, which
        // makes the id the var name) nor an explicit `.Id("...")` (which makes the id that literal).
        // Both of those live IN the statement; a sibling index is only IMPLIED BY the statement's
        // position, which is exactly why a positional id does not survive a statement move.
        //
        // ONE definition, shared by every consumer — the parser's ambiguity report
        // (BuilderParser.ParseCore), the reorder conflict below, and the Reconciler's `.Id(...)`
        // injection — so all three agree on what is ambiguous BY CONSTRUCTION rather than by three
        // hand-kept-in-sync copies of the same shape check.
        internal static bool IsPositional(GameObjectNode node, string? parentLogicalId) =>
            LogicalIdResolver.TryParseSynthesized(node.LogicalId, parentLogicalId, out var parsedName, out _)
            && parsedName == node.Name;

        // Every sibling group that CANNOT be told apart: >= 2 siblings sharing a name whose ids are
        // all positional. Yielded per parent level, pre-order.
        //
        // NOTE the threshold is ">= 2 POSITIONAL members", not "all members are positional". Three
        // siblings named "Enemy" of which one carries an explicit `.Id(...)` still leave TWO that are
        // only distinguishable by position — an `All(...)` test scores that group unambiguous and
        // walks straight past a live instance of the very defect it exists to catch.
        internal static IEnumerable<(string? ParentLogicalId, string Name, GameObjectNode[] PositionalMembers)>
            AmbiguousGroups(SceneModel model)
        {
            var results = new List<(string?, string, GameObjectNode[])>();
            Walk(model.Roots, null);
            return results;

            void Walk(GameObjectNode[] siblings, string? parentLogicalId)
            {
                foreach (var group in siblings.GroupBy(n => n.Name))
                {
                    var positional = group.Where(n => IsPositional(n, parentLogicalId)).ToArray();
                    if (positional.Length >= 2)
                    {
                        results.Add((parentLogicalId, group.Key, positional));
                    }
                }

                foreach (var node in siblings)
                {
                    Walk(node.Children, node.LogicalId);
                }
            }
        }

        // The parse-time report (§7: fail loud, located). Computed on EVERY BuilderParser.Parse — the
        // one call both directions reach — so no caller can route around the detection. Parse does
        // NOT throw on these: Sync must be able to parse an ambiguous file in order to HEAL it by
        // injecting `.Id(...)`. The policy is the consumer's: Build refuses, Sync heals.
        internal static IReadOnlyList<Conflict> DuplicateNameConflicts(
            SceneModel model,
            IReadOnlyDictionary<string, SourceSpan> anchors)
        {
            var conflicts = new List<Conflict>();

            foreach (var (_, name, members) in AmbiguousGroups(model))
            {
                var idList = string.Join("', '", members.Select(n => n.LogicalId));
                conflicts.Add(new Conflict
                {
                    Kind = ConflictKind.AmbiguousAnchor,
                    LogicalId = members[0].LogicalId,
                    Reason =
                        $"Ambiguous duplicate sibling name: {members.Length} siblings named '{name}' ('{idList}') have " +
                        "neither a handle nor an explicit `.Id(\"...\")`, so they are distinguishable only by their " +
                        "position in the file. Any edit that moves a statement would silently re-point identity at a " +
                        "different object. Add `.Id(\"...\")` to each to disambiguate them.",
                    Location = anchors.TryGetValue(members[0].LogicalId, out var span) ? span : null,
                });
            }

            return conflicts;
        }

        // Colliding LogicalIds (b1-t3): two or more nodes whose authored `var` handle / explicit
        // `.Id(...)` resolve to the SAME id. Unlike DuplicateNameConflicts (positional-only ids,
        // always `Name/index`), a collision here is on a HAND-AUTHORED id, so it is disjoint by
        // construction from that detector — a positional id can never appear in this grouping.
        // Ids are GLOBAL identity (not scoped per-parent), so `nodeAnchors` — the un-collapsed,
        // whole-file, document-order list (unlike ParseResult.Anchors, a dict that silently
        // collapses collisions to one entry) — is grouped across the entire file.
        internal static IReadOnlyList<Conflict> DuplicateLogicalIdConflicts(IReadOnlyList<NodeAnchor> nodeAnchors)
        {
            var order = new List<string>();
            var groups = new Dictionary<string, List<NodeAnchor>>(StringComparer.Ordinal);

            foreach (var anchor in nodeAnchors)
            {
                if (!groups.TryGetValue(anchor.LogicalId, out var members))
                {
                    members = new List<NodeAnchor>();
                    groups[anchor.LogicalId] = members;
                    order.Add(anchor.LogicalId);
                }

                members.Add(anchor);
            }

            var conflicts = new List<Conflict>();

            foreach (var logicalId in order)
            {
                var members = groups[logicalId];
                if (members.Count < 2)
                {
                    continue;
                }

                conflicts.Add(new Conflict
                {
                    Kind = ConflictKind.DuplicateLogicalId,
                    LogicalId = logicalId,
                    Reason =
                        $"Duplicate LogicalId '{logicalId}': {members.Count} nodes resolve to the same id. " +
                        "Explicit `.Id(\"...\")` values and `var` handles are a GLOBAL identity, not scoped " +
                        "per-parent, so the same id must not be reused anywhere in the file.",
                    Location = members[1].Span,
                });
            }

            return conflicts;
        }

        public static (IReadOnlyList<Conflict> Conflicts, ISet<string> Suppressed) DetectAmbiguousReorders(
            SceneModel model,
            ChangeSet changeSet,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            IReadOnlyDictionary<string, string>? logicalIdToGlobalObjectId = null)
        {
            var reorderedIds = new HashSet<string>(changeSet.Ops.OfType<Reorder>().Select(op => op.LogicalId));
            var conflicts = new List<Conflict>();
            var suppressed = new HashSet<string>();

            foreach (var (_, name, members) in AmbiguousGroups(model))
            {
                if (!members.Any(n => reorderedIds.Contains(n.LogicalId)))
                {
                    continue;
                }

                var reorderedMember = members.First(n => reorderedIds.Contains(n.LogicalId));
                var idList = string.Join("', '", members.Select(n => n.LogicalId));

                conflicts.Add(new Conflict
                {
                    Kind = ConflictKind.AmbiguousAnchor,
                    LogicalId = reorderedMember.LogicalId,
                    GlobalObjectId = logicalIdToGlobalObjectId != null
                        && logicalIdToGlobalObjectId.TryGetValue(reorderedMember.LogicalId, out var goid)
                        ? goid
                        : null,
                    Reason = $"Ambiguous reorder: siblings '{idList}' share name '{name}' with synthesized ids; positional anchor cannot be localized.",
                    Location = anchors.TryGetValue(reorderedMember.LogicalId, out var span) ? span : null,
                });

                foreach (var member in members)
                {
                    suppressed.Add(member.LogicalId);
                }
            }

            return (conflicts, suppressed);
        }

        public static Conflict MissingAnchor(string logicalId, string? globalObjectId) =>
            new()
            {
                Kind = ConflictKind.MissingSourceAnchor,
                LogicalId = logicalId,
                GlobalObjectId = globalObjectId,
                Reason = $"No source anchor for LogicalId '{logicalId}' (object exists in scene but has no builder statement).",
                Location = null,
            };

        public static Conflict UnanchorableDelete(string logicalId, string? globalObjectId) =>
            new()
            {
                Kind = ConflictKind.MissingSourceAnchor,
                LogicalId = logicalId,
                GlobalObjectId = globalObjectId,
                Reason = $"Cannot remove '{logicalId}': no source anchor/statement to delete (structural change not anchorable to a builder construct).",
                Location = null,
            };

        public static Conflict UnanchorableComponentEdit(string componentLogicalId, string editKind) =>
            new()
            {
                Kind = ConflictKind.MissingSourceAnchor,
                LogicalId = componentLogicalId,
                GlobalObjectId = null,
                Reason = $"Cannot {editKind} for component '{componentLogicalId}': not localizable to a single source construct (§7).",
                Location = null,
            };
    }
}
