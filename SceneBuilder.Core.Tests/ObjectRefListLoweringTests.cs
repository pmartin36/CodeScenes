using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A list of scene references (ValueNode.ObjectRef, mixed with AssetRef/Unsupported) lowers to
    // one SetArraySize carrying the authored length plus one per-index op -- SetReference for a
    // scene ref, SetAssetRef for a resolved asset ref, a SkippedField for an unresolved element.
    // No SetField is ever emitted for a reference-list path.
    public class ObjectRefListLoweringTests
    {
        private static IdentityMap ThreeGameObjectMap() =>
            new()
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-a", GlobalObjectId = "goid-a", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "root-b", GlobalObjectId = "goid-b", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "root-c", GlobalObjectId = "goid-c", Kind = "GameObject" },
                },
            };

        private static SceneModel ModelWith(ComponentData component) =>
            new()
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode { LogicalId = "root-a", Name = "Root", Components = new[] { component } },
                },
            };

        private static SceneSnapshot EmptySnapshot() =>
            new()
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-a", Name = "Root" } },
            };

        [Fact]
        public void ObjectRefList_LowersToArraySizeThenOrderedSetReferencePerIndex()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "waypoints",
                    new ValueNode.List(new ValueNode[]
                    {
                        new ValueNode.ObjectRef("root-b"),
                        new ValueNode.ObjectRef("root-c"),
                    })),
            });
            var component = new ComponentData { LogicalId = "path-1", Type = new TypeRef("Game.WaypointPath"), Fields = desiredFields };

            var plan = Materializer.Materialize(ModelWith(component), EmptySnapshot(), ThreeGameObjectMap());

            var sizeOp = Assert.Single(plan.Ops.OfType<SetArraySize>(), o => o.LogicalId == "path-1");
            Assert.Equal("waypoints", sizeOp.Path);
            Assert.Equal(2, sizeOp.Size);

            var refOps = plan.Ops.OfType<SetReference>().Where(o => o.LogicalId == "path-1").ToArray();
            Assert.Equal(2, refOps.Length);
            Assert.Equal(new[] { "waypoints[0]", "waypoints[1]" }, refOps.Select(o => o.Path));
            Assert.Equal("root-b", refOps[0].TargetLogicalId);
            Assert.Equal("root-c", refOps[1].TargetLogicalId);

            Assert.DoesNotContain(plan.Ops.OfType<SetField>(), sf => sf.LogicalId == "path-1" && sf.Path == "waypoints");
        }

        [Fact]
        public void ObjectRefList_NullElement_LowersToClearSetReferenceNotSkipped()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "waypoints",
                    new ValueNode.List(new ValueNode[]
                    {
                        new ValueNode.ObjectRef("root-b"),
                        new ValueNode.ObjectRef(null),
                    })),
            });
            var component = new ComponentData { LogicalId = "path-1", Type = new TypeRef("Game.WaypointPath"), Fields = desiredFields };

            var plan = Materializer.Materialize(ModelWith(component), EmptySnapshot(), ThreeGameObjectMap());

            var second = Assert.Single(plan.Ops.OfType<SetReference>(), o => o.LogicalId == "path-1" && o.Path == "waypoints[1]");
            Assert.True(string.IsNullOrEmpty(second.TargetLogicalId));

            Assert.DoesNotContain(plan.Skipped, sk => sk.LogicalId == "path-1" && sk.Path == "waypoints[1]");
        }

        [Fact]
        public void ObjectRefList_AuthoredShorterThanLiveArray_ShrinksViaSetArraySize()
        {
            var actualFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "waypoints",
                    new ValueNode.List(new ValueNode[]
                    {
                        new ValueNode.ObjectRef("root-a"),
                        new ValueNode.ObjectRef("root-b"),
                        new ValueNode.ObjectRef("root-c"),
                    })),
            });
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "waypoints",
                    new ValueNode.List(new ValueNode[]
                    {
                        new ValueNode.ObjectRef("root-b"),
                        new ValueNode.ObjectRef("root-c"),
                    })),
            });
            var desiredComponent = new ComponentData { LogicalId = "path-1", Type = new TypeRef("Game.WaypointPath"), Fields = desiredFields };
            var actualComponent = new ComponentData { LogicalId = "path-1", Type = new TypeRef("Game.WaypointPath"), Fields = actualFields };

            var model = ModelWith(desiredComponent);
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-a", Name = "Root", Components = new[] { actualComponent } } },
            };

            var plan = Materializer.Materialize(model, snapshot, ThreeGameObjectMap());

            var sizeOp = Assert.Single(plan.Ops.OfType<SetArraySize>(), o => o.LogicalId == "path-1" && o.Path == "waypoints");
            Assert.Equal(2, sizeOp.Size);

            Assert.DoesNotContain(plan.Ops.OfType<SetReference>(), o => o.LogicalId == "path-1" && o.Path == "waypoints[2]");
        }

        [Fact]
        public void MixedReferenceList_ObjectRefAndAssetRef_LowersEachThroughItsOwnArm()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "refs",
                    new ValueNode.List(new ValueNode[]
                    {
                        new ValueNode.ObjectRef("root-b"),
                        new ValueNode.AssetRef(new AssetRef { Guid = "guid-a", FileId = 0, DisplayPath = "Assets/Materials/A.mat" }),
                    })),
            });
            var component = new ComponentData { LogicalId = "path-1", Type = new TypeRef("Game.MixedRefs"), Fields = desiredFields };

            var plan = Materializer.Materialize(ModelWith(component), EmptySnapshot(), ThreeGameObjectMap());

            var sizeOp = Assert.Single(plan.Ops.OfType<SetArraySize>(), o => o.LogicalId == "path-1" && o.Path == "refs");
            Assert.Equal(2, sizeOp.Size);

            var refOp = Assert.Single(plan.Ops.OfType<SetReference>(), o => o.LogicalId == "path-1");
            Assert.Equal("refs[0]", refOp.Path);
            Assert.Equal("root-b", refOp.TargetLogicalId);

            var assetOp = Assert.Single(plan.Ops.OfType<SetAssetRef>(), o => o.LogicalId == "path-1");
            Assert.Equal("refs[1]", assetOp.Path);
            Assert.Equal("guid-a", assetOp.Guid);

            Assert.DoesNotContain(plan.Ops.OfType<SetField>(), sf => sf.LogicalId == "path-1" && sf.Path == "refs");
        }

        [Fact]
        public void ObjectRefList_UnresolvedHandleElement_EmitsSkippedFieldKeepsResolvedSibling()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "waypoints",
                    new ValueNode.List(new ValueNode[]
                    {
                        new ValueNode.ObjectRef("root-b"),
                        new ValueNode.Unsupported("ghost"),
                    })),
            });
            var component = new ComponentData { LogicalId = "path-1", Type = new TypeRef("Game.WaypointPath"), Fields = desiredFields };

            var plan = Materializer.Materialize(ModelWith(component), EmptySnapshot(), ThreeGameObjectMap());

            var sizeOp = Assert.Single(plan.Ops.OfType<SetArraySize>(), o => o.LogicalId == "path-1" && o.Path == "waypoints");
            Assert.Equal(2, sizeOp.Size);

            var refOp = Assert.Single(plan.Ops.OfType<SetReference>(), o => o.LogicalId == "path-1" && o.Path == "waypoints[0]");
            Assert.Equal("root-b", refOp.TargetLogicalId);

            var skip = Assert.Single(plan.Skipped, sk => sk.LogicalId == "path-1" && sk.Path == "waypoints[1]");
            Assert.Equal("Unsupported", skip.Reason);
        }

        [Fact]
        public void AssetRefOnlyList_AlsoEmitsSetArraySizeCarryingAuthoredCount()
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
            var component = new ComponentData { LogicalId = "renderer-1", Type = new TypeRef("Game.MultiRenderer"), Fields = desiredFields };

            var plan = Materializer.Materialize(ModelWith(component), EmptySnapshot(), ThreeGameObjectMap());

            var sizeOp = Assert.Single(plan.Ops.OfType<SetArraySize>(), o => o.LogicalId == "renderer-1" && o.Path == "materials");
            Assert.Equal(2, sizeOp.Size);

            var assetOps = plan.Ops.OfType<SetAssetRef>().Where(op => op.LogicalId == "renderer-1").ToArray();
            Assert.Equal(2, assetOps.Length);
            Assert.Equal(new[] { "materials[0]", "materials[1]" }, assetOps.Select(op => op.Path));
        }

        [Fact]
        public void NestedObjectRefMember_StaysASingleSetFieldWithNoSetReference()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "config",
                    new ValueNode.Nested("Game.RefHolder", new FieldMap(new[]
                    {
                        new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef("root-b")),
                    }))),
            });
            var component = new ComponentData { LogicalId = "holder-1", Type = new TypeRef("Game.HasNestedRef"), Fields = desiredFields };

            var plan = Materializer.Materialize(ModelWith(component), EmptySnapshot(), ThreeGameObjectMap());

            Assert.Single(plan.Ops.OfType<SetField>(), sf => sf.LogicalId == "holder-1" && sf.Path == "config");
            Assert.DoesNotContain(plan.Ops.OfType<SetReference>(), o => o.LogicalId == "holder-1");
        }
    }
}
