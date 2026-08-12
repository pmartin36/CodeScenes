using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // ValueNode.ManagedReference carries a [SerializeReference] polymorphic field's live value: a
    // concrete type (or null, for a null reference) plus its own field values. It round-trips
    // through canonical JSON to an equal value, a null reference is a canonical form distinct from
    // a non-null empty-fields instance, and every ValueWalk primitive reaches its Fields the same
    // way every other container kind already does (mirrors UnityEventListenerModelTests).
    public class ManagedReferenceModelTests
    {
        private static ValueNode.ManagedReference Aggressive(float range) =>
            new(new TypeRef("Game.Aggressive", "Game.Runtime"),
                new FieldMap(new[] { new KeyValuePair<string, ValueNode>("range", ValueNode.Primitive.Float(range)) }));

        private static readonly ValueNode.ManagedReference NullRef = new(null, FieldMap.Empty);

        private static ValueNode RoundTrip(ValueNode node) =>
            CanonicalJson.Deserialize<ValueNode>(CanonicalJson.Serialize(node));

        // ---- Canonical JSON + round trip -------------------------------------------------------

        [Fact]
        public void RoundTrip_ConcreteInstance_ProducesAnEqualValue()
        {
            var node = Aggressive(5f);

            Assert.Equal(node, RoundTrip(node));
        }

        [Fact]
        public void RoundTrip_NullReference_ProducesAnEqualValue()
        {
            Assert.Equal(NullRef, RoundTrip(NullRef));
        }

        [Fact]
        public void Serialize_ConcreteInstance_RepeatedSerializationIsByteIdentical()
        {
            var node = Aggressive(5f);

            var json1 = CanonicalJson.Serialize<ValueNode>(node);
            var json2 = CanonicalJson.Serialize<ValueNode>(node);

            Assert.Equal(json1, json2);
        }

        [Fact]
        public void Serialize_Discriminator_IsExactlyManagedReference()
        {
            var json = CanonicalJson.Serialize<ValueNode>(NullRef);

            Assert.Contains("\"kind\": \"ManagedReference\"", json);
        }

        [Fact]
        public void Serialize_NullReference_ProducesADistinctFormFromANonNullEmptyFieldsInstance()
        {
            var nonNullEmpty = new ValueNode.ManagedReference(new TypeRef("Game.Flee", "Game.Runtime"), FieldMap.Empty);

            var nullJson = CanonicalJson.Serialize<ValueNode>(NullRef);
            var nonNullJson = CanonicalJson.Serialize<ValueNode>(nonNullEmpty);

            Assert.NotEqual(nullJson, nonNullJson);
            Assert.NotEqual(NullRef, nonNullEmpty);
        }

        // ---- ValueWalk primitives reach ManagedReference.Fields --------------------------------

        [Fact]
        public void Fold_ManagedReferenceNode_InvokesTheManagedReferenceDelegate_NotTheLeafDelegate()
        {
            // NullRef has no fields, so leaf has no legitimate member to fold; a leaf invocation
            // can only mean the top-level node itself was misrouted to leaf.
            var node = NullRef;
            var managedReferenceDelegateCalled = false;

            ValueWalk.Fold<string>(
                node,
                (listNode, items) => "",
                (nestedNode, members) => "",
                leaf => throw new Xunit.Sdk.XunitException("leaf delegate ran for a ManagedReference node"),
                (listenersNode, slots) => "",
                (managedReferenceNode, members) => { managedReferenceDelegateCalled = true; return ""; });

            Assert.True(managedReferenceDelegateCalled,
                "ValueWalk.Fold did not invoke the managedReference delegate for a ManagedReference node");
        }

        [Fact]
        public void Fold_ManagedReferenceNode_FoldsFieldsBeforeInvokingTheManagedReferenceDelegate()
        {
            var node = Aggressive(5f);
            IReadOnlyList<KeyValuePair<string, string>>? foldedMembers = null;

            ValueWalk.Fold<string>(
                node,
                (listNode, items) => "",
                (nestedNode, members) => "",
                leaf => leaf is ValueNode.Primitive primitive
                    ? primitive.Value!.ToString()!
                    : throw new Xunit.Sdk.XunitException("leaf delegate ran for the ManagedReference node itself, not its 'range' member"),
                (listenersNode, slots) => "",
                (managedReferenceNode, members) => { foldedMembers = members; return ""; });

            Assert.NotNull(foldedMembers);
            Assert.Equal(new[] { "range" }, foldedMembers!.Select(m => m.Key));
            Assert.Equal(new[] { "5" }, foldedMembers!.Select(m => m.Value));
        }

        [Fact]
        public void Descend_ManagedReferenceNode_VisitsEachField_WithTheMemberKey()
        {
            var node = Aggressive(5f);
            var visitedKeys = new List<string>();

            ValueWalk.Descend<int>(
                node,
                0,
                enter: (n, ctx) => ctx,
                item: (listNode, ctx, index) => ctx,
                member: (nestedNode, ctx, key) => ctx,
                listenerSlot: (listenersNode, ctx, index, slotName) => ctx,
                managedReferenceMember: (managedReferenceNode, ctx, key) => { visitedKeys.Add(key); return ctx; });

            Assert.Contains("range", visitedKeys);
        }

        [Fact]
        public void Map_ManagedReferenceNode_RebuildsAnEqualNode_WithFieldsVisitedAtDepth()
        {
            var node = Aggressive(5f);

            var mapped = (ValueNode.ManagedReference)ValueWalk.Map(
                node,
                0,
                (n, ctx) => n is ValueNode.Primitive p && p.Kind == PrimitiveKind.Float
                    ? ValueNode.Primitive.Float(99f)
                    : n,
                (n, ctx, key) => ctx,
                dropEmptiedNested: false)!;

            Assert.Equal(99f, ((ValueNode.Primitive)mapped.Fields["range"]).Value);
        }

        [Fact]
        public void Any_ManagedReferenceNode_FindsAPredicateMatchNestedInsideFields()
        {
            var node = Aggressive(5f);

            var found = ValueWalk.Any(node, n => n is ValueNode.Primitive p && p.Kind == PrimitiveKind.Float);

            Assert.True(found, "ValueWalk.Any did not descend into ManagedReference.Fields");
        }

        [Fact]
        public void Enumerate_ManagedReferenceNode_YieldsEachFieldAtItsDottedMemberPath()
        {
            var node = Aggressive(5f);

            var paths = ValueWalk.Enumerate(node).Select(e => e.Path).ToList();

            Assert.Contains(".range", paths);
        }

        [Fact]
        public void Enumerate_ManagedReferenceFieldHoldingAListOfManagedReferences_WalksEachElement()
        {
            var list = new ValueNode.List(new ValueNode[] { Aggressive(5f), Aggressive(9f) });
            var composite = new ValueNode.ManagedReference(
                new TypeRef("Game.Composite", "Game.Runtime"),
                new FieldMap(new[] { new KeyValuePair<string, ValueNode>("children", list) }));

            var paths = ValueWalk.Enumerate(composite).Select(e => e.Path).ToList();

            Assert.Contains(".children[0].range", paths);
            Assert.Contains(".children[1].range", paths);
        }

        // ---- ManagedReferenceProjection: ValueNode.ManagedReference <-> (fullTypename, fields) --

        [Fact]
        public void Projection_ToFullTypename_ConcreteType_IsAssemblyQualified()
        {
            var fullTypename = ManagedReferenceProjection.ToFullTypename(new TypeRef("Game.Aggressive", "Game.Runtime"));

            Assert.Equal("Game.Runtime Game.Aggressive", fullTypename);
        }

        [Fact]
        public void Projection_ToFullTypename_NullConcreteType_ReturnsNull()
        {
            Assert.Null(ManagedReferenceProjection.ToFullTypename(null));
        }

        [Fact]
        public void Projection_FromFullTypename_RoundTripsAssemblyAndType()
        {
            var typeRef = ManagedReferenceProjection.FromFullTypename("Game.Runtime Game.Aggressive");

            Assert.Equal(new TypeRef("Game.Aggressive", "Game.Runtime"), typeRef);
        }

        [Fact]
        public void Projection_FromFullTypename_NullOrEmpty_ReturnsNull()
        {
            Assert.Null(ManagedReferenceProjection.FromFullTypename(null));
            Assert.Null(ManagedReferenceProjection.FromFullTypename(""));
        }

        [Fact]
        public void Projection_ProjectThenFromProjection_RoundTripsToAnEqualModel()
        {
            var node = Aggressive(5f);

            var projected = ManagedReferenceProjection.Project(node);
            var rebuilt = ManagedReferenceProjection.FromProjection(projected.FullTypename, projected.Fields);

            Assert.Equal(node, rebuilt);
        }

        [Fact]
        public void Projection_NullReference_ProjectsToANullFullTypename()
        {
            var projected = ManagedReferenceProjection.Project(NullRef);

            Assert.Null(projected.FullTypename);
            Assert.Empty(projected.Fields);
        }
    }
}
