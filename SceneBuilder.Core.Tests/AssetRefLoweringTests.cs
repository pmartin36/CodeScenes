using System.Collections.Generic;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b2-t2: AssetRefLowering.Lower(SceneModel, resolver) fills Guid/FileId/TypeHint on
    // unresolved ValueNode.AssetRef nodes (field, list-nested, nested-object), leaves
    // everything else untouched, and never throws on a missing resolver result.
    public class AssetRefLoweringTests
    {
        private const string AssetPathSource = @"
public class AssetLoweringScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Player"").Component<UnityEngine.MeshRenderer>(mr => mr.Set(""sharedMaterial"", Asset(""Assets/Materials/Red.mat"")));
    }
}
";

        [Fact]
        public void Lowering_AssetPath_ResolvesToAssetRefWithGuid()
        {
            var parsed = BuilderParser.Parse(AssetPathSource);
            var resolver = StubResolver(new Dictionary<string, (string, long, string)>
            {
                ["Assets/Materials/Red.mat"] = ("guid-red-mat", 0, "Material"),
            });

            var lowered = AssetRefLowering.Lower(parsed.Model, resolver);

            var component = Assert.Single(Assert.Single(lowered.Roots).Components);
            var field = Assert.IsType<ValueNode.AssetRef>(component.Fields["sharedMaterial"]);
            Assert.NotNull(field.Ref);
            Assert.Equal("guid-red-mat", field.Ref!.Guid);
            Assert.Equal(0, field.Ref.FileId);
            Assert.Equal("Material", field.Ref.TypeHint);
            Assert.Equal("Assets/Materials/Red.mat", field.Ref.DisplayPath);
        }

        [Fact]
        public void Lowering_AssetRefInsideList_ResolvesEachElement()
        {
            var model = ModelWithField(new ValueNode.List(new ValueNode[]
            {
                new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/A.mat" }),
                new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/B.mat" }),
            }));
            var resolver = StubResolver(new Dictionary<string, (string, long, string)>
            {
                ["Assets/A.mat"] = ("guid-a", 0, "Material"),
                ["Assets/B.mat"] = ("guid-b", 0, "Material"),
            });

            var lowered = AssetRefLowering.Lower(model, resolver);

            var list = Assert.IsType<ValueNode.List>(FieldOf(lowered));
            var first = Assert.IsType<ValueNode.AssetRef>(list.Items[0]);
            var second = Assert.IsType<ValueNode.AssetRef>(list.Items[1]);
            Assert.Equal("guid-a", first.Ref!.Guid);
            Assert.Equal("guid-b", second.Ref!.Guid);
        }

        [Fact]
        public void Lowering_AssetRefInsideNested_Resolves()
        {
            var nestedFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "inner",
                    new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/Inner.mat" })),
            });
            var model = ModelWithField(new ValueNode.Nested(nestedFields));
            var resolver = StubResolver(new Dictionary<string, (string, long, string)>
            {
                ["Assets/Inner.mat"] = ("guid-inner", 0, "Material"),
            });

            var lowered = AssetRefLowering.Lower(model, resolver);

            var nested = Assert.IsType<ValueNode.Nested>(FieldOf(lowered));
            var inner = Assert.IsType<ValueNode.AssetRef>(nested.Fields["inner"]);
            Assert.Equal("guid-inner", inner.Ref!.Guid);
        }

        [Fact]
        public void Lowering_ResolverReturnsNull_LeavesNodeUnresolvedNoThrow()
        {
            var model = ModelWithField(new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/Missing.mat" }));

            var lowered = AssetRefLowering.Lower(model, _ => null);

            var field = Assert.IsType<ValueNode.AssetRef>(FieldOf(lowered));
            Assert.Equal("", field.Ref!.Guid);
            Assert.Equal("Assets/Missing.mat", field.Ref.DisplayPath);
        }

        [Fact]
        public void Lowering_PreResolvedAssetRef_PassesThroughUnchanged()
        {
            var preResolved = new AssetRef { Guid = "already-resolved", FileId = 0, TypeHint = "Material", DisplayPath = "Assets/Old.mat" };
            var model = ModelWithField(new ValueNode.AssetRef(preResolved));

            var lowered = AssetRefLowering.Lower(model, _ => ("should-not-be-used", 99, "Wrong"));

            var field = Assert.IsType<ValueNode.AssetRef>(FieldOf(lowered));
            Assert.Equal("already-resolved", field.Ref!.Guid);
            Assert.Equal("Assets/Old.mat", field.Ref.DisplayPath);
        }

        [Fact]
        public void Lowering_NoneAssetRef_PassesThroughUnchanged()
        {
            var model = ModelWithField(new ValueNode.AssetRef(null));

            var lowered = AssetRefLowering.Lower(model, _ => ("guid", 0, "Material"));

            var field = Assert.IsType<ValueNode.AssetRef>(FieldOf(lowered));
            Assert.Null(field.Ref);
        }

        [Fact]
        public void Lowering_NonAssetRefField_Unchanged()
        {
            var model = ModelWithField(ValueNode.Primitive.Int(42));

            var lowered = AssetRefLowering.Lower(model, _ => ("guid", 0, "Material"));

            Assert.Equal(ValueNode.Primitive.Int(42), FieldOf(lowered));
        }

        private static System.Func<string, (string guid, long fileId, string typeHint)?> StubResolver(
            IDictionary<string, (string, long, string)> map)
        {
            return path => map.TryGetValue(path, out var hit) ? hit : ((string, long, string)?)null;
        }

        private static SceneModel ModelWithField(ValueNode value)
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("field", value),
            });
            var component = new ComponentData
            {
                LogicalId = "c1",
                Type = new TypeRef("UnityEngine.MeshRenderer"),
                Fields = fields,
            };
            var root = new GameObjectNode
            {
                LogicalId = "g1",
                Name = "Player",
                Components = new[] { component },
            };
            return new SceneModel { Roots = new[] { root } };
        }

        private static ValueNode FieldOf(SceneModel model) =>
            Assert.Single(Assert.Single(model.Roots).Components).Fields["field"];
    }
}
