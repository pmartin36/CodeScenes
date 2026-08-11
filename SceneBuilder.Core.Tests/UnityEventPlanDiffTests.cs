using System.Linq;
using System.Text.Json;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using SceneBuilder.Core.Serialization;
using Xunit;
using Change = SceneBuilder.Core.Diff;

namespace SceneBuilder.Core.Tests
{
    // The §5 SetUnityEvent Plan op: one op per changed UnityEvent field, carried end to end
    // through Diff and Materialize. Diff/Materialize "changed" is entirely
    // ValueNode.UnityEventListeners.Equals (order-significant SequenceEqual over
    // Target/MethodName/ArgMode/ArgValue/CallState); this file pins the op-emission contract
    // built on top of that equality, not the equality itself (covered by UnityEventListenerModelTests).
    public class UnityEventPlanDiffTests
    {
        private static UnityEventListener Listener(
            ValueNode? target,
            string methodName,
            ListenerArgMode argMode = ListenerArgMode.Void,
            ValueNode? argValue = null,
            ListenerCallState callState = ListenerCallState.RuntimeOnly) =>
            new(target, methodName, argMode, argValue, callState);

        private const string ButtonType = "UnityEngine.UI.Button";
        private const string ComponentLogicalId = "root-1/UnityEngine.UI.Button#0";
        private const string OnClickPath = "m_OnClick";

        private static (GameObjectNode Root, SceneModel Model, SnapshotNode SnapshotRoot, IdentityMap Map) Wire(
            ValueNode.UnityEventListeners desiredListeners,
            ValueNode.UnityEventListeners actualListeners)
        {
            var desiredComponent = new ComponentData
            {
                LogicalId = ComponentLogicalId,
                Type = new TypeRef(ButtonType),
                Fields = new FieldMap(new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, ValueNode>(OnClickPath, desiredListeners),
                }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var actualComponent = new ComponentData
            {
                Type = new TypeRef(ButtonType),
                Fields = new FieldMap(new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, ValueNode>(OnClickPath, actualListeners),
                }),
            };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualComponent } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            return (root, model, snapshotRoot, map);
        }

        [Fact]
        public void Diff_ListenerDelta_EmitsExactlyOneSetUnityEvent()
        {
            var desired = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.ObjectRef("door-1"), "Open"),
            });
            var actual = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.ObjectRef("door-1"), "Close"),
            });
            var (_, model, snapshotRoot, map) = Wire(desired, actual);
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var changeSet = Differ.Diff(model, snapshot, map);

            var setUnityEvent = Assert.Single(changeSet.Ops.OfType<Change.SetUnityEvent>());
            Assert.Equal(ComponentLogicalId, setUnityEvent.ComponentLogicalId);
            Assert.Equal(OnClickPath, setUnityEvent.Path);
            Assert.Equal(desired, setUnityEvent.Listeners);
            Assert.Empty(changeSet.Ops.OfType<Change.SetField>());
        }

        [Fact]
        public void Diff_IdenticalLists_EmitZeroOps()
        {
            var sameListeners = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.ObjectRef("door-1"), "Open"),
            });
            var (_, model, snapshotRoot, map) = Wire(sameListeners, sameListeners);
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var changeSet = Differ.Diff(model, snapshot, map);
            Assert.Empty(changeSet.Ops.OfType<Change.SetUnityEvent>());

            var plan = Materializer.Materialize(model, snapshot, map);
            Assert.Empty(plan.Ops.OfType<Plan.SetUnityEvent>());
        }

        [Fact]
        public void Materialize_ListenerDelta_LowersToOneSetUnityEvent()
        {
            var desired = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.ObjectRef("door-1"), "Open"),
            });
            var actual = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.ObjectRef("door-1"), "Close"),
            });
            var (_, model, snapshotRoot, map) = Wire(desired, actual);
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var plan = Materializer.Materialize(model, snapshot, map);

            var op = Assert.Single(plan.Ops.OfType<Plan.SetUnityEvent>());
            Assert.Equal(ComponentLogicalId, op.LogicalId);
            Assert.Equal(OnClickPath, op.Path);
            Assert.Equal(desired, op.Listeners);
        }

        [Fact]
        public void Diff_TwoListeners_PreservesOrder()
        {
            var listenerA = Listener(new ValueNode.ObjectRef("door-1"), "Open");
            var listenerB = Listener(new ValueNode.ObjectRef("door-2"), "Close");
            var desired = new ValueNode.UnityEventListeners(new[] { listenerA, listenerB });
            var actual = new ValueNode.UnityEventListeners(new[] { listenerB, listenerA });
            var (_, model, snapshotRoot, map) = Wire(desired, actual);
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var changeSet = Differ.Diff(model, snapshot, map);

            var setUnityEvent = Assert.Single(changeSet.Ops.OfType<Change.SetUnityEvent>());
            Assert.Equal(new[] { listenerA, listenerB }, setUnityEvent.Listeners.Listeners);
        }

        [Fact]
        public void Diff_EmptyDesiredVsPresentActual_EmitsClearingOp()
        {
            var desired = new ValueNode.UnityEventListeners(System.Array.Empty<UnityEventListener>());
            var actual = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.ObjectRef("door-1"), "Open"),
            });
            var (_, model, snapshotRoot, map) = Wire(desired, actual);
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var changeSet = Differ.Diff(model, snapshot, map);

            var setUnityEvent = Assert.Single(changeSet.Ops.OfType<Change.SetUnityEvent>());
            Assert.Empty(setUnityEvent.Listeners.Listeners);
        }

        [Fact]
        public void SetUnityEvent_RoundTripsThroughPlanJson_Equal()
        {
            var listeners = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.ObjectRef("door-1"), "Open"),
                Listener(null, "SetValue", ListenerArgMode.Int, ValueNode.Primitive.Int(7), ListenerCallState.EditorAndRuntime),
            });
            var op = new Plan.SetUnityEvent { LogicalId = ComponentLogicalId, Path = OnClickPath, Listeners = listeners };
            var plan = new Plan.Plan { SchemaVersion = 1, ScenePath = "Assets/Scenes/Demo.unity", Ops = new PlanOp[] { op } };

            var json = PlanJson.Serialize(plan);
            var back = PlanJson.Deserialize(json);

            var roundTripped = Assert.Single(back.Ops);
            Assert.Equal(op, roundTripped);

            using var doc = JsonDocument.Parse(json);
            var opElement = doc.RootElement.GetProperty("ops")[0];
            Assert.Equal("SetUnityEvent", opElement.GetProperty("op").GetString());
        }
    }
}
