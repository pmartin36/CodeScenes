using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class DifferTests
    {
        [Fact]
        public void Diff_ModelVsEmptySnapshot_ProducesOnlyCreates()
        {
            var child = new GameObjectNode { LogicalId = "child-1", Name = "Child" };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Children = new[] { child } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };
            var map = new IdentityMap();

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Empty(changeSet.Ops.OfType<RemoveNode>());
            var adds = changeSet.Ops.OfType<AddNode>().ToArray();
            Assert.Equal(2, adds.Length);
            var rootAdd = Assert.Single(adds, a => a.LogicalId == "root-1");
            Assert.Null(rootAdd.ParentLogicalId);
            var childAdd = Assert.Single(adds, a => a.LogicalId == "child-1");
            Assert.Equal("root-1", childAdd.ParentLogicalId);
        }

        [Fact]
        public void Diff_ModelVsEqualSnapshot_ProducesEmptyChangeSet()
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
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "child-1", GlobalObjectId = "goid-child", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Empty(changeSet.Ops);
        }

        [Fact]
        public void Diff_ModelVsDriftedSnapshot_ProducesInPlaceEdits_NeverRemoveThenAdd()
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var child = new GameObjectNode { LogicalId = "child-1", Name = "Child", Transform = transform };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Transform = transform, Children = new[] { child } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var driftedRootTransform = transform with { Position = new Vec3(9, 9, 9) };
            var snapshotChild = new SnapshotNode { GlobalObjectId = "goid-child", Name = "OldChildName", Transform = transform };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = driftedRootTransform, Children = new[] { snapshotChild } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "child-1", GlobalObjectId = "goid-child", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Contains(changeSet.Ops.OfType<SetTransform>(), op => op.LogicalId == "root-1");
            Assert.Contains(changeSet.Ops.OfType<SetName>(), op => op.LogicalId == "child-1");
            Assert.Empty(changeSet.Ops.OfType<AddNode>());
            Assert.Empty(changeSet.Ops.OfType<RemoveNode>());
        }

        [Fact]
        public void Diff_UnmappedActualAbsentFromDesired_IsNeverInDestroySet()
        {
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root" };
            var userCreated = new SnapshotNode { GlobalObjectId = "goid-user-created", Name = "UserCreated" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot, userCreated } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Empty(changeSet.Ops.OfType<RemoveNode>());
        }

        [Fact]
        public void Diff_MappedActualRemovedFromCode_ProducesDestroy()
        {
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root" };
            var snapshotRemoved = new SnapshotNode { GlobalObjectId = "goid-removed", Name = "Removed" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot, snapshotRemoved } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "removed-1", GlobalObjectId = "goid-removed", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            var removed = Assert.Single(changeSet.Ops.OfType<RemoveNode>());
            Assert.Equal("removed-1", removed.LogicalId);
            Assert.DoesNotContain(changeSet.Ops.OfType<RemoveNode>(), op => op.LogicalId == "root-1");
        }

        [Fact]
        public void Diff_DriftedMappedActual_IsUpdatedInPlace_PreservingGlobalObjectId_NeverRemoveThenAdd()
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Transform = transform };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var driftedTransform = transform with { Position = new Vec3(9, 9, 9) };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = driftedTransform };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Contains(changeSet.Ops.OfType<SetTransform>(), op => op.LogicalId == "root-1");
            Assert.Empty(changeSet.Ops.OfType<AddNode>());
            Assert.Empty(changeSet.Ops.OfType<RemoveNode>());
            Assert.Equal("goid-root", Assert.Single(map.Entries, e => e.LogicalId == "root-1").GlobalObjectId);
        }

        [Fact]
        public void Diff_ComponentInDesiredOnly_EmitsAddComponent()
        {
            var desiredComponent = new ComponentData
            {
                LogicalId = "root-1/UnityEngine.Rigidbody#0",
                Type = new TypeRef("UnityEngine.Rigidbody"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(12f)),
                }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            var add = Assert.Single(changeSet.Ops.OfType<AddComponent>());
            Assert.Equal("root-1", add.LogicalId);
            Assert.Equal(desiredComponent, add.Component);
            Assert.Empty(changeSet.Ops.OfType<SetField>());
        }

        [Fact]
        public void Diff_ComponentInActualOnly_ManagedRemoved_UnmanagedAndTransformNeverRemoved()
        {
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var transformComponent = new ComponentData { Type = new TypeRef("UnityEngine.Transform") };
            var managedRigidbody = new ComponentData { Type = new TypeRef("UnityEngine.Rigidbody") };
            var unmanagedBoxCollider = new ComponentData { Type = new TypeRef("UnityEngine.BoxCollider") };
            var snapshotRoot = new SnapshotNode
            {
                GlobalObjectId = "goid-root",
                Name = "Root",
                Components = new[] { transformComponent, managedRigidbody, unmanagedBoxCollider },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = "root-1/UnityEngine.Rigidbody#0",
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = "UnityEngine.Rigidbody",
                        ParentLogicalId = "root-1",
                    },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            var removed = Assert.Single(changeSet.Ops.OfType<RemoveComponent>());
            Assert.Equal("root-1", removed.LogicalId);
            Assert.Equal("root-1/UnityEngine.Rigidbody#0", removed.ComponentLogicalId);
            Assert.Equal("UnityEngine.Rigidbody", removed.ComponentType.FullName);
            Assert.DoesNotContain(changeSet.Ops.OfType<RemoveComponent>(), op => op.ComponentType.FullName == "UnityEngine.Transform");
            Assert.DoesNotContain(changeSet.Ops.OfType<RemoveComponent>(), op => op.ComponentType.FullName == "UnityEngine.BoxCollider");
        }

        [Fact]
        public void Diff_FieldValueDiffers_EmitsSingleSetFieldForChangedPathOnly()
        {
            var desiredComponent = new ComponentData
            {
                LogicalId = "root-1/UnityEngine.Rigidbody#0",
                Type = new TypeRef("UnityEngine.Rigidbody"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(12f)),
                    new KeyValuePair<string, ValueNode>("m_Drag", ValueNode.Primitive.Float(0f)),
                }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var actualComponent = new ComponentData
            {
                Type = new TypeRef("UnityEngine.Rigidbody"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(8f)),
                    new KeyValuePair<string, ValueNode>("m_Drag", ValueNode.Primitive.Float(0f)),
                }),
            };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualComponent } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            var setField = Assert.Single(changeSet.Ops.OfType<SetField>());
            Assert.Equal("root-1", setField.LogicalId);
            Assert.Equal("root-1/UnityEngine.Rigidbody#0", setField.ComponentLogicalId);
            Assert.Equal("m_Mass", setField.Path);
            Assert.Equal(ValueNode.Primitive.Float(12f), setField.Value);
            Assert.Empty(changeSet.Ops.OfType<AddComponent>());
            Assert.Empty(changeSet.Ops.OfType<RemoveComponent>());
        }

        [Fact]
        public void Diff_FieldExactEquality_OneUlpFloatDiffers_EmitsSetField()
        {
            var baseline = 1.0f;
            var oneUlpMore = System.MathF.BitIncrement(baseline);

            var desiredComponent = new ComponentData
            {
                LogicalId = "root-1/UnityEngine.Rigidbody#0",
                Type = new TypeRef("UnityEngine.Rigidbody"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(baseline)),
                }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var actualComponent = new ComponentData
            {
                Type = new TypeRef("UnityEngine.Rigidbody"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(oneUlpMore)),
                }),
            };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualComponent } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            var setField = Assert.Single(changeSet.Ops.OfType<SetField>());
            Assert.Equal("m_Mass", setField.Path);
            Assert.Equal(ValueNode.Primitive.Float(baseline), setField.Value);
        }

        [Fact]
        public void Diff_SameComponentSetDifferentOrder_EmitsReorder_NoAddRemove()
        {
            var desiredRigidbody = new ComponentData { LogicalId = "root-1/UnityEngine.Rigidbody#0", Type = new TypeRef("UnityEngine.Rigidbody") };
            var desiredBoxCollider = new ComponentData { LogicalId = "root-1/UnityEngine.BoxCollider#0", Type = new TypeRef("UnityEngine.BoxCollider") };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredRigidbody, desiredBoxCollider } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var transformComponent = new ComponentData { Type = new TypeRef("UnityEngine.Transform") };
            var actualBoxCollider = new ComponentData { Type = new TypeRef("UnityEngine.BoxCollider") };
            var actualRigidbody = new ComponentData { Type = new TypeRef("UnityEngine.Rigidbody") };
            var snapshotRoot = new SnapshotNode
            {
                GlobalObjectId = "goid-root",
                Name = "Root",
                Components = new[] { transformComponent, actualBoxCollider, actualRigidbody },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Empty(changeSet.Ops.OfType<AddComponent>());
            Assert.Empty(changeSet.Ops.OfType<RemoveComponent>());
            var reorders = changeSet.Ops.OfType<ReorderComponent>().ToArray();
            Assert.Contains(reorders, r => r.ComponentLogicalId == "root-1/UnityEngine.Rigidbody#0" && r.ToIndex == 0);
            Assert.Contains(reorders, r => r.ComponentLogicalId == "root-1/UnityEngine.BoxCollider#0" && r.ToIndex == 1);
        }
    }
}
