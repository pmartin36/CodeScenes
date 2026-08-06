using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // AssetRefLowering.Lower descends List/Nested containers to reach an AssetRef leaf at any
    // depth: a ref reachable only through a Nested->List (or Nested->List->Nested) chain is
    // resolved exactly like a top-level ref, container shape/order and non-AssetRef leaves are
    // preserved unchanged, and an empty Nested/List nested inside another container survives
    // lowering rather than being dropped.
    public class AssetRefLoweringDescentTests
    {
        [Fact]
        public void Lowering_EmptyGuidAssetRefInsideListInsideNested_ResolvesToRealGuid()
        {
            var model = ModelWithField(new ValueNode.Nested("Game.Payload", FM(
                ("mats", new ValueNode.List(new ValueNode[]
                {
                    new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/A.mat" }),
                    new ValueNode.Nested("Game.Inner", FM(
                        ("deep", new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/Deep.mat" }))
                    )),
                }))
            )));
            var resolver = StubResolver(new Dictionary<string, (string, long, string)>
            {
                ["Assets/A.mat"] = ("guid-a", 0, "Material"),
                ["Assets/Deep.mat"] = ("guid-deep", 0, "Material"),
            });

            var lowered = AssetRefLowering.Lower(model, resolver);

            var nested = Assert.IsType<ValueNode.Nested>(FieldOf(lowered));
            var mats = Assert.IsType<ValueNode.List>(nested.Fields["mats"]);
            var flatRef = Assert.IsType<ValueNode.AssetRef>(mats.Items[0]);
            Assert.Equal("guid-a", flatRef.Ref!.Guid);
            Assert.Equal("Assets/A.mat", flatRef.Ref.DisplayPath);

            var innerNested = Assert.IsType<ValueNode.Nested>(mats.Items[1]);
            var deepRef = Assert.IsType<ValueNode.AssetRef>(innerNested.Fields["deep"]);
            Assert.Equal("guid-deep", deepRef.Ref!.Guid);
            Assert.Equal("Assets/Deep.mat", deepRef.Ref.DisplayPath);
        }

        [Fact]
        public void Lowering_BuiltinRefInsideListInsideNested_RoutesToBuiltinResolverAndPreservesAuthoredTypeHint()
        {
            var model = ModelWithField(new ValueNode.Nested("Game.Payload", FM(
                ("mats", new ValueNode.List(new ValueNode[]
                {
                    new ValueNode.AssetRef(new AssetRef { DisplayPath = "Cube", IsBuiltin = true }),
                }))
            )));
            var calls = new List<(string name, string? typeHint)>();
            Func<string, string?, (string guid, long fileId, string typeHint)?> builtinResolver = (name, typeHint) =>
            {
                calls.Add((name, typeHint));
                return ("0000000000000000e000000000000000", 10202L, "Mesh");
            };

            var lowered = AssetRefLowering.Lower(
                model, StubResolver(new Dictionary<string, (string, long, string)>()), builtinResolver);

            Assert.Equal(("Cube", (string?)null), Assert.Single(calls));
            var nested = Assert.IsType<ValueNode.Nested>(FieldOf(lowered));
            var mats = Assert.IsType<ValueNode.List>(nested.Fields["mats"]);
            var cube = Assert.IsType<ValueNode.AssetRef>(mats.Items[0]);
            Assert.Equal("0000000000000000e000000000000000", cube.Ref!.Guid);
            Assert.Equal(10202L, cube.Ref.FileId);
            Assert.Equal("", cube.Ref.TypeHint);
            Assert.Equal("Cube", cube.Ref.DisplayPath);
        }

        [Fact]
        public void Lowering_EmptyNestedAndEmptyListInsideAContainer_SurviveUnchanged()
        {
            var model = ModelWithField(new ValueNode.Nested("Game.Payload", FM(
                ("emptyNested", new ValueNode.Nested("Game.Empty", FM())),
                ("emptyList", new ValueNode.List(Array.Empty<ValueNode>()))
            )));

            var lowered = AssetRefLowering.Lower(model, StubResolver(new Dictionary<string, (string, long, string)>()));

            var nested = Assert.IsType<ValueNode.Nested>(FieldOf(lowered));
            var emptyNested = Assert.IsType<ValueNode.Nested>(nested.Fields["emptyNested"]);
            Assert.Equal("Game.Empty", emptyNested.TypeName);
            Assert.Empty(emptyNested.Fields);
            var emptyList = Assert.IsType<ValueNode.List>(nested.Fields["emptyList"]);
            Assert.Empty(emptyList.Items);
        }

        [Fact]
        public void Lowering_ContainerStructure_PreservesItemOrderTypeNameAndNonAssetLeaves()
        {
            var model = ModelWithField(new ValueNode.List(new ValueNode[]
            {
                ValueNode.Primitive.Int(1),
                new ValueNode.Nested("Game.Inner", FM(("x", ValueNode.Primitive.String("y")))),
                new ValueNode.AssetRef(null),
            }));

            var lowered = AssetRefLowering.Lower(model, StubResolver(new Dictionary<string, (string, long, string)>()));

            var list = Assert.IsType<ValueNode.List>(FieldOf(lowered));
            Assert.Equal(3, list.Items.Count);
            Assert.Equal(ValueNode.Primitive.Int(1), list.Items[0]);
            var nested = Assert.IsType<ValueNode.Nested>(list.Items[1]);
            Assert.Equal("Game.Inner", nested.TypeName);
            Assert.Equal(ValueNode.Primitive.String("y"), nested.Fields["x"]);
            var noneRef = Assert.IsType<ValueNode.AssetRef>(list.Items[2]);
            Assert.Null(noneRef.Ref);
        }

        [Fact]
        public void Lowering_ResolverCalledOncePerReachableAssetRef_AndNeverForOtherKinds()
        {
            var model = ModelWithField(new ValueNode.Nested("Game.Payload", FM(
                ("emptyNested", new ValueNode.Nested("Game.Empty", FM())),
                ("emptyList", new ValueNode.List(Array.Empty<ValueNode>())),
                ("mats", new ValueNode.List(new ValueNode[]
                {
                    new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/A.mat" }),
                    ValueNode.Primitive.Int(9),
                    new ValueNode.AssetRef(null),
                    new ValueNode.Nested("Game.Inner", FM(
                        ("deep", new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/Deep.mat" }))
                    )),
                }))
            )));
            var calls = new List<string>();
            Func<string, string?, (string guid, long fileId, string typeHint)?> spyResolver = (path, sub) =>
            {
                calls.Add(path + "|" + (sub ?? "<null>"));
                return path == "Assets/A.mat" ? null : ("guid-deep", 0L, "Material");
            };

            var lowered = AssetRefLowering.Lower(model, spyResolver);

            Assert.Equal(new[] { "Assets/A.mat|<null>", "Assets/Deep.mat|<null>" }, calls);
            var nested = Assert.IsType<ValueNode.Nested>(FieldOf(lowered));
            var mats = Assert.IsType<ValueNode.List>(nested.Fields["mats"]);
            var unresolvedRef = Assert.IsType<ValueNode.AssetRef>(mats.Items[0]);
            Assert.Equal("", unresolvedRef.Ref!.Guid);
            var noneRef = Assert.IsType<ValueNode.AssetRef>(mats.Items[2]);
            Assert.Null(noneRef.Ref);
            var deepNested = Assert.IsType<ValueNode.Nested>(mats.Items[3]);
            var deepRef = Assert.IsType<ValueNode.AssetRef>(deepNested.Fields["deep"]);
            Assert.Equal("guid-deep", deepRef.Ref!.Guid);
        }

        private static FieldMap FM(params (string key, ValueNode value)[] entries) =>
            new FieldMap(entries.Select(e => new KeyValuePair<string, ValueNode>(e.key, e.value)));

        private static Func<string, string?, (string guid, long fileId, string typeHint)?> StubResolver(
            IDictionary<string, (string, long, string)> map)
        {
            return (path, _) => map.TryGetValue(path, out var hit) ? hit : ((string, long, string)?)null;
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
