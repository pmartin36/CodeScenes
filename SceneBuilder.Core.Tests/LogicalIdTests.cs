using System.Linq;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Tests.Fixtures;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class LogicalIdTests
    {
        [Fact]
        public void LogicalId_FromHandleName_Priority1()
        {
            var result = BuilderParser.Parse(BuilderFixtures.HandleNamedRoot, existingMap: null);

            var node = Assert.Single(result.Model.Roots);
            Assert.Equal("Player", node.Name);
            Assert.Equal("player", node.LogicalId);

            var entry = Assert.Single(result.IdentityMap.Entries);
            Assert.Equal("player", entry.LogicalId);
            Assert.Equal("GameObject", entry.Kind);
            Assert.Null(entry.ParentLogicalId);
        }

        [Fact]
        public void LogicalId_FromExplicitIdCall_Priority2()
        {
            var result = BuilderParser.Parse(BuilderFixtures.ExplicitIdRoot, existingMap: null);

            var node = Assert.Single(result.Model.Roots);
            Assert.Equal("Enemy", node.Name);
            Assert.Equal("boss", node.LogicalId);

            var entry = Assert.Single(result.IdentityMap.Entries);
            Assert.Equal("boss", entry.LogicalId);
        }

        [Fact]
        public void LogicalId_Synthesized_IsPersistedAndStableAcrossSiblingInsertion_Priority3()
        {
            var resultA = BuilderParser.Parse(BuilderFixtures.SiblingInsertion_A, existingMap: null);

            var parentA = Assert.Single(resultA.Model.Roots);
            var wallA = Assert.Single(parentA.Children.Where(c => c.Name == "Wall"));
            Assert.Equal("parent/Wall/2", wallA.LogicalId);
            Assert.Contains(resultA.IdentityMap.Entries, e => e.LogicalId == "parent/Wall/2" && e.ParentLogicalId == "parent");

            var resultB = BuilderParser.Parse(BuilderFixtures.SiblingInsertion_B, resultA.IdentityMap);

            var parentB = Assert.Single(resultB.Model.Roots);
            var wallB = Assert.Single(parentB.Children.Where(c => c.Name == "Wall"));
            var newSiblingB = Assert.Single(parentB.Children.Where(c => c.Name == "NewSibling"));

            // Wall kept its persisted id even though it shifted from sibling index 2 to 3.
            Assert.Equal("parent/Wall/2", wallB.LogicalId);
            // The newly inserted sibling gets a fresh synthesized id, not a collision with Wall's.
            Assert.Equal("parent/NewSibling/2", newSiblingB.LogicalId);
        }
    }
}
