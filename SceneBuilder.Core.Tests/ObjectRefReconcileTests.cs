using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t1: matched-field (present in BOTH source and snapshot) ObjectRef rewire/null path ->
    // handle-aware PatchComponentField rendering. Mirrors AssetRefReconcileTests.cs harness.
    public class ObjectRefReconcileTests
    {
        private const string ComponentTypeFullName = "Game.DoorOpener";
        private const string FieldKey = "target";

        private static (SceneModel Model, IdentityMap Map, string ComponentLogicalId) MappedRootWithObjectRefField(
            ValueNode sourceValue, GameObjectNode[]? extraRoots = null)
        {
            const string ownerLogicalId = "root-1";
            var componentLogicalId = $"{ownerLogicalId}/{ComponentTypeFullName}#0";

            var roots = new List<GameObjectNode>
            {
                new GameObjectNode
                {
                    LogicalId = ownerLogicalId,
                    Name = "Root",
                    Components = new[]
                    {
                        new ComponentData
                        {
                            LogicalId = componentLogicalId,
                            Type = new TypeRef(ComponentTypeFullName),
                            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, sourceValue) }),
                        },
                    },
                },
            };

            if (extraRoots != null)
            {
                roots.AddRange(extraRoots);
            }

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = roots.ToArray(),
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            return (model, map, componentLogicalId);
        }

        private static SceneSnapshot SnapshotWithObjectRefField(ValueNode snapshotValue) => new SceneSnapshot
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
                            Type = new TypeRef(ComponentTypeFullName),
                            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, snapshotValue) }),
                        },
                    },
                },
            },
        };

        private static Dictionary<string, IReadOnlyDictionary<string, SourceSpan>> FieldSpans(string componentLogicalId) =>
            new()
            {
                [componentLogicalId] = new Dictionary<string, SourceSpan> { [FieldKey] = new SourceSpan(0, 10) },
            };

        // spec #6: snapshot ObjectRef targets an object that ALREADY has a handle in source ->
        // patch swaps the argument to that handle name; no new handle is introduced.
        [Fact]
        public void Reconcile_RewireToAlreadyHandledTarget_PatchesArgumentToHandleName()
        {
            var sourceValue = new ValueNode.ObjectRef("door-1");
            var (model, map, componentLogicalId) = MappedRootWithObjectRefField(sourceValue);

            var snapshotValue = new ValueNode.ObjectRef("other-1");
            var snapshot = SnapshotWithObjectRefField(snapshotValue);

            var handles = new Dictionary<string, string> { ["door-1"] = "door", ["other-1"] = "other" };

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId), handles);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal(componentLogicalId, patch.Anchor);
            Assert.Equal("other", patch.NewExpr);

            Assert.Empty(result.Patch.Edits.OfType<IntroduceHandle>());
        }

        // spec #7: target cleared to None in the scene -> patch rewrites the argument to
        // NodeHandle.None. door-1 must be a LIVE scene object here (mapped + present in the
        // snapshot) — otherwise this is a b4-t2 dangling delete, not a legit clear
        // (research.md b4-t2: "b4-t2 MUST update that ONE test to make door-1 a live scene
        // object; then it stays a legit None").
        [Fact]
        public void Reconcile_ClearedToNone_PatchesArgumentToNodeHandleNone()
        {
            var sourceValue = new ValueNode.ObjectRef("door-1");
            var doorRoot = new GameObjectNode { LogicalId = "door-1", Name = "Door" };
            var (model, map, componentLogicalId) = MappedRootWithObjectRefField(sourceValue, new[] { doorRoot });
            map = map with
            {
                Entries = map.Entries.Append(
                    new IdentityMapEntry { LogicalId = "door-1", GlobalObjectId = "goid-door", Kind = "GameObject" }).ToArray(),
            };

            var snapshotValue = new ValueNode.ObjectRef(null);
            var snapshot = SnapshotWithObjectRefField(snapshotValue);
            snapshot = snapshot with
            {
                Roots = snapshot.Roots.Append(
                    new SnapshotNode { GlobalObjectId = "goid-door", Name = "Door" }).ToArray(),
            };

            var handles = new Dictionary<string, string> { ["door-1"] = "door" };

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId), handles);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("NodeHandle.None", patch.NewExpr);
            Assert.Empty(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
        }

        // spec #7 reverse: source was None, scene now has a live target with an existing handle ->
        // patch rewrites the argument to that handle name.
        [Fact]
        public void Reconcile_NoneRewiredToHandledTarget_PatchesArgumentToHandleName()
        {
            var sourceValue = new ValueNode.ObjectRef(null);
            var (model, map, componentLogicalId) = MappedRootWithObjectRefField(sourceValue);

            var snapshotValue = new ValueNode.ObjectRef("other-1");
            var snapshot = SnapshotWithObjectRefField(snapshotValue);

            var handles = new Dictionary<string, string> { ["other-1"] = "other" };

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId), handles);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("other", patch.NewExpr);
        }

        // spec #9 / convergence: an unchanged ref (same target, and both-null) produces NO edit —
        // a sync with no scene change must be a no-op.
        [Fact]
        public void Reconcile_IdenticalTarget_ProducesNoEdit()
        {
            var sourceValue = new ValueNode.ObjectRef("door-1");
            var (model, map, componentLogicalId) = MappedRootWithObjectRefField(sourceValue);

            var snapshotValue = new ValueNode.ObjectRef("door-1");
            var snapshot = SnapshotWithObjectRefField(snapshotValue);

            var handles = new Dictionary<string, string> { ["door-1"] = "door" };

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId), handles);

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
        }

        [Fact]
        public void Reconcile_BothNull_ProducesNoEdit()
        {
            var sourceValue = new ValueNode.ObjectRef(null);
            var (model, map, componentLogicalId) = MappedRootWithObjectRefField(sourceValue);

            var snapshotValue = new ValueNode.ObjectRef(null);
            var snapshot = SnapshotWithObjectRefField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
        }

        // Rewire to a target with NO handle anywhere in source -> patch swaps the argument to a
        // NEWLY DERIVED handle name AND an IntroduceHandle edit is emitted naming the target's
        // (pre-rekey) LogicalId, so the applier can rewrite its statement to declare that var.
        [Fact]
        public void Reconcile_RewireToHandlelessTarget_PatchesAndIntroducesHandle()
        {
            var sourceValue = new ValueNode.ObjectRef("door-1");
            var sphereRoot = new GameObjectNode { LogicalId = "sphere-1", Name = "Sphere" };
            var (model, map, componentLogicalId) = MappedRootWithObjectRefField(sourceValue, new[] { sphereRoot });

            var snapshotValue = new ValueNode.ObjectRef("sphere-1");
            var snapshot = SnapshotWithObjectRefField(snapshotValue);

            var handles = new Dictionary<string, string> { ["door-1"] = "door" };

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId), handles);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("sphere", patch.NewExpr);

            var introduce = Assert.Single(result.Patch.Edits.OfType<IntroduceHandle>());
            Assert.Equal("sphere-1", introduce.Anchor);
            Assert.Equal("sphere", introduce.Handle);
        }
    }
}
