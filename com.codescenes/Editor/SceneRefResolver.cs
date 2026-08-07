#nullable enable
using System;
using SceneBuilder.Core.Identity;

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
        public static readonly SceneRefResolver None = new SceneRefResolver(null, "none");

        public Func<UnityEngine.Object, string?>? Resolve { get; }

        public string Generation { get; }

        private SceneRefResolver(Func<UnityEngine.Object, string?>? resolve, string generation)
        {
            Resolve = resolve;
            Generation = generation;
        }

        public static SceneRefResolver ForMap(IdentityMap map)
        {
            var index = IdentityNodeIndex.GlobalObjectIdToLogicalId(map);
            return new SceneRefResolver(
                ObjectReferenceResolver.BuildFromIndex(index),
                "map:" + IdentityNodeIndex.ProjectionGeneration(index));
        }
    }
}
