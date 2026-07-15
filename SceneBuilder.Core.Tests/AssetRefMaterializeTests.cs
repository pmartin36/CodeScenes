using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // m4-asset-references b3-t3: Materializer emits SetAssetRef (not SetField) for
    // ValueNode.AssetRef fields — populated, list-per-index, and None/clear.
    public class AssetRefMaterializeTests
    {
        private static IdentityMap MatchedGameObjectMap(string logicalId = "root-1", string goid = "goid-root") =>
            new()
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = logicalId, GlobalObjectId = goid, Kind = "GameObject" },
                },
            };

        [Fact]
        public void Materialize_AssetRefField_EmitsSetAssetRef()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "material",
                    new ValueNode.AssetRef(new AssetRef { Guid = "guid-red", FileId = 0, DisplayPath = "Assets/Materials/Red.mat" })),
            });
            var actualFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "material",
                    new ValueNode.AssetRef(new AssetRef { Guid = "guid-blue", FileId = 0, DisplayPath = "Assets/Materials/Blue.mat" })),
            });

            var desiredComponent = new ComponentData { LogicalId = "weapon-1", Type = new TypeRef("Game.Weapon"), Fields = desiredFields };
            var actualComponent = new ComponentData { LogicalId = "weapon-1", Type = new TypeRef("Game.Weapon"), Fields = actualFields };

            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualComponent } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var plan = Materializer.Materialize(model, snapshot, MatchedGameObjectMap());

            var assetOps = plan.Ops.OfType<SetAssetRef>().Where(op => op.LogicalId == "weapon-1").ToArray();
            var op = Assert.Single(assetOps);
            Assert.Equal("material", op.Path);
            Assert.Equal("guid-red", op.Guid);
            Assert.Equal(0, op.FileId);

            Assert.DoesNotContain(plan.Ops.OfType<SetField>(), sf => sf.LogicalId == "weapon-1" && sf.Path == "material");
        }

        [Fact]
        public void Materialize_AssetRefList_EmitsOrderedSetAssetRefPerIndex()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "materials",
                    new ValueNode.List(new ValueNode[]
                    {
                        new ValueNode.AssetRef(new AssetRef { Guid = "guid-a", FileId = 0, DisplayPath = "Assets/Materials/A.mat" }),
                        new ValueNode.AssetRef(new AssetRef { Guid = "guid-b", FileId = 1, DisplayPath = "Assets/Materials/B.mat" }),
                    })),
            });

            var desiredComponent = new ComponentData { LogicalId = "renderer-1", Type = new TypeRef("Game.MultiRenderer"), Fields = desiredFields };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var plan = Materializer.Materialize(model, snapshot, MatchedGameObjectMap());

            var assetOps = plan.Ops.OfType<SetAssetRef>().Where(op => op.LogicalId == "renderer-1").ToArray();
            Assert.Equal(2, assetOps.Length);
            Assert.Equal(new[] { "materials[0]", "materials[1]" }, assetOps.Select(op => op.Path));
            Assert.Equal("guid-a", assetOps[0].Guid);
            Assert.Equal(0, assetOps[0].FileId);
            Assert.Equal("guid-b", assetOps[1].Guid);
            Assert.Equal(1, assetOps[1].FileId);

            Assert.DoesNotContain(plan.Ops.OfType<SetField>(), sf => sf.LogicalId == "renderer-1" && sf.Path == "materials");
        }

        [Fact]
        public void SubObjectFileId_RoundTripsAndCarriesInSetAssetRef()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "mesh",
                    new ValueNode.AssetRef(new AssetRef { Guid = "guid-model", FileId = 4, DisplayPath = "Assets/Models/Robot.fbx" })),
            });
            var actualFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "mesh",
                    new ValueNode.AssetRef(new AssetRef { Guid = "guid-model", FileId = 2, DisplayPath = "Assets/Models/Robot.fbx" })),
            });

            var desiredComponent = new ComponentData { LogicalId = "mesh-1", Type = new TypeRef("Game.MeshHolder"), Fields = desiredFields };
            var actualComponent = new ComponentData { LogicalId = "mesh-1", Type = new TypeRef("Game.MeshHolder"), Fields = actualFields };

            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualComponent } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var plan = Materializer.Materialize(model, snapshot, MatchedGameObjectMap());

            var op = Assert.Single(plan.Ops.OfType<SetAssetRef>(), o => o.LogicalId == "mesh-1");
            Assert.Equal(4, op.FileId);
            Assert.Equal("guid-model", op.Guid);
        }

        [Fact]
        public void Materialize_NullAssetRef_ClearsField()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("material", new ValueNode.AssetRef(null)),
            });
            var actualFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "material",
                    new ValueNode.AssetRef(new AssetRef { Guid = "guid-red", FileId = 0, DisplayPath = "Assets/Materials/Red.mat" })),
            });

            var desiredComponent = new ComponentData { LogicalId = "weapon-1", Type = new TypeRef("Game.Weapon"), Fields = desiredFields };
            var actualComponent = new ComponentData { LogicalId = "weapon-1", Type = new TypeRef("Game.Weapon"), Fields = actualFields };

            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualComponent } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var plan = Materializer.Materialize(model, snapshot, MatchedGameObjectMap());

            var op = Assert.Single(plan.Ops.OfType<SetAssetRef>(), o => o.LogicalId == "weapon-1" && o.Path == "material");
            Assert.True(string.IsNullOrEmpty(op.Guid));
            Assert.Equal(0, op.FileId);

            Assert.DoesNotContain(plan.Skipped, sk => sk.LogicalId == "weapon-1" && sk.Path == "material");
            Assert.DoesNotContain(plan.Ops.OfType<SetField>(), sf => sf.LogicalId == "weapon-1" && sf.Path == "material");
        }
    }
}
