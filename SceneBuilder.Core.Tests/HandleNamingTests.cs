using Microsoft.CodeAnalysis.CSharp;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class HandleNamingTests
    {
        [Fact]
        public void Derive_NoCollision_ReturnsCamelCaseBase()
        {
            var result = HandleNaming.Derive("Weapon", System.Array.Empty<string>());

            Assert.Equal("weapon", result);
        }

        [Fact]
        public void Derive_SingleCollision_SuffixesWith2()
        {
            var result = HandleNaming.Derive("Weapon", new[] { "weapon" });

            Assert.Equal("weapon2", result);
        }

        [Fact]
        public void Derive_MultipleCollisions_TakesFirstFreeSuffix()
        {
            var result = HandleNaming.Derive("Weapon", new[] { "weapon", "weapon2", "weapon3" });

            Assert.Equal("weapon4", result);
        }

        [Fact]
        public void Derive_MultiWordName_SanitizesToSingleCamelCaseIdentifier()
        {
            var result = HandleNaming.Derive("Main Camera", System.Array.Empty<string>());

            Assert.Equal("mainCamera", result);
        }

        [Fact]
        public void Derive_LeadingDigit_ProducesValidIdentifier()
        {
            var result = HandleNaming.Derive("3Legged", System.Array.Empty<string>());

            Assert.False(char.IsDigit(result[0]));
            Assert.True(SyntaxFacts.IsValidIdentifier(result));
        }

        [Fact]
        public void Derive_ReservedKeyword_ProducesNonKeywordValidIdentifier()
        {
            var result = HandleNaming.Derive("Class", System.Array.Empty<string>());

            Assert.Equal(SyntaxKind.None, SyntaxFacts.GetKeywordKind(result));
            Assert.True(SyntaxFacts.IsValidIdentifier(result));
        }

        [Fact]
        public void Derive_DoesNotMutateExistingIdentifiers()
        {
            var existing = new[] { "weapon" };

            HandleNaming.Derive("Weapon", existing);

            Assert.Equal(new[] { "weapon" }, existing);
        }
    }
}
