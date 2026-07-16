using System.Collections.Generic;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b2-t1: AssetRefLowering.Lower(SceneModel, resolver, builtinResolver?) fills
    // Guid/FileId/TypeHint on unresolved ValueNode.AssetRef nodes (field, list-nested,
    // nested-object), leaves everything else untouched, and never throws on a missing
    // resolver result. Built-in refs (IsBuiltin == true) route to builtinResolver, set
    // ONLY Guid/FileId, and preserve the authored TypeHint/DisplayPath verbatim (spec
    // #12-#16, specs/17-builtin-resources.md).
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
            var model = ModelWithField(new ValueNode.Nested("Game.ImpactData", nestedFields));
            var resolver = StubResolver(new Dictionary<string, (string, long, string)>
            {
                ["Assets/Inner.mat"] = ("guid-inner", 0, "Material"),
            });

            var lowered = AssetRefLowering.Lower(model, resolver);

            var nested = Assert.IsType<ValueNode.Nested>(FieldOf(lowered));
            Assert.Equal("Game.ImpactData", nested.TypeName);
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

        // ---- b2-t1: built-in ref lowering (spec #12-#16) ----

        [Fact]
        public void Lowering_BuiltinRef_RoutesToBuiltinResolverAndSetsGuidFileId()
        {
            var model = ModelWithField(new ValueNode.AssetRef(
                new AssetRef { DisplayPath = "Cube", IsBuiltin = true, TypeHint = "" }));
            var calls = new List<(string name, string? typeHint)>();
            var builtinResolver = StubBuiltinResolver(calls, new Dictionary<(string, string?), (string, long, string)>
            {
                [("Cube", null)] = ("0000000000000000e000000000000000", 10202L, "Mesh"),
            });

            var lowered = AssetRefLowering.Lower(model, StubResolver(new Dictionary<string, (string, long, string)>()), builtinResolver);

            var field = Assert.IsType<ValueNode.AssetRef>(FieldOf(lowered));
            Assert.Equal("0000000000000000e000000000000000", field.Ref!.Guid);
            Assert.Equal(10202L, field.Ref.FileId);
            Assert.Equal(("Cube", (string?)null), Assert.Single(calls));
        }

        [Fact]
        public void Lowering_BuiltinRef_PreservesAuthoredTypeHintAndDisplayPath()
        {
            // (a) bare form: authored TypeHint == "" must stay "" even though the resolver
            // returns a different, non-empty typeHint — stamping it would churn
            // Builtin("Cube") into Builtin("Cube", "Mesh") on every sync.
            var bareModel = ModelWithField(new ValueNode.AssetRef(
                new AssetRef { DisplayPath = "Cube", IsBuiltin = true, TypeHint = "" }));
            var bareResolver = StubBuiltinResolver(new List<(string, string?)>(),
                new Dictionary<(string, string?), (string, long, string)>
                {
                    [("Cube", null)] = ("0000000000000000e000000000000000", 10202L, "Mesh"),
                });

            var bareLowered = AssetRefLowering.Lower(bareModel, StubResolver(new Dictionary<string, (string, long, string)>()), bareResolver);

            var bareField = Assert.IsType<ValueNode.AssetRef>(FieldOf(bareLowered));
            Assert.Equal("0000000000000000e000000000000000", bareField.Ref!.Guid);
            Assert.Equal("", bareField.Ref.TypeHint);
            Assert.Equal("Cube", bareField.Ref.DisplayPath);

            // (b) qualified form: authored TypeHint ("Sprite") wins over the resolver's
            // returned typeHint ("Texture2D").
            var qualifiedModel = ModelWithField(new ValueNode.AssetRef(
                new AssetRef { DisplayPath = "UISprite", IsBuiltin = true, TypeHint = "Sprite" }));
            var qualifiedResolver = StubBuiltinResolver(new List<(string, string?)>(),
                new Dictionary<(string, string?), (string, long, string)>
                {
                    [("UISprite", "Sprite")] = ("0000000000000000f000000000000000", 10905L, "Texture2D"),
                });

            var qualifiedLowered = AssetRefLowering.Lower(qualifiedModel, StubResolver(new Dictionary<string, (string, long, string)>()), qualifiedResolver);

            var qualifiedField = Assert.IsType<ValueNode.AssetRef>(FieldOf(qualifiedLowered));
            Assert.Equal("0000000000000000f000000000000000", qualifiedField.Ref!.Guid);
            Assert.Equal("Sprite", qualifiedField.Ref.TypeHint);
            Assert.Equal("UISprite", qualifiedField.Ref.DisplayPath);
        }

        [Fact]
        public void Lowering_BuiltinResolverReturnsNull_LeavesNodeUnresolvedNoThrow()
        {
            var model = ModelWithField(new ValueNode.AssetRef(
                new AssetRef { DisplayPath = "Cube", IsBuiltin = true, TypeHint = "" }));
            var calls = new List<(string, string?)>();
            var builtinResolver = StubBuiltinResolver(calls, new Dictionary<(string, string?), (string, long, string)>());

            var lowered = AssetRefLowering.Lower(model, StubResolver(new Dictionary<string, (string, long, string)>()), builtinResolver);

            var field = Assert.IsType<ValueNode.AssetRef>(FieldOf(lowered));
            Assert.Equal("", field.Ref!.Guid);
            Assert.Equal("Cube", field.Ref.DisplayPath);
            Assert.Equal(("Cube", (string?)null), Assert.Single(calls));
        }

        [Fact]
        public void Lowering_NoBuiltinResolverSupplied_LeavesBuiltinNodeUnresolvedNoThrow()
        {
            var model = ModelWithField(new ValueNode.AssetRef(
                new AssetRef { DisplayPath = "Cube", IsBuiltin = true, TypeHint = "" }));
            Func<string, (string guid, long fileId, string typeHint)?> spyPathResolver = path =>
                throw new Xunit.Sdk.XunitException("path resolver called for a built-in: " + path);

            var lowered = AssetRefLowering.Lower(model, spyPathResolver);

            var field = Assert.IsType<ValueNode.AssetRef>(FieldOf(lowered));
            Assert.Equal("", field.Ref!.Guid);
        }

        [Fact]
        public void Lowering_NonBuiltinRef_DoesNotCallBuiltinResolver()
        {
            var parsed = BuilderParser.Parse(AssetPathSource);
            var pathResolver = StubResolver(new Dictionary<string, (string, long, string)>
            {
                ["Assets/Materials/Red.mat"] = ("guid-red-mat", 0, "Material"),
            });
            Func<string, string?, (string guid, long fileId, string typeHint)?> builtinResolverSpy = (name, hint) =>
                throw new Xunit.Sdk.XunitException($"builtin resolver called for a non-builtin ref: {name}");

            var lowered = AssetRefLowering.Lower(parsed.Model, pathResolver, builtinResolverSpy);

            var component = Assert.Single(Assert.Single(lowered.Roots).Components);
            var field = Assert.IsType<ValueNode.AssetRef>(component.Fields["sharedMaterial"]);
            Assert.Equal("guid-red-mat", field.Ref!.Guid);
            Assert.Equal("Material", field.Ref.TypeHint);
        }

        [Fact]
        public void Lowering_BuiltinRefInsideList_ResolvesEachElement()
        {
            var model = ModelWithField(new ValueNode.List(new ValueNode[]
            {
                new ValueNode.AssetRef(new AssetRef { DisplayPath = "Cube", IsBuiltin = true }),
                new ValueNode.AssetRef(new AssetRef { DisplayPath = "Sphere", IsBuiltin = true }),
            }));
            var builtinResolver = StubBuiltinResolver(new List<(string, string?)>(),
                new Dictionary<(string, string?), (string, long, string)>
                {
                    [("Cube", null)] = ("0000000000000000e000000000000000", 10202L, "Mesh"),
                    [("Sphere", null)] = ("0000000000000000e000000000000000", 10207L, "Mesh"),
                });

            var lowered = AssetRefLowering.Lower(model, StubResolver(new Dictionary<string, (string, long, string)>()), builtinResolver);

            var list = Assert.IsType<ValueNode.List>(FieldOf(lowered));
            var first = Assert.IsType<ValueNode.AssetRef>(list.Items[0]);
            var second = Assert.IsType<ValueNode.AssetRef>(list.Items[1]);
            Assert.Equal("0000000000000000e000000000000000", first.Ref!.Guid);
            Assert.Equal(10202L, first.Ref.FileId);
            Assert.Equal("", first.Ref.TypeHint);
            Assert.Equal("0000000000000000e000000000000000", second.Ref!.Guid);
            Assert.Equal(10207L, second.Ref.FileId);
            Assert.Equal("", second.Ref.TypeHint);
        }

        private static System.Func<string, (string guid, long fileId, string typeHint)?> StubResolver(
            IDictionary<string, (string, long, string)> map)
        {
            return path => map.TryGetValue(path, out var hit) ? hit : ((string, long, string)?)null;
        }

        private static Func<string, string?, (string guid, long fileId, string typeHint)?> StubBuiltinResolver(
            List<(string name, string? typeHint)> calls,
            IDictionary<(string name, string? typeHint), (string, long, string)> map)
        {
            return (name, typeHint) =>
            {
                calls.Add((name, typeHint));
                return map.TryGetValue((name, typeHint), out var hit) ? hit : ((string, long, string)?)null;
            };
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
