using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t3 iteration 2 (scope-validator bucket-b4 Findings 1+2): two apply-time ObjectRef paths
    // b4-t3/iteration-1 left unhandled.
    //   Finding 1 (HIGH, reproduced apply-time crash): a MAPPED component's field that is present
    //   in the snapshot but ABSENT from source ("IntroduceComponentField") carries the raw
    //   ValueNode; SourceExpr.ValueNodeLiteral has no ObjectRef arm and throws NotSupportedException
    //   at apply time (SourceExpr.cs:110). Mirrors b4-t1's PatchComponentField fix, but for the
    //   introduce path.
    //   Finding 2 (MED, permanent silent null): a genuinely-new object's (DetectAppends, not the
    //   mapped-owner ADD loop) unresolvable ObjectRef field is dropped from the append with NO
    //   conflict, forever, because the append path never threads `conflicts` through.
    public class ObjectRefIntroduceFieldTests
    {
        private const string DoorOpenerType = "Game.DoorOpener";

        private const string OwnerAndDoorScene = @"
public class OwnerAndDoorScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var opener = scene.Add(""Opener"");
        opener.Component<Game.DoorOpener>(c => { });
    }
}
";

        private const string OpenerOnlyScene = @"
public class OpenerOnlyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Opener"");
        opener.Component<Game.DoorOpener>(c => { });
    }
}
";

        // Finding 1, LOAD-BEARING: the exact crash the scope-validator reproduced. A mapped
        // owner's DoorOpener component was authored with an EMPTY closure (no `target` field); the
        // snapshot now carries `target = ObjectRef(door)` (the user wired the reference in the
        // editor). Reconcile -> Apply must render `.Set("target", door)`, never throw
        // NotSupportedException.
        [Fact]
        public void Reconcile_IntroducedObjectRefField_Apply_RendersHandle()
        {
            var parsed = BuilderParser.Parse(OwnerAndDoorScene);
            var doorLogicalId = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var openerLogicalId = parsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId;

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = doorLogicalId, GlobalObjectId = "goid-door", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = openerLogicalId, GlobalObjectId = "goid-opener", Kind = "GameObject" },
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
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(OwnerAndDoorScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));

            Assert.Contains(".Set(\"target\", door)", patched);
        }

        // Finding 1 sibling: an introduced `ObjectRef(null)` field renders "NodeHandle.None", never
        // a thrown exception and never a bare-null placeholder.
        [Fact]
        public void Reconcile_IntroducedObjectRefField_Null_RendersNodeHandleNone()
        {
            var parsed = BuilderParser.Parse(OpenerOnlyScene);
            var openerLogicalId = Assert.Single(parsed.Model.Roots).LogicalId;

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = openerLogicalId, GlobalObjectId = "goid-opener", Kind = "GameObject" },
                },
            };

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

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(OpenerOnlyScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));

            Assert.Contains(".Set(\"target\", NodeHandle.None)", patched);
        }

        // Finding 1, "never a silent null" side: an introduced field whose target is neither
        // resolvable nor a same-batch create-candidate (genuinely unknown) must report a located
        // DanglingReference and emit NO IntroduceComponentField edit — never a phantom-handle
        // render, never a bare unresolved ObjectRef left for the applier to choke on.
        [Fact]
        public void Reconcile_IntroducedObjectRefField_UnknownTarget_ReportsDangling()
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
                                Fields = FieldMap.Empty,
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

            var snapshotFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef("ghost-2")),
            });

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
                            new ComponentData { LogicalId = "unused", Type = new TypeRef(DoorOpenerType), Fields = snapshotFields },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var conflict = Assert.Single(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Contains("ghost-2", conflict.Reason);
            Assert.Empty(result.Patch.Edits.OfType<IntroduceComponentField>());
        }

        // Finding 2: a genuinely-new object (DetectAppends path, not the mapped-owner ADD loop)
        // whose component field is an unresolvable ObjectRef must report a located
        // DanglingReference, not disappear forever. The field is still correctly OMITTED from the
        // append (iteration-1 already does that part) — what iteration-1 dropped was the report.
        [Fact]
        public void Reconcile_NewObjectUnresolvableRef_ReportsDangling()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = System.Array.Empty<GameObjectNode>() };

            var openerFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef("ghost-1")),
            });

            var created = new SnapshotNode
            {
                GlobalObjectId = "goid-opener",
                Name = "Opener",
                Components = new[]
                {
                    new ComponentData { Type = new TypeRef(DoorOpenerType), Fields = openerFields },
                },
            };

            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { created } };
            var map = new IdentityMap { Entries = System.Array.Empty<IdentityMapEntry>() };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var conflict = Assert.Single(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Contains("ghost-1", conflict.Reason);

            var attach = Assert.Single(result.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.False(attach.Fields.ContainsKey("target"));
        }
    }
}
