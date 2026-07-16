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

        // m-builtin-resources b2-t2 #17: a RESOLVED built-in ref field is unchanged — the container
        // guid and non-zero fileId survive into the emitted op exactly as a non-built-in ref would.
        // Container guid/fileId are TEST DATA only (never hardcoded in Core).
        [Fact]
        public void Materialize_ResolvedBuiltinAssetRefField_EmitsSetAssetRef()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "mesh",
                    new ValueNode.AssetRef(new AssetRef
                    {
                        Guid = "0000000000000000e000000000000000",
                        FileId = 10202,
                        DisplayPath = "Cube",
                        IsBuiltin = true,
                    })),
            });
            var actualFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "mesh",
                    new ValueNode.AssetRef(new AssetRef { Guid = "guid-model", FileId = 0, DisplayPath = "Assets/Models/Robot.fbx" })),
            });

            var desiredComponent = new ComponentData { LogicalId = "mesh-1", Type = new TypeRef("Game.MeshHolder"), Fields = desiredFields };
            var actualComponent = new ComponentData { LogicalId = "mesh-1", Type = new TypeRef("Game.MeshHolder"), Fields = actualFields };

            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualComponent } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var plan = Materializer.Materialize(model, snapshot, MatchedGameObjectMap());

            var op = Assert.Single(plan.Ops.OfType<SetAssetRef>(), o => o.LogicalId == "mesh-1" && o.Path == "mesh");
            Assert.Equal("0000000000000000e000000000000000", op.Guid);
            Assert.Equal(10202, op.FileId);

            Assert.DoesNotContain(plan.Skipped, sk => sk.LogicalId == "mesh-1" && sk.Path == "mesh");
        }

        // m-builtin-resources b2-t2 #18: a RESOLVED built-in ref LIST is unchanged — ordered
        // SetAssetRef per index, each carrying its own container guid + fileId.
        [Fact]
        public void Materialize_ResolvedBuiltinAssetRefList_EmitsOrderedSetAssetRefPerIndex()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "materials",
                    new ValueNode.List(new ValueNode[]
                    {
                        new ValueNode.AssetRef(new AssetRef
                        {
                            Guid = "0000000000000000e000000000000000",
                            FileId = 10202,
                            DisplayPath = "Cube",
                            IsBuiltin = true,
                        }),
                        new ValueNode.AssetRef(new AssetRef
                        {
                            Guid = "0000000000000000e000000000000000",
                            FileId = 10207,
                            DisplayPath = "Sphere",
                            IsBuiltin = true,
                        }),
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
            Assert.Equal("0000000000000000e000000000000000", assetOps[0].Guid);
            Assert.Equal(10202, assetOps[0].FileId);
            Assert.Equal("0000000000000000e000000000000000", assetOps[1].Guid);
            Assert.Equal(10207, assetOps[1].FileId);

            Assert.Empty(plan.Skipped.Where(sk => sk.LogicalId == "renderer-1"));
        }

        // m-builtin-resources b2-t2: a ref that names something (DisplayPath != "") but never
        // resolved (Guid == "") must be SKIPPED, not lowered to a null-GUID SetAssetRef — the
        // adapter treats a null/empty guid as "clear the field" (AssetReferenceResolver.cs:239-244),
        // so emitting it here would silently destroy the live scene value. #19.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Materialize_UnresolvedAssetRefField_SkipsInsteadOfClearing(bool isBuiltin)
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "material",
                    new ValueNode.AssetRef(new AssetRef { Guid = "", FileId = 0, DisplayPath = "Cube", IsBuiltin = isBuiltin })),
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

            Assert.DoesNotContain(plan.Ops.OfType<SetAssetRef>(), op => op.LogicalId == "weapon-1" && op.Path == "material");
            var skip = Assert.Single(plan.Skipped, sk => sk.LogicalId == "weapon-1" && sk.Path == "material");
            Assert.Equal("Unresolved", skip.Reason);
        }

        // #20 (regression pin): Ref == null is the genuine None/clear form and must keep clearing —
        // the unresolved guard must not swallow it. Materialize_NullAssetRef_ClearsField above already
        // asserts this against current code; this is an explicit companion asserting Skipped stays empty.
        [Fact]
        public void Materialize_NullAssetRef_DoesNotAppearInSkipped()
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

            Assert.Empty(plan.Skipped.Where(sk => sk.LogicalId == "weapon-1" && sk.Path == "material"));
        }

        // The list half of the guard — an unresolved element must be skipped WITHOUT dropping its
        // resolved siblings, which keep emitting at their ORIGINAL indices. A whole-list skip would
        // pass a test that only checks "no op at [1]"; this asserts the survivors too.
        [Fact]
        public void Materialize_AssetRefList_SkipsUnresolvedElementKeepsSiblings()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "materials",
                    new ValueNode.List(new ValueNode[]
                    {
                        new ValueNode.AssetRef(new AssetRef { Guid = "guid-a", FileId = 0, DisplayPath = "Assets/Materials/A.mat" }),
                        new ValueNode.AssetRef(new AssetRef { Guid = "", FileId = 0, DisplayPath = "Missing" }),
                        new ValueNode.AssetRef(new AssetRef { Guid = "guid-c", FileId = 0, DisplayPath = "Assets/Materials/C.mat" }),
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
            Assert.Equal(new[] { "materials[0]", "materials[2]" }, assetOps.Select(op => op.Path));
            Assert.Equal("guid-a", assetOps[0].Guid);
            Assert.Equal("guid-c", assetOps[1].Guid);

            Assert.DoesNotContain(plan.Ops.OfType<SetAssetRef>(), op => op.LogicalId == "renderer-1" && op.Path == "materials[1]");
            var skip = Assert.Single(plan.Skipped, sk => sk.LogicalId == "renderer-1" && sk.Path == "materials[1]");
            Assert.Equal("Unresolved", skip.Reason);
        }

        // Degenerate shape pin: Asset("") / Builtin("") — Ref != null, Guid == "", DisplayPath == "" —
        // names nothing, is not the None form (Ref == null exclusively), and must be skipped rather
        // than emitted as a clearing SetAssetRef.
        [Fact]
        public void Materialize_EmptyDisplayPathUnresolvedRef_SkipsInsteadOfClearing()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "material",
                    new ValueNode.AssetRef(new AssetRef { Guid = "", FileId = 0, DisplayPath = "" })),
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

            Assert.DoesNotContain(plan.Ops.OfType<SetAssetRef>(), op => op.LogicalId == "weapon-1" && op.Path == "material");
            var skip = Assert.Single(plan.Skipped, sk => sk.LogicalId == "weapon-1" && sk.Path == "material");
            Assert.Equal("Unresolved", skip.Reason);
        }
    }
}
