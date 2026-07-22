using System.Linq;
using SceneBuilder.Core.Facades;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class FacadeCatalogBuilderTests
    {
        private static PrefabHierarchy MakePrefab(string guid, string assetPath, string rootRealName, long rootLocalId, params PrefabHierarchyNode[] children) =>
            new PrefabHierarchy
            {
                Guid = guid,
                AssetPath = assetPath,
                Root = new PrefabHierarchyNode
                {
                    RealName = rootRealName,
                    LocalId = rootLocalId,
                    Children = children,
                },
            };

        private static PrefabHierarchyNode Node(string realName, long localId, string? nestedGuid = null, params PrefabHierarchyNode[] children) =>
            new PrefabHierarchyNode
            {
                RealName = realName,
                LocalId = localId,
                NestedPrefabGuid = nestedGuid,
                Children = children,
            };

        [Fact]
        public void Generate_SameHierarchy_ByteIdentical()
        {
            var tank = MakePrefab(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Assets/Prefabs/Tank.prefab", "Tank", 1,
                Node("Chassis", 10, null,
                    Node("Wheel", 12),
                    Node("Wheel", 11),
                    Node("Front Wheel", 13)));

            var coin = MakePrefab("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Assets/Prefabs/Coin.prefab", "Coin", 2);

            var a = FacadeCatalogBuilder.Build(new[] { tank, coin });
            var b = FacadeCatalogBuilder.Build(new[] { coin, tank }); // shuffled input order

            Assert.Equal(a.Serialize(), b.Serialize());

            var chassisChildren = a.Entries.Single(e => e.PropertyName == "Tank")
                .Root.Children.Single(c => c.RealName == "Chassis").Node.Children;
            Assert.Equal(11, chassisChildren[0].LocalId);
            Assert.Equal(12, chassisChildren[1].LocalId);
            Assert.Equal(13, chassisChildren[2].LocalId);
        }

        [Fact]
        public void DuplicateSibling_DisambiguatesDeterministically_AndResolvesDistinct()
        {
            var prefabA = MakePrefab("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Assets/Prefabs/Cart.prefab", "Cart", 1,
                Node("Wheel", 21),
                Node("Wheel", 20));

            var prefabB = MakePrefab("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Assets/Prefabs/Cart.prefab", "Cart", 1,
                Node("Wheel", 20),
                Node("Wheel", 21)); // same children, swapped input order

            var catA = FacadeCatalogBuilder.Build(new[] { prefabA });
            var catB = FacadeCatalogBuilder.Build(new[] { prefabB });

            var wheelsA = catA.Entries.Single().Root.Children;

            Assert.Equal("Wheel", wheelsA[0].PropertyName);
            Assert.Equal(20, wheelsA[0].LocalId);
            Assert.Equal("Wheel2", wheelsA[1].PropertyName);
            Assert.Equal(21, wheelsA[1].LocalId);

            // Disambiguation is keyed on LocalId, not sibling/input order.
            Assert.Equal(catA.Serialize(), catB.Serialize());
        }

        [Fact]
        public void CrossPrefabNameCollision_DisambiguatedByGuidOrdinal()
        {
            var coinLow = MakePrefab("11111111111111111111111111111111", "Assets/Prefabs/A/Coin.prefab", "Coin", 1);
            var coinHigh = MakePrefab("22222222222222222222222222222222", "Assets/Prefabs/B/Coin.prefab", "Coin", 1);

            var catalog = FacadeCatalogBuilder.Build(new[] { coinHigh, coinLow });

            var byGuid = catalog.Entries.ToDictionary(e => e.Guid);
            Assert.Equal("Coin", byGuid["11111111111111111111111111111111"].PropertyName);
            Assert.Equal("Coin2", byGuid["22222222222222222222222222222222"].PropertyName);
        }

        [Fact]
        public void SanitizedSegment_ReDerivesRealName()
        {
            var prefab = MakePrefab("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Assets/Prefabs/Widget.prefab", "Widget", 1,
                Node("Front Wheel", 10),
                Node("2Fast", 11),
                Node("event", 12));

            var catalog = FacadeCatalogBuilder.Build(new[] { prefab });
            var children = catalog.Entries.Single().Root.Children;

            var frontWheel = children.Single(c => c.RealName == "Front Wheel");
            Assert.Equal("FrontWheel", frontWheel.PropertyName);

            var twoFast = children.Single(c => c.RealName == "2Fast");
            Assert.Equal("_2Fast", twoFast.PropertyName);

            var evt = children.Single(c => c.RealName == "event");
            Assert.Equal("@event", evt.PropertyName);
        }

        [Fact]
        public void Rename_RegeneratesFacade_KeepsLocalId()
        {
            var renamed = MakePrefab("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Assets/Prefabs/Tank.prefab", "Tank", 1,
                Node("Cannon", 5)); // was "Turret" at the same LocalId

            var catalog = FacadeCatalogBuilder.Build(new[] { renamed });
            var child = catalog.Entries.Single().Root.Children.Single();

            Assert.Equal("Cannon", child.PropertyName);
            Assert.Equal("CannonRef", child.Node.TypeName);
            Assert.Equal(5, child.LocalId);
            Assert.Equal("Cannon", child.RealName);
        }

        [Fact]
        public void NestedPrefab_FacadeComposes()
        {
            const string turretGuid = "cccccccccccccccccccccccccccccccc";
            var turret = MakePrefab(turretGuid, "Assets/Prefabs/Turret.prefab", "Turret", 1,
                Node("Barrel", 2));

            var tank = MakePrefab("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Assets/Prefabs/Tank.prefab", "Tank", 1,
                Node("Turret", 5, turretGuid));

            var catalog = FacadeCatalogBuilder.Build(new[] { turret, tank });

            var turretEntry = catalog.Entries.Single(e => e.Guid == turretGuid);
            var tankTurretChild = catalog.Entries.Single(e => e.Guid != turretGuid).Root.Children.Single();

            // Composed by reference to the nested prefab's own FacadeEntry.Root, never inlined.
            Assert.Same(turretEntry.Root, tankTurretChild.Node);
        }

        [Fact]
        public void Generator_SanitizesToValidUniqueIdentifiers()
        {
            var prefab = MakePrefab("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Assets/Prefabs/Widget.prefab", "Widget", 1,
                Node("a b", 10),
                Node("a-b", 11));

            var catalog = FacadeCatalogBuilder.Build(new[] { prefab });
            var children = catalog.Entries.Single().Root.Children;

            Assert.Equal(10, children[0].LocalId);
            Assert.Equal("a_b", children[0].PropertyName);
            Assert.Equal(11, children[1].LocalId);
            Assert.Equal("a_b2", children[1].PropertyName);
        }
    }
}
