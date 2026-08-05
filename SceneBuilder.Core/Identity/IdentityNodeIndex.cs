using System.Collections.Generic;
using System.Linq;

namespace SceneBuilder.Core.Identity
{
    /// THE node-kind rule for IdentityMap entries, and the two identity indexes built from it.
    /// A scene NODE is an entry that denotes a GameObject in the scene: a plain "GameObject" or a
    /// "PrefabInstance" root. "Component" and every override/added/removed record kind are not.
    public static class IdentityNodeIndex
    {
        public const string GameObject = "GameObject";
        public const string PrefabInstance = "PrefabInstance";
        public const string Component = "Component";

        public static bool IsNode(IdentityMapEntry entry) => IsNode(entry.Kind);

        public static bool IsNode(string? kind) => kind == GameObject || kind == PrefabInstance;

        /// IsNode AND a non-empty GlobalObjectId — "this node has durable scene identity".
        public static bool IsMappedNode(IdentityMapEntry entry) =>
            IsNode(entry) && !string.IsNullOrEmpty(entry.GlobalObjectId);

        /// LogicalId -> GlobalObjectId over mapped nodes.
        public static Dictionary<string, string> LogicalIdToGlobalObjectId(IdentityMap map) =>
            map.Entries
                .Where(IsMappedNode)
                .ToDictionary(e => e.LogicalId, e => e.GlobalObjectId);

        /// GlobalObjectId -> LogicalId over mapped nodes, first-wins on a duplicate goid.
        public static Dictionary<string, string> GlobalObjectIdToLogicalId(IdentityMap map) =>
            map.Entries
                .Where(IsMappedNode)
                .GroupBy(e => e.GlobalObjectId)
                .ToDictionary(g => g.Key, g => g.First().LogicalId);

        /// Order-insensitive content key over the MAPPED-NODE projection — the only part of an IdentityMap a
        /// scene-ref resolver's answers depend on. Equal projections => equal key; any added/removed/retargeted
        /// mapped node => different key. Assets[] and non-node entries do not participate.
        public static string MappedNodeGeneration(IdentityMap map)
            => MappedNodeGeneration(GlobalObjectIdToLogicalId(map));

        public static string MappedNodeGeneration(IReadOnlyDictionary<string, string> goidToLogicalId)
        {
            unchecked
            {
                var total = 0UL;
                foreach (var pair in goidToLogicalId)
                {
                    var hash = FnvOffsetBasis;
                    hash = FoldString(hash, pair.Key);
                    hash = FoldChar(hash, '\0');
                    hash = FoldString(hash, pair.Value);
                    total += hash;
                }

                total += (ulong)goidToLogicalId.Count;
                return total.ToString("x16");
            }
        }

        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private static ulong FoldString(ulong hash, string value)
        {
            foreach (var c in value)
            {
                hash = FoldChar(hash, c);
            }

            return hash;
        }

        private static ulong FoldChar(ulong hash, char c)
        {
            unchecked
            {
                hash ^= c;
                hash *= FnvPrime;
                return hash;
            }
        }
    }
}
