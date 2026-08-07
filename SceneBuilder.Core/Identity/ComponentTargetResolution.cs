using System.Collections.Generic;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Identity
{
    // What the adapter hands Core after stamping a live listener reference (a persistent-call
    // target or an object-mode argument). Closed via three private-constructor factories: "asset
    // AND scene at once" and "neither, but not null" are unrepresentable.
    public readonly record struct StampedReference
    {
        private StampedReference(string? globalObjectId, AssetRef? assetRef)
        {
            GlobalObjectId = globalObjectId;
            AssetRef = assetRef;
        }

        public static StampedReference Dangling => new(null, null);

        public static StampedReference Scene(string globalObjectId) => new(globalObjectId, null);

        public static StampedReference Asset(AssetRef assetRef) => new(null, assetRef);

        public string? GlobalObjectId { get; }

        public AssetRef? AssetRef { get; }
    }

    // The total, discriminated answer ComponentTargetResolution.Classify produces. Never a
    // nullable string: Asset and DanglingNull are distinct inhabitants.
    public abstract record ListenerTarget
    {
        public sealed record SceneObject(string LogicalId) : ListenerTarget;

        public sealed record Asset(AssetRef Ref) : ListenerTarget;

        public sealed record DanglingNull : ListenerTarget;

        public sealed record Unresolvable(string GlobalObjectId) : ListenerTarget;
    }

    // Order-insensitive content key over BOTH the mapped-node and mapped-component projections of
    // an IdentityMap -- the input a cached listener-target resolution must invalidate on.
    public sealed class ComponentTargetIndex
    {
        private ComponentTargetIndex(
            IReadOnlyDictionary<string, string> componentGoidToLogicalId,
            IReadOnlyDictionary<string, string> nodeGoidToLogicalId,
            string generation)
        {
            ComponentGoidToLogicalId = componentGoidToLogicalId;
            NodeGoidToLogicalId = nodeGoidToLogicalId;
            Generation = generation;
        }

        internal IReadOnlyDictionary<string, string> ComponentGoidToLogicalId { get; }

        internal IReadOnlyDictionary<string, string> NodeGoidToLogicalId { get; }

        public string Generation { get; }

        public static ComponentTargetIndex ForMap(IdentityMap map)
        {
            var componentProjection = IdentityNodeIndex.GlobalObjectIdToComponentLogicalId(map);
            var nodeProjection = IdentityNodeIndex.GlobalObjectIdToLogicalId(map);
            return new ComponentTargetIndex(
                componentProjection, nodeProjection, CombinedGeneration(componentProjection, nodeProjection));
        }

        // Folds BOTH projections into one order-insensitive key: prefixing each goid by its
        // projection ("N:"/"C:") before hashing keeps them from colliding even in the impossible
        // case of an identical goid string appearing in both (a component's own goid always
        // differs from its owner's).
        private static string CombinedGeneration(
            IReadOnlyDictionary<string, string> componentProjection,
            IReadOnlyDictionary<string, string> nodeProjection)
        {
            var merged = new Dictionary<string, string>(componentProjection.Count + nodeProjection.Count);
            foreach (var pair in nodeProjection)
            {
                merged["N:" + pair.Key] = pair.Value;
            }

            foreach (var pair in componentProjection)
            {
                merged["C:" + pair.Key] = pair.Value;
            }

            return IdentityNodeIndex.ProjectionGeneration(merged);
        }
    }

    // The milestone's one cross-cutting mechanism: a listener target resolves to a component's own
    // LogicalId, never its owning GameObject's.
    public static class ComponentTargetResolution
    {
        // Total over every StampedReference shape. The mapped-component projection is consulted
        // BEFORE the mapped-node one -- the invariant this whole task exists to enforce.
        public static ListenerTarget Classify(StampedReference reference, ComponentTargetIndex index)
        {
            if (reference.AssetRef is not null)
            {
                return new ListenerTarget.Asset(reference.AssetRef);
            }

            var goid = reference.GlobalObjectId;
            if (goid is null)
            {
                return new ListenerTarget.DanglingNull();
            }

            if (index.ComponentGoidToLogicalId.TryGetValue(goid, out var componentLogicalId))
            {
                return new ListenerTarget.SceneObject(componentLogicalId);
            }

            if (index.NodeGoidToLogicalId.TryGetValue(goid, out var nodeLogicalId))
            {
                return new ListenerTarget.SceneObject(nodeLogicalId);
            }

            return new ListenerTarget.Unresolvable(goid);
        }

        // THE lowering into a listener's Target/object-mode ArgValue slot -- the only site in the
        // tree that constructs a ValueNode.ObjectRef for a listener (enforced by the shipped
        // ObjectRef descent scan).
        public static ValueNode? ToValueNode(ListenerTarget target) => target switch
        {
            ListenerTarget.SceneObject sceneObject => new ValueNode.ObjectRef(sceneObject.LogicalId),
            ListenerTarget.Asset asset => new ValueNode.AssetRef(asset.Ref),
            ListenerTarget.DanglingNull => null,
            ListenerTarget.Unresolvable unresolvable => new ValueNode.ObjectRef(unresolvable.GlobalObjectId),
            _ => null,
        };

        // THE component-LogicalId format: "{ownerLogicalId}/{typeFullName}#{ordinal}".
        public static string ComposeLogicalId(string ownerLogicalId, string typeFullName, int ordinal) =>
            $"{ownerLogicalId}/{typeFullName}#{ordinal}";

        // Inverts ComposeLogicalId: the owner splits at the LAST '/' (an owner LogicalId may itself
        // contain '/'), the type/ordinal split at the LAST '#' -- agreeing with PlanExecutor.OwnerOf's
        // rule. A string with no '/' or no '#' after it, or a non-numeric ordinal, returns false.
        public static bool TryParseLogicalId(
            string logicalId, out string ownerLogicalId, out string typeFullName, out int ordinal)
        {
            ownerLogicalId = null!;
            typeFullName = null!;
            ordinal = 0;

            var hashIndex = logicalId.LastIndexOf('#');
            if (hashIndex < 0 || hashIndex == logicalId.Length - 1)
            {
                return false;
            }

            var beforeHash = logicalId.Substring(0, hashIndex);
            var ordinalText = logicalId.Substring(hashIndex + 1);
            if (!int.TryParse(ordinalText, out var parsedOrdinal))
            {
                return false;
            }

            var slashIndex = beforeHash.LastIndexOf('/');
            if (slashIndex < 0)
            {
                return false;
            }

            ownerLogicalId = beforeHash.Substring(0, slashIndex);
            typeFullName = beforeHash.Substring(slashIndex + 1);
            ordinal = parsedOrdinal;
            return true;
        }
    }
}
