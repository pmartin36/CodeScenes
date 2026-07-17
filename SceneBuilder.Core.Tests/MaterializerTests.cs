using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class MaterializerTests
    {
        [Fact]
        public void Materialize_OrdersParentsBeforeChildren_AndParentingAfterCreation()
        {
            var child = new GameObjectNode { LogicalId = "child-1", Name = "Child" };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Children = new[] { child } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };
            var map = new IdentityMap { Scene = "Assets/Scenes/Demo.unity" };

            var plan = Materializer.Materialize(model, snapshot, map);

            var ops = plan.Ops;
            var rootCreateIndex = System.Array.FindIndex(ops, op => op is CreateObject co && co.LogicalId == "root-1");
            var childCreateIndex = System.Array.FindIndex(ops, op => op is CreateObject co && co.LogicalId == "child-1");
            var lastCreateIndex = ops
                .Select((op, i) => (op, i))
                .Where(t => t.op is CreateObject)
                .Select(t => t.i)
                .DefaultIfEmpty(-1)
                .Max();
            var firstSetParentIndex = System.Array.FindIndex(ops, op => op is SetParent);

            Assert.True(rootCreateIndex >= 0, "root CreateObject op missing");
            Assert.True(childCreateIndex >= 0, "child CreateObject op missing");
            Assert.True(rootCreateIndex < childCreateIndex, "parent must be created before child");
            Assert.True(firstSetParentIndex >= 0, "expected a SetParent op for the child");
            Assert.True(firstSetParentIndex > lastCreateIndex, "SetParent must come after every CreateObject");

            var childSetParent = Assert.Single(ops.OfType<SetParent>(), op => op.LogicalId == "child-1");
            Assert.Equal("root-1", childSetParent.ParentLogicalId);
        }

        [Fact]
        public void Materialize_LowersTransform_ToConstrainedSetFieldPaths()
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Transform = transform };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var driftedTransform = transform with { Position = new Vec3(9, 9, 9) };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = driftedTransform };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);

            var setFields = plan.Ops.OfType<SetField>().Where(op => op.LogicalId == "root-1").ToArray();

            Assert.Equal(3, setFields.Length);
            Assert.Equal(new[] { "m_LocalPosition", "m_LocalRotation", "m_LocalScale" }, setFields.Select(f => f.Path));

            var position = Assert.IsType<ValueNode.Vec3>(setFields[0].Value);
            Assert.Equal(new Vec3(1, 2, 3), position.Value);

            Assert.IsType<ValueNode.Quat>(setFields[1].Value);
            Assert.IsType<ValueNode.Vec3>(setFields[2].Value);

            Assert.DoesNotContain(plan.Ops.OfType<SetField>(), op => op.Path != "m_LocalPosition" && op.Path != "m_LocalRotation" && op.Path != "m_LocalScale");
        }

        [Fact]
        public void Materialize_UnmappedActual_DirectCall_EmitsNoDestroy_GuardIsNotOptIn()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = System.Array.Empty<GameObjectNode>() };

            var userCreated = new SnapshotNode { GlobalObjectId = "goid-user-created", Name = "UserCreated" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { userCreated } };

            var map = new IdentityMap { Scene = "Assets/Scenes/Demo.unity" };

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.Empty(plan.Ops.OfType<DestroyObject>());
        }

        [Fact]
        public void Materialize_UnmappedUserComponent_IsNeverRemoved()
        {
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode
            {
                GlobalObjectId = "goid-root",
                Name = "Root",
                Components = new[] { new ComponentData() },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.Empty(plan.Ops.OfType<DestroyObject>());
            Assert.DoesNotContain(plan.Ops, op => op.GetType().Name == "RemoveComponent");
            Assert.Empty(plan.Ops);
        }

        [Fact]
        public void Materialize_AddedCodeObject_AppendsCreateOnly_WithoutRecreatingSiblings()
        {
            var existingRoot = new GameObjectNode { LogicalId = "root-1", Name = "Root" };
            var newRoot = new GameObjectNode { LogicalId = "new-1", Name = "New" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { existingRoot, newRoot } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);

            var create = Assert.Single(plan.Ops.OfType<CreateObject>());
            Assert.Equal("new-1", create.LogicalId);
            Assert.DoesNotContain(plan.Ops, op => op.LogicalId == "root-1");
        }

        [Fact]
        public void Materialize_CodeEqualsScene_ProducesEmptyPlan()
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var child = new GameObjectNode { LogicalId = "child-1", Name = "Child", Transform = transform };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Transform = transform, Children = new[] { child } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotChild = new SnapshotNode { GlobalObjectId = "goid-child", Name = "Child", Transform = transform };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = transform, Children = new[] { snapshotChild } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "child-1", GlobalObjectId = "goid-child", Kind = "GameObject" },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.Empty(plan.Ops);
        }

        // M3 b4-t2: lower component ChangeOps (b4-t1 Differ) into Plan ops.

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
        public void Materialize_ComponentAdd_EmitsAddComponentThenSetFields()
        {
            var rigidbody = new ComponentData
            {
                LogicalId = "rb-1",
                Type = new TypeRef("UnityEngine.Rigidbody"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(2)),
                    new KeyValuePair<string, ValueNode>("m_Drag", ValueNode.Primitive.Float(0.5f)),
                }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { rigidbody } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var plan = Materializer.Materialize(model, snapshot, MatchedGameObjectMap());

            var ops = plan.Ops;
            var addIndex = System.Array.FindIndex(ops, op => op is AddComponent ac && ac.LogicalId == "rb-1");
            Assert.True(addIndex >= 0, "expected an AddComponent op for rb-1");

            var addOp = Assert.IsType<AddComponent>(ops[addIndex]);
            Assert.Equal("UnityEngine.Rigidbody", addOp.Type.FullName);

            var followingSetFields = ops.Skip(addIndex + 1)
                .TakeWhile(op => op is SetField sf && sf.LogicalId == "rb-1")
                .Cast<SetField>()
                .ToArray();

            Assert.Equal(2, followingSetFields.Length);
            Assert.Equal(new[] { "m_Mass", "m_Drag" }, followingSetFields.Select(f => f.Path));
        }

        [Fact]
        public void Materialize_ComponentRemove_EmitsRemoveComponent_TransformExcluded()
        {
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode
            {
                GlobalObjectId = "goid-root",
                Name = "Root",
                Components = new[]
                {
                    new ComponentData { Type = new TypeRef("UnityEngine.Transform") },
                    new ComponentData { Type = new TypeRef("UnityEngine.BoxCollider") },
                },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = "bc-1",
                        GlobalObjectId = "goid-bc-1",
                        Kind = "Component",
                        ComponentType = "UnityEngine.BoxCollider",
                        ParentLogicalId = "root-1",
                    },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);

            var removes = plan.Ops.OfType<RemoveComponent>().ToArray();
            var remove = Assert.Single(removes);
            Assert.Equal("bc-1", remove.LogicalId);
            Assert.DoesNotContain(plan.Ops, op => op is RemoveComponent rc && rc.LogicalId != "bc-1");
        }

        [Fact]
        public void Materialize_FieldChangeAcrossKinds_EmitsSingleSetField()
        {
            ComponentData Component(string logicalId, FieldMap fields) => new()
            {
                LogicalId = logicalId,
                Type = new TypeRef("Game.Health"),
                Fields = fields,
            };

            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("primitive", ValueNode.Primitive.Int(5)),
                new KeyValuePair<string, ValueNode>("faction", new ValueNode.Enum("Game.Faction", new[] { "Enemy" }, false)),
                new KeyValuePair<string, ValueNode>("velocity", new ValueNode.Vec3(new Vec3(1, 2, 3))),
                new KeyValuePair<string, ValueNode>("tint", new ValueNode.Color(new Color(1, 0, 0, 1))),
                new KeyValuePair<string, ValueNode>("nested", new ValueNode.Nested("Game.Impact", new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("inner", ValueNode.Primitive.Int(1)),
                }))),
                new KeyValuePair<string, ValueNode>("items", new ValueNode.List(new ValueNode[] { ValueNode.Primitive.Int(1), ValueNode.Primitive.Int(2) })),
            });

            var actualFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("primitive", ValueNode.Primitive.Int(1)),
                new KeyValuePair<string, ValueNode>("faction", new ValueNode.Enum("Game.Faction", new[] { "Neutral" }, false)),
                new KeyValuePair<string, ValueNode>("velocity", new ValueNode.Vec3(new Vec3(0, 0, 0))),
                new KeyValuePair<string, ValueNode>("tint", new ValueNode.Color(new Color(0, 0, 0, 1))),
                new KeyValuePair<string, ValueNode>("nested", new ValueNode.Nested("Game.Impact", new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("inner", ValueNode.Primitive.Int(0)),
                }))),
                new KeyValuePair<string, ValueNode>("items", new ValueNode.List(new ValueNode[] { ValueNode.Primitive.Int(9) })),
            });

            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { Component("health-1", desiredFields) } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode
            {
                GlobalObjectId = "goid-root",
                Name = "Root",
                Components = new[] { Component("health-1", actualFields) },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var plan = Materializer.Materialize(model, snapshot, MatchedGameObjectMap());

            var setFields = plan.Ops.OfType<SetField>().Where(op => op.LogicalId == "health-1").ToArray();
            Assert.Equal(6, setFields.Length);
            Assert.Equal(new[] { "primitive", "faction", "velocity", "tint", "nested", "items" }, setFields.Select(f => f.Path));
            Assert.Equal(desiredFields["primitive"], Assert.Single(setFields, f => f.Path == "primitive").Value);
            Assert.Equal(desiredFields["items"], Assert.Single(setFields, f => f.Path == "items").Value);
        }

        [Fact]
        public void Materialize_ComponentOrder_RespectsDesiredOrder_TransformExcluded()
        {
            var rigidbody = new ComponentData { LogicalId = "rb-1", Type = new TypeRef("UnityEngine.Rigidbody") };
            var boxCollider = new ComponentData { LogicalId = "bc-1", Type = new TypeRef("UnityEngine.BoxCollider") };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { rigidbody, boxCollider } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var plan = Materializer.Materialize(model, snapshot, MatchedGameObjectMap());

            var addComponentLogicalIdsInOrder = plan.Ops.OfType<AddComponent>().Select(op => op.LogicalId).ToArray();
            Assert.Equal(new[] { "rb-1", "bc-1" }, addComponentLogicalIdsInOrder);
            Assert.DoesNotContain(plan.Ops.OfType<AddComponent>(), op => op.Type.FullName == "UnityEngine.Transform");
        }

        [Fact]
        public void Materialize_ComponentOrderDiffers_EmitsReorder()
        {
            var rigidbody = new ComponentData { LogicalId = "rb-1", Type = new TypeRef("UnityEngine.Rigidbody") };
            var boxCollider = new ComponentData { LogicalId = "bc-1", Type = new TypeRef("UnityEngine.BoxCollider") };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { rigidbody, boxCollider } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode
            {
                GlobalObjectId = "goid-root",
                Name = "Root",
                Components = new[]
                {
                    new ComponentData { Type = new TypeRef("UnityEngine.Transform") },
                    new ComponentData { Type = new TypeRef("UnityEngine.BoxCollider") },
                    new ComponentData { Type = new TypeRef("UnityEngine.Rigidbody") },
                },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var plan = Materializer.Materialize(model, snapshot, MatchedGameObjectMap());

            Assert.Empty(plan.Ops.OfType<AddComponent>());
            Assert.Empty(plan.Ops.OfType<RemoveComponent>());

            var reorders = plan.Ops.OfType<ReorderComponent>().ToArray();
            Assert.Equal(2, reorders.Length);

            var rbReorder = Assert.Single(reorders, r => r.ComponentLogicalId == "rb-1");
            Assert.Equal("root-1", rbReorder.GameObjectLogicalId);
            Assert.Equal(0, rbReorder.ToIndex);
            Assert.Equal("rb-1", rbReorder.LogicalId);

            var bcReorder = Assert.Single(reorders, r => r.ComponentLogicalId == "bc-1");
            Assert.Equal(1, bcReorder.ToIndex);
        }

        [Fact]
        public void Materialize_UnsupportedField_EmitsNoSetField_AndIsFlagged()
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("weirdField", new ValueNode.Unsupported("SomeUnparseableExpr()")),
            });
            var actualFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("weirdField", ValueNode.Primitive.Int(0)),
            });

            var component = new ComponentData { LogicalId = "comp-1", Type = new TypeRef("Game.Weird"), Fields = desiredFields };
            var actualComponent = new ComponentData { LogicalId = "comp-1", Type = new TypeRef("Game.Weird"), Fields = actualFields };

            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { component } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualComponent } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var plan = Materializer.Materialize(model, snapshot, MatchedGameObjectMap());

            Assert.DoesNotContain(plan.Ops, op => op is SetField sf && sf.Path == "weirdField");

            var skipped = Assert.Single(plan.Skipped);
            Assert.Equal("comp-1", skipped.LogicalId);
            Assert.Equal("weirdField", skipped.Path);
        }

        [Fact]
        public void Materialize_NewRootWithComponent_EmitsAddComponentAndSetField()
        {
            var component = new ComponentData
            {
                LogicalId = "root-1/UnityEngine.Rigidbody#0",
                Type = new TypeRef("UnityEngine.Rigidbody"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)),
                }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { component } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };
            var map = new IdentityMap { Scene = "Assets/Scenes/Demo.unity" };

            var plan = Materializer.Materialize(model, snapshot, map);

            var add = Assert.Single(plan.Ops.OfType<AddComponent>());
            Assert.Equal("root-1/UnityEngine.Rigidbody#0", add.LogicalId);
            Assert.Equal("UnityEngine.Rigidbody", add.Type.FullName);

            var setField = Assert.Single(plan.Ops.OfType<SetField>(), op => op.LogicalId == "root-1/UnityEngine.Rigidbody#0");
            Assert.Equal("m_Mass", setField.Path);
            Assert.Equal(ValueNode.Primitive.Float(5f), setField.Value);
        }

        [Fact]
        public void Materialize_NewChildOfNewParentWithComponent_EmitsAddComponentAndSetField()
        {
            var component = new ComponentData
            {
                LogicalId = "child-1/UnityEngine.Rigidbody#0",
                Type = new TypeRef("UnityEngine.Rigidbody"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(7f)),
                }),
            };
            var child = new GameObjectNode { LogicalId = "child-1", Name = "Child", Components = new[] { component } };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Children = new[] { child } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };
            var map = new IdentityMap { Scene = "Assets/Scenes/Demo.unity" };

            var plan = Materializer.Materialize(model, snapshot, map);

            var add = Assert.Single(plan.Ops.OfType<AddComponent>());
            Assert.Equal("child-1/UnityEngine.Rigidbody#0", add.LogicalId);
            Assert.Equal("UnityEngine.Rigidbody", add.Type.FullName);

            var setField = Assert.Single(plan.Ops.OfType<SetField>(), op => op.LogicalId == "child-1/UnityEngine.Rigidbody#0");
            Assert.Equal("m_Mass", setField.Path);
            Assert.Equal(ValueNode.Primitive.Float(7f), setField.Value);
        }

        // b3-t1: CREATE/materialize mask carrier — a SetTransform's DrivenChannels flows onto the
        // lowered m_LocalPosition/m_LocalScale SetField ops so b6-t1's write seam can skip driven
        // channels on create, where there is no snapshot to compare against.
        [Fact]
        public void Materialize_CreateSizerSnapperNode_LoweredTransformOpsCarryDrivenMask()
        {
            var emptySnapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };
            var map = new IdentityMap { Scene = "Assets/Scenes/Demo.unity" };

            var sizerTransform = new TransformData { Scale = new Vec3(2, 2, 2), DrivenChannels = ChannelMask.Scale };
            var sizerRoot = new GameObjectNode { LogicalId = "sizer-1", Name = "Crate", Transform = sizerTransform };
            var sizerModel = new SceneModel { SchemaVersion = 1, Roots = new[] { sizerRoot } };

            var sizerPlan = Materializer.Materialize(sizerModel, emptySnapshot, map);

            var sizerScaleField = Assert.Single(sizerPlan.Ops.OfType<SetField>(), op => op.LogicalId == "sizer-1" && op.Path == "m_LocalScale");
            Assert.Equal(ChannelMask.Scale, sizerScaleField.DrivenChannels & ChannelMask.Scale);
            var sizerPositionField = Assert.Single(sizerPlan.Ops.OfType<SetField>(), op => op.LogicalId == "sizer-1" && op.Path == "m_LocalPosition");
            Assert.Equal(ChannelMask.None, sizerPositionField.DrivenChannels);

            var snapperTransform = new TransformData { DrivenChannels = ChannelMask.PositionY };
            var snapperRoot = new GameObjectNode { LogicalId = "snapper-1", Name = "Crate", Transform = snapperTransform };
            var snapperModel = new SceneModel { SchemaVersion = 1, Roots = new[] { snapperRoot } };

            var snapperPlan = Materializer.Materialize(snapperModel, emptySnapshot, map);

            var snapperPositionField = Assert.Single(snapperPlan.Ops.OfType<SetField>(), op => op.LogicalId == "snapper-1" && op.Path == "m_LocalPosition");
            Assert.Equal(ChannelMask.PositionY, snapperPositionField.DrivenChannels);
            var snapperScaleField = Assert.Single(snapperPlan.Ops.OfType<SetField>(), op => op.LogicalId == "snapper-1" && op.Path == "m_LocalScale");
            Assert.Equal(ChannelMask.None, snapperScaleField.DrivenChannels);
        }
    }
}
