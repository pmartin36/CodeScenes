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
        /// (matched by <c>LogicalId</c> + <c>(ComponentType, PropertyPath)</c>), never clobbering an
        /// already-set BaseValue. No-ops when there is no match.
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

                var lookup = entry.Overrides.ToDictionary(
                    r => (r.ComponentType, r.PropertyPath),
                    r => r.BaseValue);

                return instance with
                {
                    Overrides = instance.Overrides
                        .Select(o => RehydrateOverride(o, lookup))
                        .ToArray(),
                };
            }

            return rehydrated;
        }

        private static PropertyOverride RehydrateOverride(
            PropertyOverride @override,
            IReadOnlyDictionary<(string ComponentType, string PropertyPath), string?> lookup)
        {
            if (@override.BaseValue is not null)
            {
                return @override;
            }

            var key = (@override.Target.ComponentType, @override.PropertyPath);
            if (!lookup.TryGetValue(key, out var baseValue) || baseValue is null)
            {
                return @override;
            }

            return @override with { BaseValue = ValueNode.Primitive.String(baseValue) };
        }
    }
}
