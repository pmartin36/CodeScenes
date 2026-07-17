using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // m5-cross-object-references b3-t1: Materializer emits SetReference (not SetField) for
    // ValueNode.ObjectRef fields — populated, null/clear, and mutual A<->B references.
    public class ObjectRefMaterializeTests
    {
        private static IdentityMap TwoGameObjectMap() =>
            new()
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-a", GlobalObjectId = "goid-a", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "root-b", GlobalObjectId = "goid-b", Kind = "GameObject" },
                },
            };

        [Fact]
        public void Materialize_ObjectRefField_EmitsSetReference()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef("root-b")),
            });
            var desiredComponent = new ComponentData { LogicalId = "opener-1", Type = new TypeRef("Game.DoorOpener"), Fields = desiredFields };
            var root = new GameObjectNode { LogicalId = "root-a", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-a", Name = "Root" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var plan = Materializer.Materialize(model, snapshot, TwoGameObjectMap());

            var op = Assert.Single(plan.Ops.OfType<SetReference>(), o => o.LogicalId == "opener-1");
            Assert.Equal("target", op.Path);
            Assert.Equal("root-b", op.TargetLogicalId);

            Assert.DoesNotContain(plan.Ops.OfType<SetField>(), sf => sf.LogicalId == "opener-1" && sf.Path == "target");
        }

        [Fact]
        public void Materialize_NullObjectRef_EmitsClearSetReference()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(null)),
            });
            var actualFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef("root-b")),
            });
            var desiredComponent = new ComponentData { LogicalId = "opener-1", Type = new TypeRef("Game.DoorOpener"), Fields = desiredFields };
            var actualComponent = new ComponentData { LogicalId = "opener-1", Type = new TypeRef("Game.DoorOpener"), Fields = actualFields };

            var root = new GameObjectNode { LogicalId = "root-a", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-a", Name = "Root", Components = new[] { actualComponent } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var plan = Materializer.Materialize(model, snapshot, TwoGameObjectMap());

            var op = Assert.Single(plan.Ops.OfType<SetReference>(), o => o.LogicalId == "opener-1" && o.Path == "target");
            Assert.True(string.IsNullOrEmpty(op.TargetLogicalId));

            Assert.DoesNotContain(plan.Skipped, sk => sk.LogicalId == "opener-1" && sk.Path == "target");
            Assert.DoesNotContain(plan.Ops.OfType<SetField>(), sf => sf.LogicalId == "opener-1" && sf.Path == "target");
        }

        [Fact]
        public void Materialize_MutualReference_EmitsBothSetReferences()
        {
            var componentA = new ComponentData
            {
                LogicalId = "a-comp",
                Type = new TypeRef("Game.DoorOpener"),
                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef("root-b")) }),
            };
            var componentB = new ComponentData
            {
                LogicalId = "b-comp",
                Type = new TypeRef("Game.DoorOpener"),
                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef("root-a")) }),
            };
            var rootA = new GameObjectNode { LogicalId = "root-a", Name = "A", Components = new[] { componentA } };
            var rootB = new GameObjectNode { LogicalId = "root-b", Name = "B", Components = new[] { componentB } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { rootA, rootB } };

            var snapshotA = new SnapshotNode { GlobalObjectId = "goid-a", Name = "A" };
            var snapshotB = new SnapshotNode { GlobalObjectId = "goid-b", Name = "B" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotA, snapshotB } };

            var plan = Materializer.Materialize(model, snapshot, TwoGameObjectMap());

            var opA = Assert.Single(plan.Ops.OfType<SetReference>(), o => o.LogicalId == "a-comp");
            Assert.Equal("root-b", opA.TargetLogicalId);
            var opB = Assert.Single(plan.Ops.OfType<SetReference>(), o => o.LogicalId == "b-comp");
            Assert.Equal("root-a", opB.TargetLogicalId);
        }
    }
}
