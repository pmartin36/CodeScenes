using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using Xunit;
using Change = SceneBuilder.Core.Diff;

namespace SceneBuilder.Core.Tests
{
    // Differ's ManagedReference contract: a concrete-type change OR any nested field change both
    // emit exactly ONE Change.SetManagedReference carrying the full new ConcreteType+Fields — never
    // a field-level SetField — and an equal desired/actual pair emits nothing (mirrors
    // UnityEventPlanDiffTests' Diff_ListenerDelta_EmitsExactlyOneSetUnityEvent).
    public class ManagedReferenceDiffTests
    {
        private const string ComponentType = "Game.AiBrain";
        private const string ComponentLogicalId = "root-1/Game.AiBrain#0";
        private const string StrategyPath = "strategy";

        private static ValueNode.ManagedReference Instance(string typeName, float value) =>
            new(new TypeRef(typeName, "Game.Runtime"),
                new FieldMap(new[] { new KeyValuePair<string, ValueNode>("range", ValueNode.Primitive.Float(value)) }));

        private static readonly ValueNode.ManagedReference NullRef = new(null, FieldMap.Empty);

        private static (SceneModel Model, SnapshotNode SnapshotRoot, IdentityMap Map) Wire(
            ValueNode desired, ValueNode actual)
        {
            var desiredComponent = new ComponentData
            {
                LogicalId = ComponentLogicalId,
                Type = new TypeRef(ComponentType),
                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(StrategyPath, desired) }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var actualComponent = new ComponentData
            {
                Type = new TypeRef(ComponentType),
                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(StrategyPath, actual) }),
            };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualComponent } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            return (model, snapshotRoot, map);
        }

        private static SceneSnapshot Snapshot(SnapshotNode root) =>
            new() { SchemaVersion = 1, Roots = new[] { root } };

        [Fact]
        public void Diff_ConcreteTypeChange_EmitsExactlyOneSetManagedReference_CarryingTheFullNewValue()
        {
            var desired = Instance("Game.Aggressive", 5f);
            var actual = Instance("Game.Flee", 3f);
            var (model, snapshotRoot, map) = Wire(desired, actual);

            var changeSet = Differ.Diff(model, Snapshot(snapshotRoot), map);

            var op = Assert.Single(changeSet.Ops.OfType<Change.SetManagedReference>());
            Assert.Equal(ComponentLogicalId, op.ComponentLogicalId);
            Assert.Equal(StrategyPath, op.Path);
            Assert.Equal(desired.ConcreteType, op.ConcreteType);
            Assert.Equal(desired.Fields, op.Fields);
        }

        [Fact]
        public void Diff_ConcreteTypeChange_EmitsNoSetField()
        {
            var desired = Instance("Game.Aggressive", 5f);
            var actual = Instance("Game.Flee", 3f);
            var (model, snapshotRoot, map) = Wire(desired, actual);

            var changeSet = Differ.Diff(model, Snapshot(snapshotRoot), map);

            Assert.Empty(changeSet.Ops.OfType<Change.SetField>());
        }

        [Fact]
        public void Diff_FieldOnlyChange_SameType_EmitsExactlyOneSetManagedReference()
        {
            var desired = Instance("Game.Aggressive", 9f);
            var actual = Instance("Game.Aggressive", 5f);
            var (model, snapshotRoot, map) = Wire(desired, actual);

            var changeSet = Differ.Diff(model, Snapshot(snapshotRoot), map);

            var op = Assert.Single(changeSet.Ops.OfType<Change.SetManagedReference>());
            Assert.Equal(desired.Fields, op.Fields);
        }

        [Fact]
        public void Diff_ActualNullToDesiredNonNull_EmitsExactlyOneSetManagedReference()
        {
            var desired = Instance("Game.Aggressive", 5f);
            var (model, snapshotRoot, map) = Wire(desired, NullRef);

            var changeSet = Differ.Diff(model, Snapshot(snapshotRoot), map);

            var op = Assert.Single(changeSet.Ops.OfType<Change.SetManagedReference>());
            Assert.Equal(desired.ConcreteType, op.ConcreteType);
        }

        [Fact]
        public void Diff_ActualNonNullToDesiredNull_EmitsExactlyOneSetManagedReferenceWithNullConcreteType()
        {
            var actual = Instance("Game.Aggressive", 5f);
            var (model, snapshotRoot, map) = Wire(NullRef, actual);

            var changeSet = Differ.Diff(model, Snapshot(snapshotRoot), map);

            var op = Assert.Single(changeSet.Ops.OfType<Change.SetManagedReference>());
            Assert.Null(op.ConcreteType);
        }

        [Fact]
        public void Diff_IdenticalDesiredAndActual_EmitsZeroManagedReferenceOps()
        {
            var same = Instance("Game.Aggressive", 5f);
            var (model, snapshotRoot, map) = Wire(same, same);

            var changeSet = Differ.Diff(model, Snapshot(snapshotRoot), map);

            Assert.Empty(changeSet.Ops.OfType<Change.SetManagedReference>());
        }
    }
}
