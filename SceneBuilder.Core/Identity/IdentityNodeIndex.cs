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
    }
}
