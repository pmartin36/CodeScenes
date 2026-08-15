using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;
using static SceneBuilder.Core.Tests.SourcePatchTestHelpers;

namespace SceneBuilder.Core.Tests
{
    // ResolveIntroduceComponentField folds a forward-referencing field into an existing
    // `.Component<T>(...)` closure IN PLACE, without checking that the argument handle it now
    // names is declared earlier. The live two-sync repro: Opener and Door are added first (Door
    // gets no handle, nothing references it yet); a LATER sync sets Opener's component field to
    // Door. The fold lands above Door's own declaration and the emitted source fails to compile
    // (CS0841). The fix hoists Door's declaration above the referencing component through the
    // placement path, leaving the `.Component<T>()` statement itself untouched.
    public class IntroduceForwardReferencePlacementTests
    {
        private const string DoorOpenerType = "Game.DoorOpener";
        private const string TargetField = "target";

        private const string EmptyScene = @"using SceneBuilder.Authoring;
public class EmptyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
    }
}
";

        private static ComponentData DoorOpenerComponent(params KeyValuePair<string, ValueNode>[] fields) =>
            new() { Type = new TypeRef(DoorOpenerType), Fields = new FieldMap(fields) };

        // Pass 1: Opener gets a DoorOpener component with no fields yet; Door is appended as a
        // handle-less root (nothing references it). Mirrors an editor session where both objects
        // are added before any reference is wired between them.
        private static string SyncOpenerAndDoor()
        {
            var parsed = BuilderParser.Parse(EmptyScene);
            var map = new IdentityMap { Entries = Array.Empty<IdentityMapEntry>() };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-opener",
                        Name = "Opener",
                        Components = new[] { DoorOpenerComponent() },
                    },
                    new SnapshotNode { GlobalObjectId = "goid-door", Name = "Door" },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, handles: parsed.Handles);
            Assert.Empty(result.Conflicts);

            return SourcePatchApplier.Apply(EmptyScene, result.Patch, parsed.Anchors);
        }

        // Pass 2: reparse the converged pass-1 source with both nodes now mapped, and wire
        // Opener's DoorOpener.target at Door -- the live repro's second sync.
        private static (string Patched, IdentityMap Map, SceneSnapshot Snapshot) SyncTargetOntoOpener(string source)
        {
            var parsed = BuilderParser.Parse(source);
            var openerLid = parsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId;
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var componentLid = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(DoorOpenerType + "#0"));

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = openerLid, GlobalObjectId = "goid-opener", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = doorLid, GlobalObjectId = "goid-door", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = componentLid,
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = DoorOpenerType,
                        ParentLogicalId = openerLid,
                    },
                },
            };

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
                            DoorOpenerComponent(new KeyValuePair<string, ValueNode>(TargetField, new ValueNode.ObjectRef(doorLid))),
                        },
                    },
                    new SnapshotNode { GlobalObjectId = "goid-door", Name = "Door" },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);
            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(source, result.Patch, MergeAnchors(parsed));
            return (patched, map, snapshot);
        }

        // The pass-1/pass-2 live repro: the applied source must compile with zero errors
        // (specifically no CS0841), Door's declaration must precede the referencing component, and
        // the component statement itself must not be duplicated/relocated -- only Door's
        // declaration moves.
        [Fact]
        public void Introduce_ForwardReference_HoistsTargetAboveComponent_CompilesCleanly()
        {
            var pass1 = SyncOpenerAndDoor();
            var (patched, _, _) = SyncTargetOntoOpener(pass1);

            var doorDeclIndex = patched.IndexOf("var door = scene.Add(\"Door\")", StringComparison.Ordinal);
            var setCallIndex = patched.IndexOf(".Set(\"target\", door)", StringComparison.Ordinal);
            Assert.True(doorDeclIndex >= 0, "expected door's own declaration in applied source:\n" + patched);
            Assert.True(setCallIndex >= 0, "expected the folded target set in applied source:\n" + patched);
            Assert.True(
                doorDeclIndex < setCallIndex,
                "door's declaration must precede the referencing component:\n" + patched);
            Assert.Equal(1, CountOccurrences(patched, "opener.Component<Game.DoorOpener>"));

            var errors = AuthoringBindHarness.BindErrors(patched, AuthoringBindHarness.DoorOpenerStubs);
            Assert.Empty(errors);
        }

        // A third pass over the converged source, re-synced against the same snapshot, must be a
        // fixed point: the hoist is a one-time repair, not a per-sync churn. Door acquires its
        // handle during pass 2 (its LogicalId shifts from the positional "Door/1" to the handle
        // name "door"), so the third pass's map is rebuilt fresh from the pass-2 output rather than
        // reusing pass 2's map -- mirroring how a real sync re-derives identity from the live scene
        // on every pass.
        [Fact]
        public void Introduce_ForwardReference_ThirdPassIsFixedPoint()
        {
            var pass1 = SyncOpenerAndDoor();
            var (patched, _, _) = SyncTargetOntoOpener(pass1);

            var reparsed = BuilderParser.Parse(patched);
            var doorLid = reparsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var openerLid = reparsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId;
            var componentLid = reparsed.ComponentAnchors.Keys.Single(k => k.EndsWith(DoorOpenerType + "#0"));

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = openerLid, GlobalObjectId = "goid-opener", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = doorLid, GlobalObjectId = "goid-door", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = componentLid,
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = DoorOpenerType,
                        ParentLogicalId = openerLid,
                    },
                },
            };

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
                            DoorOpenerComponent(new KeyValuePair<string, ValueNode>(TargetField, new ValueNode.ObjectRef(doorLid))),
                        },
                    },
                    new SnapshotNode { GlobalObjectId = "goid-door", Name = "Door" },
                },
            };

            var third = Reconciler.Reconcile(
                reparsed.Model, snapshot, map, reparsed.Anchors, null, null,
                reparsed.ComponentAnchors, reparsed.FieldArgumentSpans, reparsed.Handles);

            Assert.Empty(third.Conflicts);
            Assert.Empty(third.Patch.Edits);
        }

        // Two same-type components on Opener, the FORWARD-REFERRING one authored first. Door must
        // hoist above BOTH components (it is a peer of Opener, not of either component), and the
        // two components must keep their authored relative order -- proof the fix relocates the
        // declaration, never the component the alternative "move the component down" approach
        // would have reordered.
        [Fact]
        public void Introduce_TwoSameTypeComponents_ForwardReferrerFirst_NoComponentReorderChurn()
        {
            const string twoComponentScene = @"using SceneBuilder.Authoring;
public class TwoComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Opener"");
        opener.Component<Game.DoorOpener>();
        opener.Component<Game.DoorOpener>();
        scene.Add(""Door"");
    }
}
";
            var parsed = BuilderParser.Parse(twoComponentScene);
            var openerLid = parsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId;
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var comp0Lid = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(DoorOpenerType + "#0"));
            var comp1Lid = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(DoorOpenerType + "#1"));

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = openerLid, GlobalObjectId = "goid-opener", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = doorLid, GlobalObjectId = "goid-door", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = comp0Lid, GlobalObjectId = "", Kind = "Component",
                        ComponentType = DoorOpenerType, ParentLogicalId = openerLid,
                    },
                    new IdentityMapEntry
                    {
                        LogicalId = comp1Lid, GlobalObjectId = "", Kind = "Component",
                        ComponentType = DoorOpenerType, ParentLogicalId = openerLid,
                    },
                },
            };

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
                            DoorOpenerComponent(new KeyValuePair<string, ValueNode>(TargetField, new ValueNode.ObjectRef(doorLid))),
                            DoorOpenerComponent(),
                        },
                    },
                    new SnapshotNode { GlobalObjectId = "goid-door", Name = "Door" },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);
            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Patch.Edits.OfType<ReorderStatement>());
            Assert.Empty(result.Patch.Edits.OfType<MoveStatement>());

            var patched = SourcePatchApplier.Apply(twoComponentScene, result.Patch, MergeAnchors(parsed));

            var doorDeclIndex = patched.IndexOf("var door = scene.Add(\"Door\")", StringComparison.Ordinal);
            var firstComponentIndex = patched.IndexOf("opener.Component<Game.DoorOpener>(c => c.Set(\"target\", door))", StringComparison.Ordinal);
            var secondComponentIndex = patched.IndexOf("opener.Component<Game.DoorOpener>()", StringComparison.Ordinal);
            Assert.True(doorDeclIndex >= 0, "expected door's own declaration in applied source:\n" + patched);
            Assert.True(firstComponentIndex >= 0, "expected the folded first component in applied source:\n" + patched);
            Assert.True(secondComponentIndex >= 0, "expected the untouched second component in applied source:\n" + patched);
            Assert.True(
                doorDeclIndex < firstComponentIndex,
                "door's declaration must precede both components:\n" + patched);
            Assert.True(
                firstComponentIndex < secondComponentIndex,
                "the two components must keep their authored order:\n" + patched);

            var errors = AuthoringBindHarness.BindErrors(patched, AuthoringBindHarness.DoorOpenerStubs);
            Assert.Empty(errors);
        }

        // A field referencing an ALREADY-declared handle needs no hoist: the in-place closure fold
        // must stay byte-identical to a straight fold, so existing generated files do not churn.
        [Fact]
        public void Introduce_BackwardReference_InPlaceFoldUnchanged()
        {
            const string backwardScene = @"
public class BackwardOpenerScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var opener = scene.Add(""Opener"");
        opener.Component<Game.DoorOpener>();
    }
}
";
            var parsed = BuilderParser.Parse(backwardScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var openerLid = parsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId;
            var componentLid = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(DoorOpenerType + "#0"));

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = doorLid, GlobalObjectId = "goid-door", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = openerLid, GlobalObjectId = "goid-opener", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = componentLid, GlobalObjectId = "", Kind = "Component",
                        ComponentType = DoorOpenerType, ParentLogicalId = openerLid,
                    },
                },
            };

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
                            DoorOpenerComponent(new KeyValuePair<string, ValueNode>(TargetField, new ValueNode.ObjectRef(doorLid))),
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);
            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(backwardScene, result.Patch, MergeAnchors(parsed));

            var expected = backwardScene.Replace(
                "opener.Component<Game.DoorOpener>();",
                "opener.Component<Game.DoorOpener>(c => c.Set(\"target\", door));");
            Assert.Equal(expected, patched);
        }

        // A plain (non-ObjectRef) field introduction names no handle at all; the companion hoist
        // must no-op cleanly rather than perturb the closure fold.
        [Fact]
        public void Introduce_NoReferenceField_InPlaceFoldUnchanged()
        {
            const string plainFieldScene = @"
public class PlainFieldScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Opener"");
        opener.Component<Game.DoorOpener>();
    }
}
";
            var parsed = BuilderParser.Parse(plainFieldScene);
            var openerLid = parsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId;
            var componentLid = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(DoorOpenerType + "#0"));

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = openerLid, GlobalObjectId = "goid-opener", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = componentLid, GlobalObjectId = "", Kind = "Component",
                        ComponentType = DoorOpenerType, ParentLogicalId = openerLid,
                    },
                },
            };

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
                            DoorOpenerComponent(new KeyValuePair<string, ValueNode>("speed", ValueNode.Primitive.Float(5f))),
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);
            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(plainFieldScene, result.Patch, MergeAnchors(parsed));

            var expected = plainFieldScene.Replace(
                "opener.Component<Game.DoorOpener>();",
                "opener.Component<Game.DoorOpener>(c => c.Set(\"speed\", 5f));");
            Assert.Equal(expected, patched);
        }

        // From-scratch (parent+child+ref reconciled in one pass) and the two-pass introduce repro
        // must converge on the SAME emitted shape: `var target = ...; owner.Component<T>(c =>
        // c.Set(field, target));` -- the ordered statement text, ignoring the different receiver
        // names BuilderParser derives for a from-scratch batch vs an existing mapped scene, matches.
        [Fact]
        public void Introduce_And_FromScratch_AgreeOnEmittedShape()
        {
            var pass1 = SyncOpenerAndDoor();
            var (introducePatched, _, _) = SyncTargetOntoOpener(pass1);

            var fromScratchParsed = BuilderParser.Parse(EmptyScene);
            var fromScratchMap = new IdentityMap { Entries = Array.Empty<IdentityMapEntry>() };

            var fromScratchSnapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-opener2",
                        Name = "Opener",
                        Components = new[]
                        {
                            DoorOpenerComponent(new KeyValuePair<string, ValueNode>(TargetField, new ValueNode.ObjectRef("goid-door2"))),
                        },
                    },
                    new SnapshotNode { GlobalObjectId = "goid-door2", Name = "Door" },
                },
            };

            var fromScratchResult = Reconciler.Reconcile(
                fromScratchParsed.Model, fromScratchSnapshot, fromScratchMap, fromScratchParsed.Anchors,
                handles: fromScratchParsed.Handles);
            Assert.Empty(fromScratchResult.Conflicts);

            var fromScratchPatched = SourcePatchApplier.Apply(EmptyScene, fromScratchResult.Patch, fromScratchParsed.Anchors);

            var introduceDoorIndex = introducePatched.IndexOf("var door = scene.Add(\"Door\")", StringComparison.Ordinal);
            var introduceSetIndex = introducePatched.IndexOf(".Set(\"target\", door)", StringComparison.Ordinal);
            var fromScratchDoorIndex = fromScratchPatched.IndexOf("scene.Add(\"Door\")", StringComparison.Ordinal);
            var fromScratchSetIndex = fromScratchPatched.IndexOf(".Set(\"target\", door)", StringComparison.Ordinal);

            Assert.True(introduceDoorIndex >= 0 && introduceDoorIndex < introduceSetIndex,
                "introduce path: door must precede the referencing component:\n" + introducePatched);
            Assert.True(fromScratchDoorIndex >= 0 && fromScratchSetIndex >= 0 && fromScratchDoorIndex < fromScratchSetIndex,
                "from-scratch path: door must precede the referencing component:\n" + fromScratchPatched);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }
    }
}
