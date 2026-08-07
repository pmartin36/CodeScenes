using System.Linq;
using System.Reflection;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // The milestone's one invariant: a listener target resolves to a component's OWN LogicalId,
    // never its owning GameObject's. Classify is total over StampedReference; ToValueNode is the
    // one lowering into a listener's Target/object-mode ArgValue slot; ComposeLogicalId/
    // TryParseLogicalId are the one owner of the component-LogicalId format; ComponentTargetIndex
    // is the generation both mapped-node and mapped-component moves invalidate.
    public class ComponentTargetResolutionTests
    {
        private static IdentityMapEntry GameObjectEntry(string logicalId, string goid) =>
            new() { LogicalId = logicalId, GlobalObjectId = goid, Kind = "GameObject" };

        private static IdentityMapEntry PrefabInstanceEntry(string logicalId, string goid) =>
            new() { LogicalId = logicalId, GlobalObjectId = goid, Kind = "PrefabInstance" };

        private static IdentityMapEntry ComponentEntry(string logicalId, string goid, string type = "DoorOpener") =>
            new() { LogicalId = logicalId, GlobalObjectId = goid, Kind = "Component", ComponentType = type };

        private static IdentityMap MapOf(params IdentityMapEntry[] entries) => new() { Entries = entries };

        // ---- Classify: one outcome per StampedReference shape --------------------------------

        [Fact]
        public void Classify_ComponentGoid_ReturnsSceneObjectWithComponentLogicalId()
        {
            var map = MapOf(GameObjectEntry("Rig", "goid-rig"), ComponentEntry("Rig/DoorOpener#0", "goid-comp"));
            var index = ComponentTargetIndex.ForMap(map);

            var result = ComponentTargetResolution.Classify(StampedReference.Scene("goid-comp"), index);

            var sceneObject = Assert.IsType<ListenerTarget.SceneObject>(result);
            Assert.Equal("Rig/DoorOpener#0", sceneObject.LogicalId);
        }

        [Fact]
        public void Classify_GameObjectGoid_ReturnsSceneObjectWithNodeLogicalId()
        {
            var map = MapOf(GameObjectEntry("Rig", "goid-rig"));
            var index = ComponentTargetIndex.ForMap(map);

            var result = ComponentTargetResolution.Classify(StampedReference.Scene("goid-rig"), index);

            var sceneObject = Assert.IsType<ListenerTarget.SceneObject>(result);
            Assert.Equal("Rig", sceneObject.LogicalId);
        }

        [Fact]
        public void Classify_PrefabInstanceRootGoid_ReturnsSceneObjectWithInstanceLogicalId()
        {
            var map = MapOf(PrefabInstanceEntry("Enemy0", "goid-instance"));
            var index = ComponentTargetIndex.ForMap(map);

            var result = ComponentTargetResolution.Classify(StampedReference.Scene("goid-instance"), index);

            var sceneObject = Assert.IsType<ListenerTarget.SceneObject>(result);
            Assert.Equal("Enemy0", sceneObject.LogicalId);
        }

        [Fact]
        public void Classify_AssetStamp_ReturnsAsset()
        {
            var index = ComponentTargetIndex.ForMap(MapOf());
            var assetRef = new AssetRef { Guid = "abc123", FileId = 2 };

            var result = ComponentTargetResolution.Classify(StampedReference.Asset(assetRef), index);

            var asset = Assert.IsType<ListenerTarget.Asset>(result);
            Assert.Equal(assetRef, asset.Ref);
        }

        [Fact]
        public void Classify_DanglingStamp_ReturnsDanglingNull()
        {
            var index = ComponentTargetIndex.ForMap(MapOf());

            var result = ComponentTargetResolution.Classify(StampedReference.Dangling, index);

            Assert.IsType<ListenerTarget.DanglingNull>(result);
        }

        [Fact]
        public void Classify_UnknownGoid_ReturnsUnresolvableCarryingThatGoid()
        {
            var index = ComponentTargetIndex.ForMap(MapOf());

            var result = ComponentTargetResolution.Classify(StampedReference.Scene("goid-unknown"), index);

            var unresolvable = Assert.IsType<ListenerTarget.Unresolvable>(result);
            Assert.Equal("goid-unknown", unresolvable.GlobalObjectId);
        }

        [Fact]
        public void Classify_ComponentEntryWithEmptyGlobalObjectId_IsUnresolvable()
        {
            var map = MapOf(new IdentityMapEntry { LogicalId = "Rig/DoorOpener#0", GlobalObjectId = "", Kind = "Component" });
            var index = ComponentTargetIndex.ForMap(map);

            var result = ComponentTargetResolution.Classify(StampedReference.Scene("goid-comp"), index);

            Assert.IsType<ListenerTarget.Unresolvable>(result);
        }

        // The milestone's invariant: a Component's own goid never falls back to its owning
        // GameObject's LogicalId, even when both are mapped.
        [Fact]
        public void Classify_OwnerAndComponentBothMapped_StampingTheComponentNeverReturnsTheOwner()
        {
            var map = MapOf(GameObjectEntry("Rig", "goid-rig"), ComponentEntry("Rig/DoorOpener#0", "goid-comp"));
            var index = ComponentTargetIndex.ForMap(map);

            var result = ComponentTargetResolution.Classify(StampedReference.Scene("goid-comp"), index);

            var sceneObject = Assert.IsType<ListenerTarget.SceneObject>(result);
            Assert.Equal("Rig/DoorOpener#0", sceneObject.LogicalId);
            Assert.NotEqual("Rig", sceneObject.LogicalId);
        }

        // Classify takes no hint about which slot (Target vs object-mode ArgValue) it is
        // answering for -- the same call classifies both identically.
        [Fact]
        public void Classify_AnswersAnObjectModeArgumentStampTheSameWayAsATarget()
        {
            var map = MapOf(GameObjectEntry("Rig", "goid-rig"), ComponentEntry("Rig/DoorOpener#0", "goid-comp"));
            var index = ComponentTargetIndex.ForMap(map);

            var targetResult = ComponentTargetResolution.Classify(StampedReference.Scene("goid-comp"), index);
            var argResult = ComponentTargetResolution.Classify(StampedReference.Scene("goid-comp"), index);

            Assert.Equal(targetResult, argResult);
        }

        // ---- (a3): no new IdentityMap entry kind is introduced --------------------------------

        [Fact]
        public void IdentityNodeIndex_KindConstantSet_IsExactlyGameObjectPrefabInstanceComponent()
        {
            var kindConstants = typeof(IdentityNodeIndex)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
                .OrderBy(v => v)
                .ToArray();

            Assert.Equal(new[] { "Component", "GameObject", "PrefabInstance" }, kindConstants);
        }

        [Fact]
        public void IdentityNodeIndex_IsNode_ComponentKindIsFalse()
        {
            Assert.False(IdentityNodeIndex.IsNode(IdentityNodeIndex.Component));
            Assert.False(IdentityNodeIndex.IsNode(
                new IdentityMapEntry { LogicalId = "Rig/DoorOpener#0", GlobalObjectId = "goid-comp", Kind = "Component" }));
        }

        // ---- ToValueNode: the one lowering into a listener's Target/ArgValue slot -------------

        [Fact]
        public void ToValueNode_SceneObject_ReturnsObjectRefOfThatLogicalId()
        {
            var node = ComponentTargetResolution.ToValueNode(new ListenerTarget.SceneObject("Rig/DoorOpener#0"));

            var objectRef = Assert.IsType<ValueNode.ObjectRef>(node);
            Assert.Equal("Rig/DoorOpener#0", objectRef.TargetLogicalId);
        }

        [Fact]
        public void ToValueNode_Asset_ReturnsAssetRefOfThatRef()
        {
            var assetRef = new AssetRef { Guid = "abc123", FileId = 2 };

            var node = ComponentTargetResolution.ToValueNode(new ListenerTarget.Asset(assetRef));

            var wrapped = Assert.IsType<ValueNode.AssetRef>(node);
            Assert.Equal(assetRef, wrapped.Ref);
        }

        [Fact]
        public void ToValueNode_DanglingNull_ReturnsNull()
        {
            var node = ComponentTargetResolution.ToValueNode(new ListenerTarget.DanglingNull());

            Assert.Null(node);
        }

        [Fact]
        public void ToValueNode_Unresolvable_ReturnsObjectRefCarryingTheRawGlobalObjectId()
        {
            var node = ComponentTargetResolution.ToValueNode(new ListenerTarget.Unresolvable("goid-unknown"));

            var objectRef = Assert.IsType<ValueNode.ObjectRef>(node);
            Assert.Equal("goid-unknown", objectRef.TargetLogicalId);
        }

        // Every ToValueNode result must be a legal listener Target AND a legal object-mode
        // ArgValue -- both slots reuse UnityEventListener's own validation.
        [Theory]
        [MemberData(nameof(EveryListenerTargetResult))]
        public void ToValueNode_EveryResult_IsALegalListenerTargetAndObjectModeArgValue(ListenerTarget target)
        {
            var node = ComponentTargetResolution.ToValueNode(target);

            var asTarget = new UnityEventListener(node, "Open", ListenerArgMode.Void);
            Assert.Equal(node, asTarget.Target);

            if (node is not null)
            {
                var asArg = new UnityEventListener(null, "Open", ListenerArgMode.Object, node);
                Assert.Equal(node, asArg.ArgValue);
            }
        }

        public static System.Collections.Generic.IEnumerable<object[]> EveryListenerTargetResult()
        {
            yield return new object[] { new ListenerTarget.SceneObject("Rig/DoorOpener#0") };
            yield return new object[] { new ListenerTarget.Asset(new AssetRef { Guid = "abc123", FileId = 2 }) };
            yield return new object[] { new ListenerTarget.DanglingNull() };
            yield return new object[] { new ListenerTarget.Unresolvable("goid-unknown") };
        }

        // ---- Compose/parse: the one owner of the component-LogicalId format ------------------

        [Theory]
        [InlineData("Rig", "DoorOpener", 0)]
        [InlineData("Level/Rig", "DoorOpener", 0)]
        [InlineData("Rig", "UnityEngine.UI.Button", 3)]
        [InlineData("Rig", "Outer+Inner", 12)]
        public void ComposeThenTryParse_RoundTrips(string owner, string type, int ordinal)
        {
            var composed = ComponentTargetResolution.ComposeLogicalId(owner, type, ordinal);

            var ok = ComponentTargetResolution.TryParseLogicalId(
                composed, out var parsedOwner, out var parsedType, out var parsedOrdinal);

            Assert.True(ok, $"TryParseLogicalId failed to parse '{composed}'");
            Assert.Equal(owner, parsedOwner);
            Assert.Equal(type, parsedType);
            Assert.Equal(ordinal, parsedOrdinal);
        }

        [Fact]
        public void ComposeLogicalId_MatchesTheDollarInterpolatedFormat()
        {
            var composed = ComponentTargetResolution.ComposeLogicalId("Rig", "DoorOpener", 0);

            Assert.Equal("Rig/DoorOpener#0", composed);
        }

        [Theory]
        [InlineData("NoSeparatorAtAll")]
        [InlineData("Rig/NoHash")]
        [InlineData("NoSlash#0")]
        public void TryParseLogicalId_NonComponentString_ReturnsFalse(string logicalId)
        {
            var ok = ComponentTargetResolution.TryParseLogicalId(
                logicalId, out var owner, out var type, out var ordinal);

            Assert.False(ok, $"'{logicalId}' should not parse as a component LogicalId");
            Assert.Null(owner);
            Assert.Null(type);
            Assert.Equal(0, ordinal);
        }

        // The owner is everything before the LAST '/', matching PlanExecutor.OwnerOf's rule --
        // required because an owner LogicalId may itself contain '/' (a nested node path).
        [Fact]
        public void TryParseLogicalId_OwnerContainingSlash_SplitsAtTheLastSlash()
        {
            var ok = ComponentTargetResolution.TryParseLogicalId(
                "Level/Rig/DoorOpener#0", out var owner, out var type, out var ordinal);

            Assert.True(ok);
            Assert.Equal("Level/Rig", owner);
            Assert.Equal("DoorOpener", type);
            Assert.Equal(0, ordinal);
        }

        // ---- Generation: invalidates on EITHER a mapped-node or a mapped-component move ------

        [Fact]
        public void Generation_TwoContentEqualMaps_EqualRegardlessOfEntryOrder()
        {
            var a = MapOf(GameObjectEntry("Rig", "goid-rig"), ComponentEntry("Rig/DoorOpener#0", "goid-comp"));
            var b = MapOf(ComponentEntry("Rig/DoorOpener#0", "goid-comp"), GameObjectEntry("Rig", "goid-rig"));

            Assert.Equal(ComponentTargetIndex.ForMap(a).Generation, ComponentTargetIndex.ForMap(b).Generation);
        }

        [Fact]
        public void Generation_ComponentEntryAdded_ProducesDifferentGeneration()
        {
            var a = MapOf(GameObjectEntry("Rig", "goid-rig"));
            var b = MapOf(GameObjectEntry("Rig", "goid-rig"), ComponentEntry("Rig/DoorOpener#0", "goid-comp"));

            Assert.NotEqual(ComponentTargetIndex.ForMap(a).Generation, ComponentTargetIndex.ForMap(b).Generation);
        }

        [Fact]
        public void Generation_ComponentEntryRemoved_ProducesDifferentGeneration()
        {
            var a = MapOf(GameObjectEntry("Rig", "goid-rig"), ComponentEntry("Rig/DoorOpener#0", "goid-comp"));
            var b = MapOf(GameObjectEntry("Rig", "goid-rig"));

            Assert.NotEqual(ComponentTargetIndex.ForMap(a).Generation, ComponentTargetIndex.ForMap(b).Generation);
        }

        [Fact]
        public void Generation_ComponentEntryRetargeted_ProducesDifferentGeneration()
        {
            var a = MapOf(ComponentEntry("Rig/DoorOpener#0", "goid-comp"));
            var b = MapOf(ComponentEntry("Rig/DoorOpener#0", "goid-comp-retargeted"));

            Assert.NotEqual(ComponentTargetIndex.ForMap(a).Generation, ComponentTargetIndex.ForMap(b).Generation);
        }

        [Fact]
        public void Generation_NodeEntryChanged_ProducesDifferentGeneration()
        {
            var a = MapOf(GameObjectEntry("Rig", "goid-rig"));
            var b = MapOf(GameObjectEntry("Rig", "goid-rig-retargeted"));

            Assert.NotEqual(ComponentTargetIndex.ForMap(a).Generation, ComponentTargetIndex.ForMap(b).Generation);
        }

        [Fact]
        public void Generation_MapsDifferingOnlyInAssets_AreEqual()
        {
            var a = new IdentityMap
            {
                Entries = new[] { GameObjectEntry("Rig", "goid-rig") },
                Assets = System.Array.Empty<AssetEntry>(),
            };
            var b = new IdentityMap
            {
                Entries = new[] { GameObjectEntry("Rig", "goid-rig") },
                Assets = new[] { new AssetEntry { Guid = "abc123", LastKnownPath = "Assets/Mat.mat" } },
            };

            Assert.Equal(ComponentTargetIndex.ForMap(a).Generation, ComponentTargetIndex.ForMap(b).Generation);
        }
    }
}
