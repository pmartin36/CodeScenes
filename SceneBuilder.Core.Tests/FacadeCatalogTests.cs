using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class FacadeCatalogTests
    {
        // Multi-entry catalog: disambiguated PropertyNames (Coin/Coin2), a composed nested
        // FacadeChild.Node (inlined, not $ref), a duplicate-sibling Wheel/Wheel2 with distinct
        // LocalIds, and a RealName needing sanitize ("Front Wheel").
        private static FacadeCatalog SampleCatalog() => new FacadeCatalog
        {
            SchemaVersion = 1,
            Entries = new[]
            {
                new FacadeEntry
                {
                    PropertyName = "Coin",
                    Guid = "11111111111111111111111111111111",
                    Root = new FacadeNode
                    {
                        TypeName = "CoinRef",
                        Children = new[]
                        {
                            new FacadeChild
                            {
                                PropertyName = "Sparkle",
                                RealName = "Sparkle",
                                LocalId = 100,
                                Node = new FacadeNode { TypeName = "SparkleRef" },
                            },
                        },
                    },
                },
                new FacadeEntry
                {
                    PropertyName = "Coin2",
                    Guid = "22222222222222222222222222222222",
                    Root = new FacadeNode
                    {
                        TypeName = "Coin2Ref",
                        Children = new[]
                        {
                            new FacadeChild
                            {
                                PropertyName = "Wheel",
                                RealName = "Wheel",
                                LocalId = 200,
                                Node = new FacadeNode { TypeName = "WheelRef" },
                            },
                            new FacadeChild
                            {
                                PropertyName = "Wheel2",
                                RealName = "Wheel",
                                LocalId = 201,
                                Node = new FacadeNode { TypeName = "WheelRef" },
                            },
                            new FacadeChild
                            {
                                PropertyName = "FrontWheel",
                                RealName = "Front Wheel",
                                LocalId = 202,
                                Node = new FacadeNode { TypeName = "FrontWheelRef" },
                            },
                        },
                    },
                },
            },
        };

        [Fact]
        public void Manifest_RoundTrips()
        {
            var catalog = SampleCatalog();

            var jsonA = catalog.Serialize();
            var back = FacadeCatalog.Deserialize(jsonA);
            var jsonB = back.Serialize();

            Assert.Equal(jsonA, jsonB);

            Assert.Equal(catalog.Entries.Length, back.Entries.Length);
            Assert.Equal("Coin", back.Entries[0].PropertyName);
            Assert.Equal("Coin2", back.Entries[1].PropertyName);

            var wheels = back.Entries[1].Root.Children;
            Assert.Equal("Wheel", wheels[0].PropertyName);
            Assert.Equal("Wheel", wheels[0].RealName);
            Assert.Equal(200, wheels[0].LocalId);
            Assert.Equal("Wheel2", wheels[1].PropertyName);
            Assert.Equal(201, wheels[1].LocalId);
            Assert.Equal("FrontWheel", wheels[2].PropertyName);
            Assert.Equal("Front Wheel", wheels[2].RealName);
        }

        [Fact]
        public void Catalog_ReverseLookup_GuidToPropertyName()
        {
            var catalog = SampleCatalog();

            var found = catalog.TryGetPropertyName("22222222222222222222222222222222", out var propertyName);
            Assert.True(found);
            Assert.Equal("Coin2", propertyName);

            var missing = catalog.TryGetPropertyName("99999999999999999999999999999999", out var missingName);
            Assert.False(missing);
        }
    }
}
