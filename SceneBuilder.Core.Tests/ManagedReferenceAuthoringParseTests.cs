using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // The `.SetRef(x => x.field, new T{...} | null)` authoring form: the CALL (not the syntax)
    // decides ManagedReference vs Nested, since both render `new T{...}`. Parse -> emit -> reparse
    // must round-trip at arbitrary depth, null is a canonical form distinct from a non-null
    // empty-fields instance, and an array of `new T{...}` parses to a List of ManagedReference
    // element-for-element.
    public class ManagedReferenceAuthoringParseTests
    {
        private static ComponentData ParseSingleComponent(string source)
        {
            var result = BuilderParser.Parse(source);
            var node = Assert.Single(result.Model.Roots);
            return Assert.Single(node.Components);
        }

        private static string Source(string setRefArg) => $@"
public class SetRefScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Add(""Enemy"").Component<AiBrain>(c => c.SetRef(x => x.strategy, {setRefArg}));
    }}
}}
";

        [Fact]
        public void Parse_ConcreteInstance_YieldsManagedReferenceUnderMemberKey()
        {
            var component = ParseSingleComponent(Source("new Aggressive { range = 5f }"));

            var value = component.Fields["member:strategy"];
            var managedRef = Assert.IsType<ValueNode.ManagedReference>(value);

            Assert.Equal("Aggressive", managedRef.ConcreteType!.FullName);
            Assert.Equal(ValueNode.Primitive.Float(5f), managedRef.Fields["range"]);
        }

        [Fact]
        public void EmitThenReparse_ConcreteInstance_RoundTripsToAnEqualModel()
        {
            var component = ParseSingleComponent(Source("new Aggressive { range = 5f }"));
            var original = component.Fields["member:strategy"];

            var literal = SourceExpr.ValueNodeLiteral(original);
            var reparsed = ParseSingleComponent(Source(literal)).Fields["member:strategy"];

            Assert.Equal(original, reparsed);
        }

        [Fact]
        public void Parse_Null_YieldsNullConcreteTypeDistinctFromEmptyInstance()
        {
            var component = ParseSingleComponent(Source("null"));

            var value = component.Fields["member:strategy"];
            var managedRef = Assert.IsType<ValueNode.ManagedReference>(value);

            Assert.Null(managedRef.ConcreteType);
            Assert.NotEqual(managedRef, new ValueNode.ManagedReference(new TypeRef("Aggressive"), FieldMap.Empty));
        }

        [Fact]
        public void Emit_Null_RendersNullLiteral()
        {
            var component = ParseSingleComponent(Source("null"));
            var value = component.Fields["member:strategy"];

            Assert.Equal("null", SourceExpr.ValueNodeLiteral(value));
        }

        [Fact]
        public void Parse_NestedComposite_RecursesManagedReferenceAtDepth()
        {
            var component = ParseSingleComponent(Source(
                "new Composite { primary = new Aggressive { range = 5f }, fallback = new Flee { speed = 3f } }"));

            var composite = Assert.IsType<ValueNode.ManagedReference>(component.Fields["member:strategy"]);
            var primary = Assert.IsType<ValueNode.ManagedReference>(composite.Fields["primary"]);
            var fallback = Assert.IsType<ValueNode.ManagedReference>(composite.Fields["fallback"]);

            Assert.Equal("Aggressive", primary.ConcreteType!.FullName);
            Assert.Equal(ValueNode.Primitive.Float(5f), primary.Fields["range"]);
            Assert.Equal("Flee", fallback.ConcreteType!.FullName);
            Assert.Equal(ValueNode.Primitive.Float(3f), fallback.Fields["speed"]);
        }

        [Fact]
        public void EmitThenReparse_NestedComposite_RoundTripsToAnEqualModel()
        {
            var component = ParseSingleComponent(Source(
                "new Composite { primary = new Aggressive { range = 5f }, fallback = new Flee { speed = 3f } }"));
            var original = component.Fields["member:strategy"];

            var literal = SourceExpr.ValueNodeLiteral(original);
            var reparsed = ParseSingleComponent(Source(literal)).Fields["member:strategy"];

            Assert.Equal(original, reparsed);
        }

        [Fact]
        public void Parse_ArrayOfInstances_YieldsListOfManagedReferenceElementForElement()
        {
            var component = ParseSingleComponent(Source(
                "new IStrategy[] { new Aggressive { range = 5f }, new Flee { speed = 3f } }"));

            var list = Assert.IsType<ValueNode.List>(component.Fields["member:strategy"]);
            Assert.Equal(2, list.Items.Count);

            var first = Assert.IsType<ValueNode.ManagedReference>(list.Items[0]);
            var second = Assert.IsType<ValueNode.ManagedReference>(list.Items[1]);
            Assert.Equal("Aggressive", first.ConcreteType!.FullName);
            Assert.Equal("Flee", second.ConcreteType!.FullName);
        }

        [Fact]
        public void EmitThenReparse_ArrayOfInstances_RoundTripsElementForElement()
        {
            var component = ParseSingleComponent(Source(
                "new IStrategy[] { new Aggressive { range = 5f }, new Flee { speed = 3f } }"));
            var original = component.Fields["member:strategy"];

            var literal = SourceExpr.ValueNodeLiteral(original);
            var reparsed = ParseSingleComponent(Source(literal)).Fields["member:strategy"];

            Assert.Equal(original, reparsed);
        }
    }
}
