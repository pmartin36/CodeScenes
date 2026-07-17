using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t3: §13 create-with-payload for an ObjectRef field. TODAY a newly-created object carrying
    // a cross-object ref does not silently null it — it THROWS in the applier
    // (SourceExpr.ValueNodeLiteral has no ObjectRef arm, ComponentPatchApplier.BuildComponentStatementText
    // renders every AppendComponentStatement field through it). These tests exercise the real
    // Reconcile -> Apply round trip so the current throw is the RED signal, mirroring
    // ReconcileConflictTests.Reconcile_CreatedObjectWithPayload_ConvergesNoSilentDrop's harness.
    public class ObjectRefNewObjectTests
    {
        private const string DoorOpenerType = "Game.DoorOpener";

        private const string OwnerAndDoorScene = @"
public class OwnerAndDoorScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
    }
}
";

        private const string EmptyScene = @"
public class EmptyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
    }
}
";

        // Deliverable #10 primary: a newly-created scene object (unmapped goid) carries a
        // DoorOpener.target ObjectRef pointing at an EXISTING mapped+handled root ("door"). The
        // ref must be appended onto the just-created statement as the handle argument
        // (`.Set("target", door)`) — never a thrown NotSupportedException, never a silent null.
        [Fact]
        public void Reconcile_CrossRefOnNewObject_AppendsHandleArgument()
        {
            var parsed = BuilderParser.Parse(OwnerAndDoorScene);
            var doorLogicalId = Assert.Single(parsed.Model.Roots).LogicalId;

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = doorLogicalId, GlobalObjectId = "goid-door", Kind = "GameObject" },
                },
            };

            var openerFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(doorLogicalId)),
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-door", Name = "Door" },
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-opener",
                        Name = "Opener",
                        Components = new[]
                        {
                            new ComponentData { Type = new TypeRef(DoorOpenerType), Fields = openerFields },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, handles: parsed.Handles);

            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(OwnerAndDoorScene, result.Patch, parsed.Anchors);

            Assert.Contains(".Set(\"target\", door)", patched);
        }

        // Deliverable #10 tail: a new object carrying `ObjectRef(null)` (target explicitly None)
        // renders `NodeHandle.None`, never a thrown exception and never a bare-null placeholder.
        [Fact]
        public void Reconcile_CrossRefOnNewObject_NullTarget_RendersNodeHandleNone()
        {
            var parsed = BuilderParser.Parse(EmptyScene);
            var map = new IdentityMap { Entries = System.Array.Empty<IdentityMapEntry>() };

            var openerFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(null)),
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-opener",
                        Name = "Opener",
                        Components = new[]
                        {
                            new ComponentData { Type = new TypeRef(DoorOpenerType), Fields = openerFields },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(EmptyScene, result.Patch, parsed.Anchors);

            Assert.Contains(".Set(\"target\", NodeHandle.None)", patched);
        }

        // Case B: an EXISTING mapped source object's field is rewired to a same-batch NEW target
        // (an unmapped snapshot node created in this same edit). This must NOT report a
        // DanglingReference (b4-t2 Detection 2 would otherwise mistake the create-candidate for a
        // phantom target) — it defers, converging on the guaranteed second Sync once the target is
        // mapped. Never a silent null, never a bogus DanglingReference on a target that is actually
        // being created right now.
        [Fact]
        public void Reconcile_CrossRefToNewTarget_DefersNotDangling()
        {
            const string ownerLogicalId = "root-1";
            var componentLogicalId = $"{ownerLogicalId}/{DoorOpenerType}#0";

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
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
                                Type = new TypeRef(DoorOpenerType),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(null)),
                                }),
                            },
                        },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            // The rewired-to target is a same-batch create candidate: its own (unmapped) snapshot
            // GlobalObjectId doubles as the only identity Core has for it before DetectAppends runs.
            const string newTargetGoid = "goid-newtarget";

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
                                Type = new TypeRef(DoorOpenerType),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(newTargetGoid)),
                                }),
                            },
                        },
                    },
                    new SnapshotNode { GlobalObjectId = newTargetGoid, Name = "NewTarget" },
                },
            };

            var fieldSpans = new Dictionary<string, IReadOnlyDictionary<string, SourceSpan>>
            {
                [componentLogicalId] = new Dictionary<string, SourceSpan> { ["target"] = new SourceSpan(0, 10) },
            };

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, fieldSpans);

            Assert.Empty(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>().Where(p => p.Anchor == componentLogicalId));
        }
    }
}
