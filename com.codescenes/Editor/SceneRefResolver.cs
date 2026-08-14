#nullable enable
using System;
using System.Collections.Generic;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// THE scene-object identity resolver PAIRED with the generation of the <see cref="IdentityMap"/> it
    /// was derived from. A <see cref="ChangeScopedSnapshot"/> node cache is keyed on <see cref="Generation"/>
    /// so it can never serve a node computed under a stale map. The private constructor and the two static
    /// factories below are the only construction sites in existence — a generation can never be hand-supplied
    /// or omitted. Carries INGREDIENTS, not built closures: <see cref="Bind"/> is the only way to obtain
    /// ref/listener resolvers, and it demands a <see cref="GlobalObjectIdCache"/> — there is no cache-free
    /// resolver reachable from this type, so the per-keystroke assemble path (the only path that ever
    /// holds a <see cref="SceneRefResolver"/>, never a bare <see cref="IdentityMap"/>) cannot bypass the
    /// counted seam even by accident.
    /// </summary>
    public sealed class SceneRefResolver
    {
        /// <summary>The explicit "no identity map" answer: scene refs read Unsupported. Its generation is
        /// distinct from every <see cref="ForMap"/> generation, so a cache built with <see cref="None"/> is
        /// never served to a <see cref="ForMap"/> assemble.</summary>
        public static readonly SceneRefResolver None = new SceneRefResolver(null, null, "none");

        private readonly IReadOnlyDictionary<string, string>? _goidToLogicalId;
        private readonly ComponentTargetIndex? _cti;

        public string Generation { get; }

        private SceneRefResolver(
            IReadOnlyDictionary<string, string>? goidToLogicalId, ComponentTargetIndex? cti, string generation)
        {
            _goidToLogicalId = goidToLogicalId;
            _cti = cti;
            Generation = generation;
        }

        public static SceneRefResolver ForMap(IdentityMap map)
        {
            var cti = ComponentTargetIndex.ForMap(map);
            return new SceneRefResolver(
                IdentityNodeIndex.GlobalObjectIdToLogicalId(map), cti, "map:" + cti.Generation);
        }

        /// <summary>
        /// The ONLY way to obtain ref/listener resolvers: binds this instance's stored ingredients to
        /// <paramref name="ids"/>'s counted resolve, so every scene-object slow-resolve on the ref/listener
        /// seam is a <see cref="GlobalObjectIdCache.Resolve"/> call — the same counted cache node identity
        /// already uses. Returns <c>(null, null)</c> for <see cref="None"/> (preserving current null
        /// behavior — scene refs read Unsupported).
        /// </summary>
        public (Func<UnityEngine.Object, string?>? Resolve, Func<UnityEngine.Object?, ValueNode?>? ResolveListener) Bind(
            GlobalObjectIdCache ids)
        {
            if (_goidToLogicalId == null)
            {
                return (null, null);
            }

            return (
                ObjectReferenceResolver.BuildFromIndex(_goidToLogicalId, ids.Resolve),
                ObjectReferenceResolver.BuildListenerResolver(_cti!, ids.Resolve));
        }
    }
}
