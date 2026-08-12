using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Spec 09 §13 create-with-payload for a UnityEvent listener: a listener carried by a
    // newly-created object/component must reach the pending set rather than a permanent
    // Dangling/Unsyncable conflict, and converges on the guaranteed second Sync (never a silent
    // drop). Two distinct scenarios: the OWNER of the listener is new (Reconcile_UnityEventOnNewObject_
    // Converges) and the listener's TARGET is a same-batch new component
    // (Reconcile_ListenerTargetIsSameBatchNewComponent_DefersNotDangling).
    //
    // Boundary: a component reached only through a prefab-instance's added-components/added-
    // GameObjects channel carries no snapshot component GlobalObjectId (stamped only at the
    // general read site). Such a component's identity is never available to compare against, so a
    // listener that can only be identified through that channel stays a permanent Unsyncable
    // report rather than converging — the non-empty-GlobalObjectId gate on the pending-target
    // union is what keeps that channel out of scope, and produces the identical Dangling/
    // Unsyncable outcome whether or not that gate exists, so it is not independently asserted here.
    public class UnityEventReconcileNewObjectTests
    {
        private const string DoorOpenerType = "Game.DoorOpener";
        private const string ButtonType = "UnityEngine.UI.Button";
        private const string OnClickFieldKey = "m_OnClick";

        private static UnityEventListener VoidListener(string targetId, string method) =>
            new UnityEventListener(new ValueNode.ObjectRef(targetId), method, ListenerArgMode.Void);

        // ---- Scenario 1: the LISTENER'S OWNER is a new object created in the same edit. The
        // target (an existing, already-mapped DoorOpener) is resolvable throughout. ----

        [Fact]
        public void Reconcile_UnityEventOnNewObject_Converges()
        {
            const string doorLogicalId = "root-1/" + DoorOpenerType + "#0";

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = doorLogicalId, GlobalObjectId = "", Kind = "Component",
                        ComponentType = DoorOpenerType, ParentLogicalId = "root-1",
                    },
                },
            };

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "root-1",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = doorLogicalId, Type = new TypeRef(DoorOpenerType), Fields = FieldMap.Empty },
                        },
                    },
                },
            };

            var buttonOnClick = new ValueNode.UnityEventListeners(new[] { VoidListener(doorLogicalId, "Open") });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused", Type = new TypeRef(DoorOpenerType), Fields = FieldMap.Empty },
                        },
                    },
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-button",
                        Name = "Button",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef(ButtonType),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>(OnClickFieldKey, buttonOnClick),
                                }),
                            },
                        },
                    },
                },
            };

            // Sync1: the Button is unmapped -> DetectAppends appends the GameObject and its
            // Button component. The listener field must not choke the append (no throw), and must
            // not be patched or reported yet (its target-side render needs the mapped-owner pass).
            var result1 = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result1.Patch.Edits.OfType<AppendStatement>());
            Assert.Equal("Button", append.Name);
            var attach = Assert.Single(result1.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.Equal(append.NewLogicalId, attach.Anchor);
            Assert.Equal(ButtonType, attach.TypeFullName);

            Assert.Empty(result1.Patch.Edits.OfType<AppendListenerCall>());
            Assert.Empty(result1.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Empty(result1.Patch.Edits.OfType<RemoveListenerCall>());
            Assert.Empty(result1.Notes.Where(c => c.Kind == ConflictKind.UnsyncableListener));

            var buttonComponentEntry = Assert.Single(result1.AddedEntries, e => e.Kind == "Component");
            var buttonGameObjectEntry = Assert.Single(result1.AddedEntries, e => e.Kind == "GameObject" && e.GlobalObjectId == "goid-button");

            // Sync2: the Button is now mapped, but its source carries no m_OnClick yet (Sync1
            // stripped the listener field from the append). The mapped-owner field-diff pass must
            // emit exactly one AppendListenerCall for the deferred listener.
            var map2 = new IdentityMap { Entries = map.Entries.Concat(result1.AddedEntries).ToArray() };

            var modelAfterSync1 = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "root-1",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = doorLogicalId, Type = new TypeRef(DoorOpenerType), Fields = FieldMap.Empty },
                        },
                    },
                    new GameObjectNode
                    {
                        LogicalId = buttonGameObjectEntry.LogicalId,
                        Name = "Button",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = buttonComponentEntry.LogicalId, Type = new TypeRef(ButtonType), Fields = FieldMap.Empty },
                        },
                    },
                },
            };

            var componentHandles = new Dictionary<string, string> { [doorLogicalId] = "door" };
            var emptySpans = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SourceSpan>>>();

            var result2 = Reconciler.Reconcile(
                modelAfterSync1, snapshot, map2, componentHandles: componentHandles, listenerCallSpans: emptySpans);

            Assert.Empty(result2.Patch.Edits.OfType<AppendStatement>());
            Assert.Empty(result2.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.Empty(result2.Notes.Where(c => c.Kind == ConflictKind.UnsyncableListener));

            var listenerAppend = Assert.Single(result2.Patch.Edits.OfType<AppendListenerCall>());
            Assert.Equal(buttonComponentEntry.LogicalId, listenerAppend.Anchor);
            Assert.Equal(OnClickFieldKey, listenerAppend.FieldKey);
            Assert.Contains("OnClick(door", listenerAppend.CallExpr);
            Assert.Contains("Open", listenerAppend.CallExpr);

            // Sync3: the listener is now present in source too (as Sync2's edit would have made
            // it) — a fixed point, no further edits or conflicts for this field.
            var modelAfterSync2 = modelAfterSync1 with
            {
                Roots = new[]
                {
                    modelAfterSync1.Roots[0],
                    modelAfterSync1.Roots[1] with
                    {
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = buttonComponentEntry.LogicalId,
                                Type = new TypeRef(ButtonType),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>(OnClickFieldKey, buttonOnClick),
                                }),
                            },
                        },
                    },
                },
            };

            var result3 = Reconciler.Reconcile(
                modelAfterSync2, snapshot, map2, componentHandles: componentHandles, listenerCallSpans: emptySpans);

            Assert.Empty(result3.Patch.Edits.OfType<AppendListenerCall>());
            Assert.Empty(result3.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Empty(result3.Patch.Edits.OfType<RemoveListenerCall>());
            Assert.Empty(result3.Notes.Where(c => c.Kind == ConflictKind.UnsyncableListener));
        }

        // ---- Scenario 2: the LISTENER'S TARGET is a same-batch new component (the owner —
        // Button — is already mapped). Without the pending-keyspace fix, the new component's own
        // GlobalObjectId is neither resolvable nor pending, so the target is classified Dangling
        // and the listener is reported Unsyncable even though it will resolve on the very next
        // Sync. ----

        [Fact]
        public void Reconcile_ListenerTargetIsSameBatchNewComponent_DefersNotDangling()
        {
            const string buttonLogicalId = "root-1/" + ButtonType + "#0";
            const string newDoorGoid = "goid-newdoor";

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = buttonLogicalId, GlobalObjectId = "", Kind = "Component",
                        ComponentType = ButtonType, ParentLogicalId = "root-1",
                    },
                },
            };

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "root-1",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = buttonLogicalId, Type = new TypeRef(ButtonType), Fields = FieldMap.Empty },
                        },
                    },
                },
            };

            // The Button's onClick targets a DoorOpener that exists in the scene but has no
            // IdentityMap entry yet — its own stamped GlobalObjectId is its only identity before
            // this pass maps it.
            var buttonOnClick = new ValueNode.UnityEventListeners(new[] { VoidListener(newDoorGoid, "Open") });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef(ButtonType),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>(OnClickFieldKey, buttonOnClick),
                                }),
                            },
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef(DoorOpenerType),
                                Fields = FieldMap.Empty,
                                GlobalObjectId = newDoorGoid,
                            },
                        },
                    },
                },
            };

            var componentHandles = new Dictionary<string, string>();
            var emptySpans = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SourceSpan>>>();

            var result = Reconciler.Reconcile(
                model, snapshot, map, componentHandles: componentHandles, listenerCallSpans: emptySpans);

            // The DoorOpener is still appended as a new component (unrelated to the listener path).
            Assert.Single(result.Patch.Edits.OfType<AppendComponentStatement>(), a => a.TypeFullName == DoorOpenerType);

            // The listener's target defers (Pending) rather than being permanently reported.
            Assert.Empty(result.Notes.Where(c => c.Kind == ConflictKind.UnsyncableListener));
            Assert.Empty(result.Patch.Edits.OfType<AppendListenerCall>());
            Assert.Empty(result.Patch.Edits.OfType<PatchListenerCall>());
        }
    }
}
