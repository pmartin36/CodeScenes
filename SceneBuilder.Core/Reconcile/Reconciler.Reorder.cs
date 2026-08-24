using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // The sibling-order reorder pass's declare-before-use guard. Split out of Reconciler.cs to keep
    // that file under the production line-count budget.
    public static partial class Reconciler
    {
        // The LogicalIds whose sibling reorder is UNACHIEVABLE and so must not be emitted, because an
        // inline forward reference pins their source order against the scene's.
        //
        // A CHAINED component (`scene.Add("A").Component<T>(c => c.Set(field, b))`) is authored inside
        // its owner's own Add-chain statement, so any handle it names must be declared before that
        // statement (declare-before-use, spec 46(b)'s hoist). When the named handle belongs to a
        // SIBLING that the scene orders AFTER the owner, the hoist forces the referenced sibling's
        // declaration to lead the owner's -- the exact opposite of the scene's sibling order. No
        // reorder can reconcile the two: applying one re-breaks declare-before-use and the applier
        // hoists it straight back, so the edit applies byte-identically forever. Both the owner and
        // the referenced sibling are pinned; their existing (hoist-forced) order is the fixed point.
        //
        // A NON-chained component (its own `owner.Component<T>(...)` statement) never pins a sibling:
        // it is free to follow both declarations, so ordinary sibling reorders still apply.
        private static HashSet<string> ForwardReferencePinnedReorders(
            SceneModel expected,
            ISet<string>? chainedComponentSet,
            Dictionary<string, SnapshotEntry> snapshotByGoid,
            Dictionary<string, string> logicalIdToGlobalObjectId)
        {
            var pinned = new HashSet<string>(StringComparer.Ordinal);
            if (chainedComponentSet == null || chainedComponentSet.Count == 0)
            {
                return pinned;
            }

            int? SceneSiblingIndex(string logicalId) =>
                logicalIdToGlobalObjectId.TryGetValue(logicalId, out var goid)
                    && snapshotByGoid.TryGetValue(goid, out var entry)
                    ? entry.SiblingIndex
                    : null;

            void Visit(GameObjectNode[] siblings)
            {
                var siblingIds = new HashSet<string>(siblings.Select(s => s.LogicalId), StringComparer.Ordinal);

                foreach (var owner in siblings)
                {
                    foreach (var component in owner.Components)
                    {
                        if (!chainedComponentSet.Contains(component.LogicalId))
                        {
                            continue;
                        }

                        foreach (var field in component.Fields)
                        {
                            foreach (var targetId in ObjectRefValues.Targets(field.Value))
                            {
                                if (targetId == owner.LogicalId || !siblingIds.Contains(targetId))
                                {
                                    continue;
                                }

                                var ownerScene = SceneSiblingIndex(owner.LogicalId);
                                var targetScene = SceneSiblingIndex(targetId);
                                if (ownerScene == null || targetScene == null)
                                {
                                    continue;
                                }

                                // Declare-before-use forces the referenced sibling ahead; the scene
                                // wants it after. Unreconcilable -- pin both ends.
                                if (targetScene > ownerScene)
                                {
                                    pinned.Add(owner.LogicalId);
                                    pinned.Add(targetId);
                                }
                            }
                        }
                    }

                    Visit(owner.Children);
                }
            }

            Visit(expected.Roots);
            return pinned;
        }
    }
}
