using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        public void AssetRef_EqualityKeysOnGuidFileIdAndIsBuiltin()
        {
            var a = new AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Materials/Red.mat", TypeHint = "Material" };
            var sameIdentityDifferentPath = new AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Materials/Renamed.mat", TypeHint = "Material" };
            var differentFileId = new AssetRef { Guid = "abc123", FileId = 2, DisplayPath = "Assets/Materials/Red.mat" };
            var differentGuid = new AssetRef { Guid = "zzz999", FileId = 0, DisplayPath = "Assets/Materials/Red.mat" };

            Assert.Equal(a, sameIdentityDifferentPath);
            Assert.Equal(a.GetHashCode(), sameIdentityDifferentPath.GetHashCode());
            Assert.NotEqual(a, differentFileId);
            Assert.NotEqual(a, differentGuid);

            var unresolvedAsset = new AssetRef { Guid = "", FileId = 0, DisplayPath = "Cube", IsBuiltin = false };
            var unresolvedBuiltin = new AssetRef { Guid = "", FileId = 0, DisplayPath = "Cube", IsBuiltin = true };
            Assert.NotEqual(unresolvedAsset, unresolvedBuiltin);
        }

        [Fact]
        public void AssetRef_GetHashCode_DiffersOnIsBuiltin()
        {
            var asAsset = new AssetRef { Guid = "", FileId = 0, DisplayPath = "Cube", IsBuiltin = false };
            var asBuiltin = new AssetRef { Guid = "", FileId = 0, DisplayPath = "Cube", IsBuiltin = true };

            Assert.NotEqual(asAsset.GetHashCode(), asBuiltin.GetHashCode());
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
        public void ValueNodeAssetRef_Builtin_CanonicalRoundTrips()
        {
            var original = new ValueNode.AssetRef(new AssetRef
            {
                Guid = "",
                FileId = 0,
                TypeHint = "",
                DisplayPath = "Cube",
                IsBuiltin = true,
            });
            var model = ModelWith(original);

            var json = SceneModelSerializer.Serialize(model);

            using var doc = JsonDocument.Parse(json);
            var refElement = doc.RootElement
                .GetProperty("roots")[0].GetProperty("components")[0].GetProperty("fields").GetProperty("material").GetProperty("ref");
            Assert.True(refElement.TryGetProperty("isBuiltin", out var isBuiltinElement));
            Assert.Equal(JsonValueKind.True, isBuiltinElement.ValueKind);

            var back = SceneModelSerializer.Deserialize(json);
            var field = ExtractField(back);

            Assert.Equal(original, field);
            var assetRef = Assert.IsType<ValueNode.AssetRef>(field);
            Assert.NotNull(assetRef.Ref);
            Assert.True(assetRef.Ref!.IsBuiltin);
            Assert.Equal("Cube", assetRef.Ref.DisplayPath);
        }

        [Fact]
        public void AssetRef_LegacyJsonWithoutIsBuiltin_DeserializesToFalse()
        {
            // Simulate a sidecar/model persisted before IsBuiltin existed by stripping the
            // property from a real canonical serialization, rather than hand-authoring a
            // fixture that could drift from the real JSON shape.
            var original = new ValueNode.AssetRef(new AssetRef
            {
                Guid = "abc123",
                FileId = 4,
                TypeHint = "Material",
                DisplayPath = "Assets/Materials/Red.mat",
            });
            var currentJson = SceneModelSerializer.Serialize(ModelWith(original));
            // Strip the property along with whichever adjacent comma keeps the JSON valid,
            // regardless of whether isBuiltin is serialized as the last property (no comma
            // after) or not (comma after, none before).
            var legacyJson = Regex.Replace(currentJson, @",\s*""isBuiltin""\s*:\s*(?:true|false)", "");
            if (legacyJson == currentJson)
            {
                legacyJson = Regex.Replace(currentJson, @"""isBuiltin""\s*:\s*(?:true|false),\s*", "");
            }
            Assert.DoesNotContain("isBuiltin", legacyJson);

            var model = SceneModelSerializer.Deserialize(legacyJson);
            var field = ExtractField(model);
            var assetRef = Assert.IsType<ValueNode.AssetRef>(field);

            Assert.NotNull(assetRef.Ref);
            Assert.False(assetRef.Ref!.IsBuiltin);
            Assert.Equal("abc123", assetRef.Ref.Guid);
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
