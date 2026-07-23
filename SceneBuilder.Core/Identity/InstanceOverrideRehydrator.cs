using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Identity
{
    /// <summary>
    /// M10 (b6-t2 rehydrate seam): threads each persisted <see cref="InstanceOverrideRecord.BaseValue"/>
    /// from the sidecar back onto the matching desired-model <see cref="PropertyOverride.BaseValue"/>
    /// BEFORE Diff/Reconcile, so <c>InstanceOverrideDiff.DetectStaleOverrides</c> (which short-circuits
    /// on a null desired BaseValue) can actually fire the stale-override conflict through the real
    /// adapter, not just hand-built Core POCO tests.
    /// </summary>
    public static class InstanceOverrideRehydrator
    {
        /// <summary>
        /// Rebuilds <paramref name="model"/> so that every <see cref="PropertyOverride"/> on a
        /// <see cref="PrefabInstanceNode"/> whose <c>BaseValue</c> is currently null gets it filled in
        /// from the matching <paramref name="sidecar"/> entry's <see cref="InstanceOverrideRecord"/>
        /// (matched by <c>LogicalId</c> + <c>(ComponentType, PropertyPath)</c>, disambiguated by
        /// <c>SubKey</c> — m-nested-props b4-t3 — WHEN the desired override's <c>Target.SubKey</c> is
        /// already a resolved real per-target key; ChildPath never participates so a rename still
        /// matches), never clobbering an already-set BaseValue. No-ops when there is no match.
        /// </summary>
        public static SceneModel Rehydrate(SceneModel model, IdentityMap sidecar)
        {
            var entriesByLogicalId = sidecar.Entries.ToDictionary(e => e.LogicalId, e => e);
            return model with { Roots = model.Roots.Select(root => RehydrateGameObject(root, entriesByLogicalId)).ToArray() };
        }

        private static GameObjectNode RehydrateGameObject(
            GameObjectNode go,
            IReadOnlyDictionary<string, IdentityMapEntry> entriesByLogicalId)
        {
            var rehydrated = go with
            {
                Children = go.Children.Select(c => RehydrateGameObject(c, entriesByLogicalId)).ToArray(),
            };

            if (rehydrated is PrefabInstanceNode instance)
            {
                if (!entriesByLogicalId.TryGetValue(instance.LogicalId, out var entry)
                    || entry.Kind != "PrefabInstance"
                    || entry.Overrides is null)
                {
                    return instance;
                }

                // Grouped (not a straight ToDictionary) so two records sharing (ComponentType,
                // PropertyPath) across different sub-objects (spec #13) never throw a duplicate-key
                // exception — SubKey disambiguates them at lookup time instead.
                var byComponentPath = entry.Overrides
                    .GroupBy(r => (r.ComponentType, r.PropertyPath))
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<InstanceOverrideRecord>)g.ToList());

                return instance with
                {
                    Overrides = instance.Overrides
                        .Select(o => RehydrateOverride(o, byComponentPath))
                        .ToArray(),
                };
            }

            return rehydrated;
        }

        private static readonly PrefabInstanceKey UnresolvedSubKey = new();

        private static PropertyOverride RehydrateOverride(
            PropertyOverride @override,
            IReadOnlyDictionary<(string ComponentType, string PropertyPath), IReadOnlyList<InstanceOverrideRecord>> byComponentPath)
        {
            if (@override.BaseValue is not null)
            {
                return @override;
            }

            if (!byComponentPath.TryGetValue((@override.Target.ComponentType, @override.PropertyPath), out var candidates))
            {
                return @override;
            }

            // A desired override whose Target.SubKey is already a resolved real per-target key
            // (future b7-t2 authoring) matches its OWN record exactly, so two sub-objects sharing
            // (ComponentType, PropertyPath) never cross-match (spec #13). A still-default SubKey —
            // TODAY's only case; BuilderParser never resolves it at parse time (see
            // BuilderParser.Facade.cs) — falls back to the pre-SubKey behavior: the sole candidate,
            // so a root-target override still threads its record's BaseValue even though the record
            // is persisted with the instance's OWN real key (SceneBuilderBuild.cs), not (0,0).
            var record = @override.Target.SubKey != UnresolvedSubKey
                ? candidates.FirstOrDefault(r => r.SubKey == @override.Target.SubKey)
                : candidates.Count == 1 ? candidates[0] : null;

            if (record?.BaseValue is not { } baseValue)
            {
                return @override;
            }

            return @override with { BaseValue = ValueNode.Primitive.String(baseValue) };
        }
    }
}
