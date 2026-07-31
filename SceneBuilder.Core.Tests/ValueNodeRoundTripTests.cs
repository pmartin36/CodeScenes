using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // ValueNode.Enum.Canonical is the single owning constructor of a canonical Enum node: given
    // any member ordering, duplicate set or flags flag, it produces the ONE shape that
    // SourceExpr.ValueNodeLiteral renders and ValueNodeParser.Parse reads back to an EQUAL node.
    public class ValueNodeRoundTripTests
    {
        private static ValueNode RoundTrip(ValueNode node)
        {
            var text = SourceExpr.ValueNodeLiteral(node);
            return ValueNodeParser.Parse(SyntaxFactory.ParseExpression(text));
        }

        [Fact]
        public void Canonical_SingleMember_IsNotFlags_AndRoundTrips()
        {
            var node = ValueNode.Enum.Canonical("Game.Layers", new[] { "Ground" });

            Assert.False(node.IsFlags);
            Assert.Equal(new[] { "Ground" }, node.Members);
            Assert.Equal(node, RoundTrip(node));
        }

        [Fact]
        public void Canonical_MembersOutOfOrder_SortsOrdinally()
        {
            var node = ValueNode.Enum.Canonical("Game.Layers", new[] { "Water", "Ground" });

            Assert.Equal(new[] { "Ground", "Water" }, node.Members);
            Assert.True(node.IsFlags);
            Assert.Equal(node, RoundTrip(node));
        }

        [Fact]
        public void Canonical_ClrNestedTypeName_BecomesDotSeparated()
        {
            var node = ValueNode.Enum.Canonical(
                "UnityEngine.UI.ContentSizeFitter+FitMode", new[] { "PreferredSize" });

            Assert.Equal("UnityEngine.UI.ContentSizeFitter.FitMode", node.TypeFullName);
        }

        [Fact]
        public void Canonical_DuplicateMembers_CollapseToDistinctSet()
        {
            var node = ValueNode.Enum.Canonical("Game.Layers", new[] { "Ground", "Water", "Ground" });

            Assert.Equal(new[] { "Ground", "Water" }, node.Members);
        }

        // A bitmask that happens to decompose to ONE active member must not be tagged IsFlags:true.
        // ValueNodeParser's member-access arm always produces IsFlags:false for a single member, so
        // an IsFlags:true/1-member node never equals what it round-trips to.
        [Fact]
        public void Canonical_OneMemberFromAFlagsValue_IsFlagsIsFalse()
        {
            var node = ValueNode.Enum.Canonical("UnityEngine.RigidbodyConstraints", new[] { "FreezePositionY" });

            Assert.False(node.IsFlags,
                "A one-member decomposition of a flags value must not be tagged IsFlags:true — it " +
                "would never equal the node ValueNodeParser produces from the emitted single member access.");
            Assert.Equal(node, RoundTrip(node));
        }

        [Fact]
        public void Canonical_EmptyMemberSet_Throws()
        {
            Assert.ThrowsAny<Exception>(() => ValueNode.Enum.Canonical("Game.Layers", Enumerable.Empty<string>()));
        }
    }
}
