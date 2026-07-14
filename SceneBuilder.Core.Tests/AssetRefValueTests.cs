using System.Collections.Generic;
using System.Text.Json;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b1-t1: AssetRef POCO + ValueNode.AssetRef + canonical serialize + SourceExpr rendering.
    public class AssetRefValueTests
    {
        private static SceneModel ModelWith(ValueNode value)
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("material", value),
            });

            return new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "go-1",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "comp-1",
                                Type = new TypeRef("UnityEngine.MeshRenderer"),
                                Fields = fields,
                            },
                        },
                    },
                },
            };
        }

        private static ValueNode ExtractField(SceneModel model) =>
            model.Roots[0].Components[0].Fields["material"];

        [Fact]
        public void AssetRef_EqualityKeysOnGuidFileIdOnly()
        {
            var a = new AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Materials/Red.mat", TypeHint = "Material" };
            var sameIdentityDifferentPath = new AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Materials/Renamed.mat", TypeHint = "Material" };
            var differentFileId = new AssetRef { Guid = "abc123", FileId = 2, DisplayPath = "Assets/Materials/Red.mat" };
            var differentGuid = new AssetRef { Guid = "zzz999", FileId = 0, DisplayPath = "Assets/Materials/Red.mat" };

            Assert.Equal(a, sameIdentityDifferentPath);
            Assert.Equal(a.GetHashCode(), sameIdentityDifferentPath.GetHashCode());
            Assert.NotEqual(a, differentFileId);
            Assert.NotEqual(a, differentGuid);
        }

        [Fact]
        public void ValueNodeAssetRef_NullVsPopulated_DistinctAndBothRoundTrip()
        {
            var populated = new ValueNode.AssetRef(new AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Materials/Red.mat", TypeHint = "Material" });
            var none = new ValueNode.AssetRef(null);
            var noneAgain = new ValueNode.AssetRef(null);

            Assert.NotEqual(populated, none);
            Assert.Equal(none, noneAgain);

            var populatedJson = SceneModelSerializer.Serialize(ModelWith(populated));
            var noneJson = SceneModelSerializer.Serialize(ModelWith(none));
            Assert.NotEqual(populatedJson, noneJson);

            using var populatedDoc = JsonDocument.Parse(populatedJson);
            var populatedRefElement = populatedDoc.RootElement
                .GetProperty("roots")[0].GetProperty("components")[0].GetProperty("fields").GetProperty("material").GetProperty("ref");
            Assert.Equal(JsonValueKind.Object, populatedRefElement.ValueKind);

            using var noneDoc = JsonDocument.Parse(noneJson);
            var noneRefElement = noneDoc.RootElement
                .GetProperty("roots")[0].GetProperty("components")[0].GetProperty("fields").GetProperty("material").GetProperty("ref");
            Assert.Equal(JsonValueKind.Null, noneRefElement.ValueKind);

            var populatedBack = ExtractField(SceneModelSerializer.Deserialize(populatedJson));
            var noneBack = ExtractField(SceneModelSerializer.Deserialize(noneJson));
            Assert.Equal(populated, populatedBack);
            Assert.Equal(none, noneBack);
        }

        [Fact]
        public void AssetRef_CanonicalRoundTrip_PreservesGuidFileIdTypeHint()
        {
            var original = new ValueNode.AssetRef(new AssetRef
            {
                Guid = "abc123",
                FileId = 4,
                TypeHint = "Material",
                DisplayPath = "Assets/Materials/Red.mat",
            });
            var model = ModelWith(original);

            var json = SceneModelSerializer.Serialize(model);
            var back = SceneModelSerializer.Deserialize(json);
            var field = ExtractField(back);

            Assert.Equal(original, field);
            var assetRef = Assert.IsType<ValueNode.AssetRef>(field);
            Assert.NotNull(assetRef.Ref);
            Assert.Equal("abc123", assetRef.Ref!.Guid);
            Assert.Equal(4, assetRef.Ref.FileId);
            Assert.Equal("Material", assetRef.Ref.TypeHint);
            Assert.Equal("Assets/Materials/Red.mat", assetRef.Ref.DisplayPath);
        }

        [Fact]
        public void SourceExpr_RendersAssetPopulatedAndNull()
        {
            var populated = new ValueNode.AssetRef(new AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Materials/A \"Weird\" One.mat" });
            var none = new ValueNode.AssetRef(null);

            var populatedText = SourceExpr.ValueNodeLiteral(populated);
            var noneText = SourceExpr.ValueNodeLiteral(none);

            Assert.Equal("Asset(" + SourceExpr.StringLiteral("Assets/Materials/A \"Weird\" One.mat") + ")", populatedText);
            Assert.Equal("Asset(null)", noneText);
        }
    }
}
