using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // The reconcile-side invariant that a listener's Target naming a component LogicalId is
    // Resolvable, not Dangling. ClassifyListenerTarget is the reconcile-side per-listener target
    // resolution; the Reconcile_* cases pin the same invariant through the top-level entry point
    // so a fix that only patches the helper (and not resolvableTargets itself) is caught.
    public class UnityEventReconcileResolutionTests
    {
        private const string DoorOpenerLogicalId = "root-1/UnityEventReconcileResolutionTests.DoorOpener#0";

        [Fact]
        public void ClassifyListenerTarget_ComponentTarget_ResolvesToObjectRefScene()
        {
            var resolvableTargets = new HashSet<string> { DoorOpenerLogicalId };
            var pendingTargets = new HashSet<string>();
            var target = new ValueNode.ObjectRef(DoorOpenerLogicalId);

            var result = ComponentReconciler.ClassifyListenerTarget(
                target, "Open", resolvableTargets, pendingTargets);

            Assert.Equal(ComponentReconciler.ListenerTargetKind.ResolvableScene, result.Kind);
            var resolved = Assert.IsType<ValueNode.ObjectRef>(result.Resolved);
            Assert.Equal(DoorOpenerLogicalId, resolved.TargetLogicalId);
        }

        [Fact]
        public void ClassifyListenerTarget_AssetTarget_ResolvesToAssetRef()
        {
            var assetRef = new global::SceneBuilder.Core.Model.AssetRef
            {
                Guid = "Guid-1",
                FileId = 0,
                DisplayPath = "Assets/Foo.asset",
                IsBuiltin = false,
                TypeHint = "UnityEngine.Object",
            };
            var target = new ValueNode.AssetRef(assetRef);

            var result = ComponentReconciler.ClassifyListenerTarget(
                target, "Open", new HashSet<string>(), new HashSet<string>());

            Assert.Equal(ComponentReconciler.ListenerTargetKind.ResolvableAsset, result.Kind);
            var resolved = Assert.IsType<ValueNode.AssetRef>(result.Resolved);
            Assert.Equal("Guid-1", resolved.Ref!.Guid);
        }

        [Fact]
        public void ClassifyListenerTarget_UnknownTarget_IsUnsyncableUnresolvedTarget()
        {
            var target = new ValueNode.ObjectRef("root-1/Unknown#0");

            var result = ComponentReconciler.ClassifyListenerTarget(
                target, "Open", new HashSet<string>(), new HashSet<string>());

            Assert.Equal(ComponentReconciler.ListenerTargetKind.Unsyncable, result.Kind);
            Assert.Equal(ListenerReportReason.UnresolvedTarget, result.Reason);
        }

        private static ComponentData DoorOpenerComponent() => new ComponentData
        {
            LogicalId = DoorOpenerLogicalId,
            Type = new TypeRef("UnityEventReconcileResolutionTests.DoorOpener"),
            Fields = FieldMap.Empty,
        };

        private static SceneModel ButtonModel(UnityEventListener sourceListener, bool includeDoorOpener = false) => new SceneModel
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new GameObjectNode
                {
                    LogicalId = "root-1",
                    Name = "Root",
                    Components = (includeDoorOpener
                        ? new[] { DoorOpenerComponent() }
                        : System.Array.Empty<ComponentData>())
                    .Append(new ComponentData
                    {
                        LogicalId = "root-1/UnityEngine.UI.Button#0",
                        Type = new TypeRef("UnityEngine.UI.Button"),
                        Fields = new FieldMap(new[]
                        {
                            new KeyValuePair<string, ValueNode>(
                                "m_OnClick", new ValueNode.UnityEventListeners(new[] { sourceListener })),
                        }),
                    })
                    .ToArray(),
                },
            },
        };

        private static SceneSnapshot ButtonSnapshot(UnityEventListener snapshotListener, bool includeDoorOpener = false) => new SceneSnapshot
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new SnapshotNode
                {
                    GlobalObjectId = "goid-root",
                    Name = "Root",
                    Components = (includeDoorOpener
                        ? new[] { DoorOpenerComponent() }
                        : System.Array.Empty<ComponentData>())
                    .Append(new ComponentData
                    {
                        LogicalId = "root-1/UnityEngine.UI.Button#0",
                        Type = new TypeRef("UnityEngine.UI.Button"),
                        Fields = new FieldMap(new[]
                        {
                            new KeyValuePair<string, ValueNode>(
                                "m_OnClick", new ValueNode.UnityEventListeners(new[] { snapshotListener })),
                        }),
                    })
                    .ToArray(),
                },
            },
        };

        private const string ButtonLogicalId = "root-1/UnityEngine.UI.Button#0";

        private static IdentityMapEntry ButtonEntry() => new IdentityMapEntry
        {
            LogicalId = ButtonLogicalId,
            GlobalObjectId = "",
            Kind = "Component",
            ComponentType = "UnityEngine.UI.Button",
            ParentLogicalId = "root-1",
        };

        private static IdentityMap MapWithDoorOpener() => new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                ButtonEntry(),
                new IdentityMapEntry
                {
                    LogicalId = DoorOpenerLogicalId,
                    GlobalObjectId = "",
                    Kind = "Component",
                    ComponentType = "UnityEventReconcileResolutionTests.DoorOpener",
                    ParentLogicalId = "root-1",
                },
            },
        };

        [Fact]
        public void Reconcile_ComponentTargetedListener_IsResolvable_NoNote_NoPatch()
        {
            var sourceListener = new UnityEventListener(
                new ValueNode.ObjectRef(DoorOpenerLogicalId), "Close", ListenerArgMode.Void);
            var snapshotListener = new UnityEventListener(
                new ValueNode.ObjectRef(DoorOpenerLogicalId), "Open", ListenerArgMode.Void);

            var result = Reconciler.Reconcile(
                ButtonModel(sourceListener, includeDoorOpener: true),
                ButtonSnapshot(snapshotListener, includeDoorOpener: true),
                MapWithDoorOpener());

            Assert.Empty(result.Notes);
            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Patch.Edits);
        }

        [Fact]
        public void Reconcile_UnresolvableListenerTarget_SurfacesExactlyOneUnsyncableListenerNote_NoPatch()
        {
            var sourceListener = new UnityEventListener(
                new ValueNode.ObjectRef("root-1/Unknown#0"), "Close", ListenerArgMode.Void);
            var snapshotListener = new UnityEventListener(
                new ValueNode.ObjectRef("root-1/Unknown#0"), "Open", ListenerArgMode.Void);

            // No IdentityMap entry and no source-model component for "root-1/Unknown#0" —
            // resolvable in neither direction.
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    ButtonEntry(),
                },
            };

            var result = Reconciler.Reconcile(
                ButtonModel(sourceListener), ButtonSnapshot(snapshotListener), map);

            Assert.Empty(result.Conflicts);
            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnsyncableListener, note.Kind);
            Assert.NotNull(note.RecurrenceKey);
            Assert.Empty(result.Patch.Edits);
        }

        [Fact]
        public void Reconcile_EqualListenerField_IsIdempotent_ZeroEdits()
        {
            var listener = new UnityEventListener(
                new ValueNode.ObjectRef(DoorOpenerLogicalId), "Open", ListenerArgMode.Void);

            var result = Reconciler.Reconcile(
                ButtonModel(listener, includeDoorOpener: true),
                ButtonSnapshot(listener, includeDoorOpener: true),
                MapWithDoorOpener());

            Assert.Empty(result.Notes);
            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Patch.Edits);
        }
    }
}
