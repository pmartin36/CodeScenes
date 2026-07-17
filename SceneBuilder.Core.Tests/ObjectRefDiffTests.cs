using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // m5-cross-object-references b3-t1: Diff compares ObjectRef fields via default record
    // equality on TargetLogicalId (both-null == equal). No production change is expected —
    // this rides on Differ.cs's generic `actualValue != field.Value` comparison, exactly as
    // AssetRefDiffTests pins the same claim for AssetRef. If any of these fail, Diff DOES
    // need ObjectRef-specific awareness — surface it as a spec deviation, don't patch Differ.cs.
    public class ObjectRefDiffTests
    {
        private const string FieldKey = "target";

        private static (SceneModel Model, SceneSnapshot Snapshot, IdentityMap Map) BuildScene(ValueNode desiredValue, ValueNode actualValue)
        {
            var desiredComponent = new ComponentData
            {
                LogicalId = "root-1/Game.DoorOpener#0",
                Type = new TypeRef("Game.DoorOpener"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>(FieldKey, desiredValue),
                }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var actualComponent = new ComponentData
            {
                Type = new TypeRef("Game.DoorOpener"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>(FieldKey, actualValue),
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

            return (model, snapshot, map);
        }

        [Fact]
        public void Diff_ObjectRefSameTarget_NoChange()
        {
            var desired = new ValueNode.ObjectRef("lid-b");
            var actual = new ValueNode.ObjectRef("lid-b");
            var (model, snapshot, map) = BuildScene(desired, actual);

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Empty(changeSet.Ops.OfType<SetField>());
        }

        [Fact]
        public void Diff_ObjectRefDifferentTarget_ReportsChange()
        {
            var desired = new ValueNode.ObjectRef("lid-a");
            var actual = new ValueNode.ObjectRef("lid-b");
            var (model, snapshot, map) = BuildScene(desired, actual);

            var changeSet = Differ.Diff(model, snapshot, map);

            var setField = Assert.Single(changeSet.Ops.OfType<SetField>());
            Assert.Equal("root-1", setField.LogicalId);
            Assert.Equal(FieldKey, setField.Path);
            Assert.Equal(desired, setField.Value);
        }

        [Fact]
        public void Diff_NullVsHandle_ReportsChange_AndNullVsNull_NoChange()
        {
            var populated = new ValueNode.ObjectRef("lid-a");
            var none = new ValueNode.ObjectRef(null);

            // desired = None, actual = populated -> change (clears the field).
            var (clearedModel, clearedSnapshot, clearedMap) = BuildScene(none, populated);
            var clearedChangeSet = Differ.Diff(clearedModel, clearedSnapshot, clearedMap);
            var clearedSetField = Assert.Single(clearedChangeSet.Ops.OfType<SetField>());
            Assert.Equal(none, clearedSetField.Value);

            // desired = populated, actual = None -> change (assigns the field).
            var (assignedModel, assignedSnapshot, assignedMap) = BuildScene(populated, none);
            var assignedChangeSet = Differ.Diff(assignedModel, assignedSnapshot, assignedMap);
            var assignedSetField = Assert.Single(assignedChangeSet.Ops.OfType<SetField>());
            Assert.Equal(populated, assignedSetField.Value);

            // desired = None, actual = None -> no change.
            var (noneModel, noneSnapshot, noneMap) = BuildScene(none, none);
            var noneChangeSet = Differ.Diff(noneModel, noneSnapshot, noneMap);
            Assert.Empty(noneChangeSet.Ops.OfType<SetField>());
        }
    }
}
