using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // SourceExpr.ValueNodeLiteral recurses through List/Nested containers by hand: a mixed
    // Nested->List value renders byte-exact, the assetCatalog argument threads down to an
    // AssetRef leaf reached below a container exactly as it does at the root, and an
    // unrecognized leaf kind still throws (naming its own kind) no matter how deep it sits.
    public class SourceExprDescentTests
    {
        private static ValueNode Roundtrip(string text) =>
            ValueNodeParser.Parse(SyntaxFactory.ParseExpression(text));

        [Fact]
        public void ValueNodeLiteral_NestedContainingAListOfAssetRefs_RendersByteExactGolden()
        {
            var node = new ValueNode.Nested("Game.Payload", FM(
                ("mats", new ValueNode.List(new ValueNode[]
                {
                    new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/A.mat" }),
                    new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/B.mat" }),
                })),
                ("count", ValueNode.Primitive.Int(2))
            ));

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal(
                @"new Game.Payload { mats = new[] { Asset(""Assets/A.mat""), Asset(""Assets/B.mat"") }, count = 2 }",
                text);
            Assert.Equal(node, Roundtrip(text));
        }

        [Fact]
        public void ValueNodeLiteral_ListOfNestedWithEmptyContainersAndEscapes_RendersByteExactGolden()
        {
            var node = new ValueNode.List(new ValueNode[]
            {
                new ValueNode.Nested("Game.Inner", FM(
                    ("label", ValueNode.Primitive.String("a\"b")),
                    ("items", new ValueNode.List(new ValueNode[]
                    {
                        ValueNode.Primitive.Int(1),
                        ValueNode.Primitive.Float(1.5f),
                    }))
                )),
                new ValueNode.Nested("Game.Inner", FM(
                    ("empty", new ValueNode.List(Array.Empty<ValueNode>())),
                    ("emptyNested", new ValueNode.Nested("Game.Empty", FM()))
                )),
            });

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal(
                @"new[] { new Game.Inner { label = ""a\""b"", items = new object[] { 1, 1.5f } }, new Game.Inner { empty = new object[] { }, emptyNested = new Game.Empty {  } } }",
                text);
            Assert.Equal(node, Roundtrip(text));
        }

        [Fact]
        public void ValueNodeLiteral_NestedWithMixedLeafKindsAndAList_RendersByteExactGolden()
        {
            var node = new ValueNode.Nested("Game.Mixed", FM(
                ("v3", new ValueNode.Vec3(new Vec3(1f, 2f, 3f))),
                ("flag", ValueNode.Primitive.Bool(true)),
                ("kind", new ValueNode.Enum("Game.Faction", new[] { "Enemy" }, false)),
                ("raw", new ValueNode.Unsupported("SomeExpr()")),
                ("builtin", new ValueNode.AssetRef(new AssetRef { DisplayPath = "Cube", IsBuiltin = true })),
                ("nums", new ValueNode.List(new ValueNode[] { ValueNode.Primitive.Int(3), ValueNode.Primitive.Int(1) }))
            ));

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal(
                @"new Game.Mixed { v3 = new UnityEngine.Vector3(1f, 2f, 3f), flag = true, kind = Game.Faction.Enemy, raw = SomeExpr(), builtin = Builtin(""Cube""), nums = new[] { 3, 1 } }",
                text);
        }

        [Fact]
        public void ValueNodeLiteral_HandleListAndNestedListOfLists_RendersByteExactGolden()
        {
            var node = new ValueNode.Nested("Game.Wiring", FM(
                ("targets", new ValueNode.List(new ValueNode[]
                {
                    new ValueNode.Unsupported("door"),
                    new ValueNode.Unsupported("tank"),
                })),
                ("inner", new ValueNode.Nested("Game.Inner", FM(
                    ("n", new ValueNode.List(new ValueNode[]
                    {
                        new ValueNode.List(new ValueNode[] { ValueNode.Primitive.Int(1) }),
                    }))
                )))
            ));

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal(
                @"new Game.Wiring { targets = new SceneBuilder.Authoring.SceneObjectHandle[] { door, tank }, inner = new Game.Inner { n = new[] { new[] { 1 } } } }",
                text);
        }

        [Fact]
        public void ValueNodeLiteral_CatalogThreadedThroughContainers_RendersTypedMemberChain()
        {
            var catalog = new AssetCatalog
            {
                Entries = new[]
                {
                    new AssetCatalogEntry
                    {
                        Group = "Materials",
                        FolderSegments = new[] { "Environment", "Rocks" },
                        MemberName = "Stone",
                        Guid = "abc123",
                        FileId = 0,
                        Path = "Assets/Environment/Rocks/Stone.mat",
                    },
                },
            };
            var node = new ValueNode.Nested("Game.Payload", FM(
                ("mats", new ValueNode.List(new ValueNode[]
                {
                    new ValueNode.AssetRef(new AssetRef
                    {
                        Guid = "abc123",
                        FileId = 0,
                        DisplayPath = "Assets/Environment/Rocks/Stone.mat",
                    }),
                    new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/Other/Rusty.mat" }),
                })),
                ("inner", new ValueNode.Nested("Game.Inner", FM(
                    ("one", new ValueNode.AssetRef(new AssetRef
                    {
                        Guid = "abc123",
                        FileId = 0,
                        DisplayPath = "Assets/Environment/Rocks/Stone.mat",
                    }))
                )))
            ));

            Assert.Equal(
                @"new Game.Payload { mats = new[] { Assets.Materials.Environment.Rocks.Stone, Asset(""Assets/Other/Rusty.mat"") }, inner = new Game.Inner { one = Assets.Materials.Environment.Rocks.Stone } }",
                SourceExpr.ValueNodeLiteral(node, catalog));
            Assert.Equal(
                @"new Game.Payload { mats = new[] { Asset(""Assets/Environment/Rocks/Stone.mat""), Asset(""Assets/Other/Rusty.mat"") }, inner = new Game.Inner { one = Asset(""Assets/Environment/Rocks/Stone.mat"") } }",
                SourceExpr.ValueNodeLiteral(node));
        }

        [Fact]
        public void ValueNodeLiteral_UnrecognizedLeafKindInsideContainers_ThrowsNamingTheKind()
        {
            var node = new ValueNode.Nested("Game.Wiring", FM(
                ("targets", new ValueNode.List(new ValueNode[]
                {
                    new ValueNode.ObjectRef("door"),
                }))
            ));

            var ex = Assert.Throws<NotSupportedException>(() => SourceExpr.ValueNodeLiteral(node));

            Assert.Equal("SourceExpr.ValueNodeLiteral: unsupported ValueNode kind ObjectRef", ex.Message);
        }

        private static FieldMap FM(params (string key, ValueNode value)[] entries) =>
            new FieldMap(entries.Select(e => new KeyValuePair<string, ValueNode>(e.key, e.value)));
    }
}
