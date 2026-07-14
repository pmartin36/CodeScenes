using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;

namespace SceneBuilder.Core.Reconcile
{
    // Detects reconcile-time conflicts that cannot be resolved into a SourceEdit: ambiguous
    // reorders among synthesized-id siblings (their source statements are only distinguishable
    // by position, and a reorder is exactly what changes position). Reused by Reconciler
    // (b2-t4) when `anchors` is supplied.
    internal static class ConflictDetector
    {
        public static (IReadOnlyList<Conflict> Conflicts, ISet<string> Suppressed) DetectAmbiguousReorders(
            SceneModel model,
            ChangeSet changeSet,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            IReadOnlyDictionary<string, string>? logicalIdToGlobalObjectId = null)
        {
            var reorderedIds = new HashSet<string>(changeSet.Ops.OfType<Reorder>().Select(op => op.LogicalId));
            var conflicts = new List<Conflict>();
            var suppressed = new HashSet<string>();

            Walk(model.Roots, null);

            return (conflicts, suppressed);

            void Walk(GameObjectNode[] siblings, string? parentLogicalId)
            {
                foreach (var group in siblings.GroupBy(n => n.Name))
                {
                    var members = group.ToArray();
                    var allSynthesized = members.Length >= 2
                        && members.All(n => LogicalIdResolver.TryParseSynthesized(n.LogicalId, parentLogicalId, out var parsedName, out _) && parsedName == n.Name);

                    if (allSynthesized && members.Any(n => reorderedIds.Contains(n.LogicalId)))
                    {
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
                            Reason = $"Ambiguous reorder: siblings '{idList}' share name '{group.Key}' with synthesized ids; positional anchor cannot be localized.",
                            Location = anchors.TryGetValue(reorderedMember.LogicalId, out var span) ? span : null,
                        });

                        foreach (var member in members)
                        {
                            suppressed.Add(member.LogicalId);
                        }
                    }
                }

                foreach (var node in siblings)
                {
                    Walk(node.Children, node.LogicalId);
                }
            }
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
    }
}
