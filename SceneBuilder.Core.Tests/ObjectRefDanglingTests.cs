using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t2: ConflictKind.DanglingReference detection — a source handle whose target vanished
    // from the scene, or a snapshot target that resolves to nothing, must report a located
    // conflict and suppress the would-be silent-null / phantom-handle patch. Rewire-vs-delete
    // (a live different target) and a legit clear-to-None (target still alive) must NOT report
    // DanglingReference. Mirrors ObjectRefReconcileTests' harness.
    public class ObjectRefDanglingTests
    {
        private const string ComponentTypeFullName = "Game.DoorOpener";
        private const string FieldKey = "target";
        private const string OwnerLogicalId = "root-1";
        private static string ComponentLogicalId => $"{OwnerLogicalId}/{ComponentTypeFullName}#0";

        private sealed class Fixture
        {
            public SceneModel Model = null!;
            public IdentityMap Map = null!;
            public SceneSnapshot Snapshot = null!;
        }

        // extraLiveTargets: (LogicalId, Goid) pairs mapped AND present in the snapshot roots —
        // genuinely alive in the scene.
        // extraDeadTargets: (LogicalId, Goid) pairs mapped in the IdentityMap but ABSENT from the
        // snapshot roots — deleted from the scene (but still authored in the source model, per
        // research.md: a delete does not remove the statement).
        private static Fixture BuildFixture(
            ValueNode sourceValue,
            ValueNode snapshotValue,
            GameObjectNode[] extraModelRoots,
            (string LogicalId, string Goid)[] extraLiveTargets,
            (string LogicalId, string Goid)[] extraDeadTargets)
        {
            var roots = new List<GameObjectNode>
            {
                new GameObjectNode
                {
                    LogicalId = OwnerLogicalId,
                    Name = "Root",
                    Components = new[]
                    {
                        new ComponentData
                        {
                            LogicalId = ComponentLogicalId,
                            Type = new TypeRef(ComponentTypeFullName),
                            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, sourceValue) }),
                        },
                    },
                },
            };
            roots.AddRange(extraModelRoots);

            var model = new SceneModel { SchemaVersion = 1, Roots = roots.ToArray() };

            var mapEntries = new List<IdentityMapEntry>
            {
                new IdentityMapEntry { LogicalId = OwnerLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" },
            };
            foreach (var (logicalId, goid) in extraLiveTargets)
            {
                mapEntries.Add(new IdentityMapEntry { LogicalId = logicalId, GlobalObjectId = goid, Kind = "GameObject" });
            }

            foreach (var (logicalId, goid) in extraDeadTargets)
            {
                mapEntries.Add(new IdentityMapEntry { LogicalId = logicalId, GlobalObjectId = goid, Kind = "GameObject" });
            }

            var map = new IdentityMap { Entries = mapEntries.ToArray() };

            var snapshotRoots = new List<SnapshotNode>
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
                            Type = new TypeRef(ComponentTypeFullName),
                            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, snapshotValue) }),
                        },
                    },
                },
            };
            foreach (var (_, goid) in extraLiveTargets)
            {
                snapshotRoots.Add(new SnapshotNode { GlobalObjectId = goid, Name = "Live" });
            }

            // extraDeadTargets are deliberately NOT added to the snapshot roots.

            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = snapshotRoots.ToArray() };

            return new Fixture { Model = model, Map = map, Snapshot = snapshot };
        }

        private static Dictionary<string, IReadOnlyDictionary<string, SourceSpan>> FieldSpans() =>
            new()
            {
                [ComponentLogicalId] = new Dictionary<string, SourceSpan> { [FieldKey] = new SourceSpan(0, 10) },
            };

        // Detection 1 / deliverable #8, checklist #4: source handle's target ("door-1") has been
        // deleted from the scene (still authored in source + `handles`, but its IdentityMap
        // entry's GlobalObjectId is absent from the snapshot) -> a located DanglingReference
        // conflict naming source-object/field/missing-target, NEVER a silent NodeHandle.None patch.
        [Fact]
        public void Dangling_SourceHandleTargetDeleted_ReportsLocatedConflict()
        {
            var doorRoot = new GameObjectNode { LogicalId = "door-1", Name = "Door" };
            var fixture = BuildFixture(
                sourceValue: new ValueNode.ObjectRef("door-1"),
                snapshotValue: new ValueNode.ObjectRef(null),
                extraModelRoots: new[] { doorRoot },
                extraLiveTargets: System.Array.Empty<(string, string)>(),
                extraDeadTargets: new[] { ("door-1", "goid-door") });

            var handles = new Dictionary<string, string> { ["door-1"] = "door" };

            var result = Reconciler.Reconcile(
                fixture.Model, fixture.Snapshot, fixture.Map, null, null, null, null, FieldSpans(), handles);

            var conflict = Assert.Single(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Equal(ComponentLogicalId, conflict.LogicalId);
            Assert.Contains("door-1", conflict.Reason);
            Assert.Contains(FieldKey, conflict.Reason);
            Assert.NotNull(conflict.Location);
            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
        }

        // Detection 2 / "GlobalObjectId with no IdentityMap entry": the snapshot points at a
        // target ("ghost-1") that is neither a live scene object, nor in the parsed source model,
        // nor a known handle -> a located DanglingReference conflict, no phantom-handle patch.
        [Fact]
        public void Dangling_SnapshotTargetUnresolvable_ReportsConflict()
        {
            var fixture = BuildFixture(
                sourceValue: new ValueNode.ObjectRef(null),
                snapshotValue: new ValueNode.ObjectRef("ghost-1"),
                extraModelRoots: System.Array.Empty<GameObjectNode>(),
                extraLiveTargets: System.Array.Empty<(string, string)>(),
                extraDeadTargets: System.Array.Empty<(string, string)>());

            var result = Reconciler.Reconcile(
                fixture.Model, fixture.Snapshot, fixture.Map, null, null, null, null, FieldSpans());

            var conflict = Assert.Single(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Contains("ghost-1", conflict.Reason);
            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
        }

        // Rewire-vs-delete distinction (spec §238-240): the snapshot target is a genuinely LIVE
        // different scene object -> a normal rewire patch, never a DanglingReference.
        [Fact]
        public void Rewire_ToLiveDifferentObject_NotDangling()
        {
            var doorRoot = new GameObjectNode { LogicalId = "door-1", Name = "Door" };
            var otherRoot = new GameObjectNode { LogicalId = "other-1", Name = "Other" };
            var fixture = BuildFixture(
                sourceValue: new ValueNode.ObjectRef("door-1"),
                snapshotValue: new ValueNode.ObjectRef("other-1"),
                extraModelRoots: new[] { doorRoot, otherRoot },
                extraLiveTargets: new[] { ("door-1", "goid-door"), ("other-1", "goid-other") },
                extraDeadTargets: System.Array.Empty<(string, string)>());

            var handles = new Dictionary<string, string> { ["door-1"] = "door", ["other-1"] = "other" };

            var result = Reconciler.Reconcile(
                fixture.Model, fixture.Snapshot, fixture.Map, null, null, null, null, FieldSpans(), handles);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("other", patch.NewExpr);
            Assert.DoesNotContain(result.Conflicts, c => c.Kind == ConflictKind.DanglingReference);
        }

        // Sibling of the corrected b4-t1 test: a target cleared to None while it is STILL a live
        // scene object is a legit clear, not a delete -> None patch, no conflict.
        [Fact]
        public void ClearedToNone_LiveTarget_IsLegitNone()
        {
            var doorRoot = new GameObjectNode { LogicalId = "door-1", Name = "Door" };
            var fixture = BuildFixture(
                sourceValue: new ValueNode.ObjectRef("door-1"),
                snapshotValue: new ValueNode.ObjectRef(null),
                extraModelRoots: new[] { doorRoot },
                extraLiveTargets: new[] { ("door-1", "goid-door") },
                extraDeadTargets: System.Array.Empty<(string, string)>());

            var handles = new Dictionary<string, string> { ["door-1"] = "door" };

            var result = Reconciler.Reconcile(
                fixture.Model, fixture.Snapshot, fixture.Map, null, null, null, null, FieldSpans(), handles);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("NodeHandle.None", patch.NewExpr);
            Assert.Empty(result.Conflicts);
        }
    }
}
