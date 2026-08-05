using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A snapshot field whose value is a List of ObjectRefs emits a `new[] { ... }` array of
    // scene-reference selectors, at every emission site (introduce, patch, append) and for every
    // element outcome (resolvable, cleared, self-targeting, unresolvable), and a second
    // reconcile against the same snapshot is a fixed point (zero conflicts, zero edits).
    public class ObjectRefListEmitTests
    {
        private const string DoorOpenerType = "Game.DoorOpener";
        private const string FieldKey = "targets";

        private const string EmptyOpenerScene = @"
public class S : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var other = scene.Add(""Other"");
        var opener = scene.Add(""Opener"");
        opener.Component<Game.DoorOpener>(c => { });
    }
}
";

        private const string AuthoredListScene = @"
public class S : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var other = scene.Add(""Other"");
        var third = scene.Add(""Third"");
        var opener = scene.Add(""Opener"");
        opener.Component<Game.DoorOpener>(c => c.Set(""targets"", new[] { door, other }));
    }
}
";

        private const string DoorsOnlyScene = @"
public class S : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var other = scene.Add(""Other"");
        var opener = scene.Add(""Opener"");
    }
}
";

        private const string HandleLessDoorScene = @"
public class S : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Door"");
        var opener = scene.Add(""Opener"");
        opener.Component<Game.DoorOpener>(c => { });
    }
}
";

        private static IdentityMap MapFor(ParseResult parsed, params (string Name, string Goid)[] nodes)
        {
            var entries = new List<IdentityMapEntry>();
            foreach (var (name, goid) in nodes)
            {
                entries.Add(new IdentityMapEntry
                {
                    LogicalId = parsed.Model.Roots.Single(r => r.Name == name).LogicalId,
                    GlobalObjectId = goid,
                    Kind = "GameObject",
                });
            }

            var openerLid = parsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId;
            var compId = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(DoorOpenerType + "#0"));
            entries.Add(new IdentityMapEntry
            {
                LogicalId = compId,
                GlobalObjectId = "",
                Kind = "Component",
                ComponentType = DoorOpenerType,
                ParentLogicalId = openerLid,
            });

            return new IdentityMap { Entries = entries.ToArray() };
        }

        private static SceneSnapshot SnapshotWith(ValueNode targetsValue, params (string Name, string Goid)[] plainNodes)
        {
            var roots = new List<SnapshotNode>();
            foreach (var (name, goid) in plainNodes)
            {
                roots.Add(new SnapshotNode { GlobalObjectId = goid, Name = name });
            }

            roots.Add(new SnapshotNode
            {
                GlobalObjectId = "goid-opener",
                Name = "Opener",
                Components = new[]
                {
                    new ComponentData
                    {
                        LogicalId = "unused",
                        Type = new TypeRef(DoorOpenerType),
                        Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, targetsValue) }),
                    },
                },
            });

            return new SceneSnapshot { SchemaVersion = 1, Roots = roots.ToArray() };
        }

        [Fact]
        public void IntroducedSceneRefList_EmitsHandleArray_AndSecondReconcileIsFixedPoint()
        {
            var parsed = BuilderParser.Parse(EmptyOpenerScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var otherLid = parsed.Model.Roots.Single(r => r.Name == "Other").LogicalId;
            var map = MapFor(parsed, ("Door", "goid-door"), ("Other", "goid-other"), ("Opener", "goid-opener"));

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.ObjectRef(otherLid) }),
                ("Door", "goid-door"), ("Other", "goid-other"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);
            Assert.Single(result.Patch.Edits);

            var patched = SourcePatchApplier.Apply(EmptyOpenerScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));
            SourcePatchTestHelpers.AssertNoSyntaxErrors(patched);
            Assert.Contains(".Set(\"targets\", new[] { door, other })", patched);

            var reparsed = BuilderParser.Parse(patched, map);
            var recon2 = Reconciler.Reconcile(
                reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors, null, null,
                reparsed.ComponentAnchors, reparsed.FieldArgumentSpans, reparsed.Handles);
            Assert.Empty(recon2.Conflicts);
            Assert.Empty(recon2.Patch.Edits);
        }

        [Fact]
        public void IntroducedSceneRefList_ElementWithoutAuthoredHandle_IntroducesOne()
        {
            var parsed = BuilderParser.Parse(HandleLessDoorScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var openerLid = parsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId;
            var compId = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(DoorOpenerType + "#0"));
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = doorLid, GlobalObjectId = "goid-door", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = openerLid, GlobalObjectId = "goid-opener", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = compId, GlobalObjectId = "", Kind = "Component", ComponentType = DoorOpenerType, ParentLogicalId = openerLid },
                },
            };

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.ObjectRef(null) }),
                ("Door", "goid-door"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);
            Assert.Single(result.Patch.Edits.OfType<IntroduceHandle>());

            var patched = SourcePatchApplier.Apply(HandleLessDoorScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));
            SourcePatchTestHelpers.AssertNoSyntaxErrors(patched);
            Assert.Contains("new[] {", patched);
        }

        [Fact]
        public void PatchedSceneRefList_RewritesOnlyTheChangedElement_AndIsFixedPoint()
        {
            var parsed = BuilderParser.Parse(AuthoredListScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var thirdLid = parsed.Model.Roots.Single(r => r.Name == "Third").LogicalId;
            var map = MapFor(parsed, ("Door", "goid-door"), ("Other", "goid-other"), ("Third", "goid-third"), ("Opener", "goid-opener"));

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.ObjectRef(thirdLid) }),
                ("Door", "goid-door"), ("Other", "goid-other"), ("Third", "goid-third"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);
            var onlyEdit = Assert.Single(result.Patch.Edits);
            var patch = Assert.IsType<PatchComponentField>(onlyEdit);
            Assert.Equal("new[] { door, third }", patch.NewExpr);

            var patched = SourcePatchApplier.Apply(AuthoredListScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));
            SourcePatchTestHelpers.AssertNoSyntaxErrors(patched);
            Assert.Contains(".Set(\"targets\", new[] { door, third })", patched);

            var reparsed = BuilderParser.Parse(patched, map);
            var recon2 = Reconciler.Reconcile(
                reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors, null, null,
                reparsed.ComponentAnchors, reparsed.FieldArgumentSpans, reparsed.Handles);
            Assert.Empty(recon2.Conflicts);
            Assert.Empty(recon2.Patch.Edits);
        }

        [Fact]
        public void ClearedSceneRefListElement_EmitsNodeHandleNoneAtThatIndex_AndIsFixedPoint()
        {
            var parsed = BuilderParser.Parse(AuthoredListScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var map = MapFor(parsed, ("Door", "goid-door"), ("Other", "goid-other"), ("Third", "goid-third"), ("Opener", "goid-opener"));

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.ObjectRef(null) }),
                ("Door", "goid-door"), ("Other", "goid-other"), ("Third", "goid-third"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);
            var onlyEdit = Assert.Single(result.Patch.Edits);
            var patch = Assert.IsType<PatchComponentField>(onlyEdit);
            Assert.Equal("new[] { door, NodeHandle.None }", patch.NewExpr);

            var patched = SourcePatchApplier.Apply(AuthoredListScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));
            SourcePatchTestHelpers.AssertNoSyntaxErrors(patched);

            var reparsed = BuilderParser.Parse(patched, map);
            var recon2 = Reconciler.Reconcile(
                reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors, null, null,
                reparsed.ComponentAnchors, reparsed.FieldArgumentSpans, reparsed.Handles);
            Assert.Empty(recon2.Conflicts);
            Assert.Empty(recon2.Patch.Edits);
        }

        [Fact]
        public void AppendedComponentWithSceneRefList_EmitsParsingSource_AndIsFixedPoint()
        {
            var parsed = BuilderParser.Parse(DoorsOnlyScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var otherLid = parsed.Model.Roots.Single(r => r.Name == "Other").LogicalId;
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = doorLid, GlobalObjectId = "goid-door", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = otherLid, GlobalObjectId = "goid-other", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = parsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId, GlobalObjectId = "goid-opener", Kind = "GameObject" },
                },
            };

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.ObjectRef(otherLid) }),
                ("Door", "goid-door"), ("Other", "goid-other"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(DoorsOnlyScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));
            SourcePatchTestHelpers.AssertNoSyntaxErrors(patched);
            Assert.Contains(".Set(\"targets\", new[] { door, other })", patched);

            var map2 = new IdentityMap { Entries = map.Entries.Concat(result.AddedEntries).ToArray() };
            var reparsed = BuilderParser.Parse(patched, map2);
            var recon2 = Reconciler.Reconcile(
                reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors, null, null,
                reparsed.ComponentAnchors, reparsed.FieldArgumentSpans, reparsed.Handles);
            Assert.Empty(recon2.Conflicts);
            Assert.Empty(recon2.Patch.Edits);
        }

        [Fact]
        public void IntroducedSceneRefList_DanglingElement_ReportsLocatedDanglingReference_EmitsNoField()
        {
            var parsed = BuilderParser.Parse(EmptyOpenerScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var map = MapFor(parsed, ("Door", "goid-door"), ("Other", "goid-other"), ("Opener", "goid-opener"));

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.ObjectRef("ghost-9") }),
                ("Door", "goid-door"), ("Other", "goid-other"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            var conflict = Assert.Single(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Contains("ghost-9", conflict.Reason);
            Assert.Empty(result.Patch.Edits.OfType<IntroduceComponentField>());
        }

        [Fact]
        public void SceneRefListElementTargetingOwner_RendersNodeHandleSelf_AndIsFixedPoint()
        {
            var parsed = BuilderParser.Parse(EmptyOpenerScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var openerLid = parsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId;
            var map = MapFor(parsed, ("Door", "goid-door"), ("Other", "goid-other"), ("Opener", "goid-opener"));

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.ObjectRef(openerLid) }),
                ("Door", "goid-door"), ("Other", "goid-other"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);
            var patched = SourcePatchApplier.Apply(EmptyOpenerScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));
            SourcePatchTestHelpers.AssertNoSyntaxErrors(patched);
            Assert.Contains(".Set(\"targets\", new[] { door, NodeHandle.Self })", patched);

            var reparsed = BuilderParser.Parse(patched, map);
            var recon2 = Reconciler.Reconcile(
                reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors, null, null,
                reparsed.ComponentAnchors, reparsed.FieldArgumentSpans, reparsed.Handles);
            Assert.Empty(recon2.Conflicts);
            Assert.Empty(recon2.Patch.Edits);
        }
    }
}
