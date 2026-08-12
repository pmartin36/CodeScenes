#nullable enable
using System;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// THE scene-object identity resolver PAIRED with the generation of the <see cref="IdentityMap"/> it
    /// was derived from. A <see cref="ChangeScopedSnapshot"/> node cache is keyed on <see cref="Generation"/>
    /// so it can never serve a node computed under a stale map. The private constructor and the two static
    /// factories below are the only construction sites in existence — a generation can never be hand-supplied
    /// or omitted.
    /// </summary>
    public sealed class SceneRefResolver
    {
        /// <summary>The explicit "no identity map" answer: scene refs read Unsupported. Its generation is
        /// distinct from every <see cref="ForMap"/> generation, so a cache built with <see cref="None"/> is
        /// never served to a <see cref="ForMap"/> assemble.</summary>
        public static readonly SceneRefResolver None = new SceneRefResolver(null, null, "none");

        public Func<UnityEngine.Object, string?>? Resolve { get; }

        /// <summary>The listener-target resolver a UnityEvent field read threads to <c>UnityEventReader</c>.
        /// Bundled with <see cref="Resolve"/> on the same private constructor — REQUIRED, never
        /// independently supplied — so no caller on this seam can wire scene-object refs without also
        /// wiring listener targets.</summary>
        public Func<UnityEngine.Object?, ValueNode?>? ResolveListener { get; }

        public string Generation { get; }

        private SceneRefResolver(
            Func<UnityEngine.Object, string?>? resolve, Func<UnityEngine.Object?, ValueNode?>? resolveListener,
            string generation)
        {
            Resolve = resolve;
            ResolveListener = resolveListener;
            Generation = generation;
        }

        public static SceneRefResolver ForMap(IdentityMap map)
        {
            var cti = ComponentTargetIndex.ForMap(map);
            return new SceneRefResolver(
                ObjectReferenceResolver.BuildFromIndex(IdentityNodeIndex.GlobalObjectIdToLogicalId(map)),
                ObjectReferenceResolver.BuildListenerResolver(cti),
                "map:" + cti.Generation);
        }
    }
}
