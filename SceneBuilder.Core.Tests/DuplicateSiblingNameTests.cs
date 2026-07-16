using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // DUPLICATE SIBLING NAMES ARE SILENT, DESTRUCTIVE DATA LOSS.
    //
    // Two same-named siblings with no `.Id(...)` are distinguishable ONLY by position: their
    // LogicalIds are `{parent}/{name}/{siblingIndex}` and LogicalIdResolver claims persisted ids
    // from a (parent, name) queue in DOCUMENT ORDER. The id is therefore pinned to the SLOT, not
    // to the OBJECT — so any edit that MOVES a statement silently re-points identity at a
    // different real object.
    //
    // Nothing surfaces, because the resulting scene is self-consistent: it faithfully materializes
    // the wrong-but-coherent model. That is why these tests assert on IDENTITY (GlobalObjectId
    // survival), not on the shape of the emitted scene.
    //
    // THE KEY POINT, and why the first two tests characterize the damage instead of asserting it
    // away: once a file CONTAINS an ambiguous pair, there is NO correct answer — the information
    // that distinguishes the two objects was never written down. So the fix cannot be "resolve it
    // better"; it is PREVENTION (Sync injects `.Id(...)` so the pair never forms) plus REFUSAL
    // (Build never guesses at one a human/LLM hand-authored). These tests pin all three.
    public class DuplicateSiblingNameTests
    {
        private const string RigidbodyFirst = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Transform(pos: (0f, 0f, 0f)).Component<UnityEngine.Rigidbody>();
        scene.Add(""Enemy"").Transform(pos: (5f, 0f, 0f));
    }
}
";

        // The SAME scene, with the two statements swapped. A pure reorder: no object created, none
        // deleted, no component added or removed.
        private const string RigidbodySecond = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Transform(pos: (5f, 0f, 0f));
        scene.Add(""Enemy"").Transform(pos: (0f, 0f, 0f)).Component<UnityEngine.Rigidbody>();
    }
}
";

        // The sidecar as it stands after the first parse+build: goid-A is the Rigidbody-owning
        // Enemy at x=0 (slot 0), goid-B is the bare Enemy at x=5 (slot 1).
        private static IdentityMap PriorMap() => new()
        {
            Scene = "Assets/Scenes/Dup.unity",
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "Enemy/0", GlobalObjectId = "goid-A", Kind = "GameObject", Name = "Enemy", SiblingIndex = 0 },
                new IdentityMapEntry
                {
                    LogicalId = "Enemy/0/UnityEngine.Rigidbody#0",
                    GlobalObjectId = "goid-A-rb",
                    Kind = "Component",
                    ComponentType = "UnityEngine.Rigidbody",
                    ParentLogicalId = "Enemy/0",
                },
                new IdentityMapEntry { LogicalId = "Enemy/1", GlobalObjectId = "goid-B", Kind = "GameObject", Name = "Enemy", SiblingIndex = 1 },
            },
        };

        // THE DEFECT, characterized. A pure REORDER of two same-named positional siblings SWAPS
        // their identities: the Rigidbody's real owner (goid-A) comes back as goid-B, the live
        // Rigidbody goid-A-rb is left unconsumed (=> orphan => DESTROYED) and a brand-new one is
        // CREATED on the wrong Enemy. A pure reorder destroys a real component.
        //
        // This is unfixable AFTER the fact — hence the assertion that Parse REPORTS it. That report
        // is what Build refuses on and what Sync heals, so this test is the anchor for both.
        [Fact]
        public void Reorder_TwoSameNamedPositionalSiblings_SwapsIdentity_AndParseReportsTheAmbiguity()
        {
            var prior = PriorMap();
            var reparsed = BuilderParser.Parse(RigidbodySecond, prior);

            var remapped = IdentityRemapper.Remap(reparsed.Model, prior);

            // The damage, pinned: the Rigidbody-owning Enemy is the SAME real object as before
            // (goid-A), yet it comes back as goid-B.
            var rigidbodyOwner = reparsed.Model.Roots.Single(r => r.Components.Length > 0);
            var ownerEntry = Assert.Single(remapped.Entries, e => e.Kind == "GameObject" && e.LogicalId == rigidbodyOwner.LogicalId);
            Assert.Equal("goid-B", ownerEntry.GlobalObjectId);

            // Its Rigidbody has NO id => it will be CREATED...
            var rigidbody = rigidbodyOwner.Components.Single();
            var componentEntry = Assert.Single(remapped.Entries, e => e.Kind == "Component" && e.LogicalId == rigidbody.LogicalId);
            Assert.Equal("", componentEntry.GlobalObjectId);

            // ...while the LIVE Rigidbody goes unconsumed => orphan => DESTROYED.
            Assert.Contains(remapped.Entries, e => e.Kind == "Component" && e.GlobalObjectId == "goid-A-rb" && e.LogicalId != rigidbody.LogicalId);

            // WHICH IS WHY the parse must refuse to let this stand silently.
            var ambiguity = Assert.Single(reparsed.Ambiguities);
            Assert.Equal(ConflictKind.AmbiguousAnchor, ambiguity.Kind);
            Assert.Contains("Enemy", ambiguity.Reason);
            Assert.Contains(".Id(", ambiguity.Reason);
            Assert.NotNull(ambiguity.Location);
        }

        // THE DEFECT, characterized: deleting the FIRST of two same-named siblings destroys the
        // object the user KEPT. The surviving statement claims slot 0's persisted id (goid-A — the
        // DELETED object), so goid-B (the one the user kept) is orphaned => DESTROYED, and the tool
        // repurposes the very object the user deleted. The end state LOOKS right, so nothing
        // surfaces.
        [Fact]
        public void DeleteFirstDuplicate_DestroysTheKeptObject_AndParseReportedTheAmbiguity()
        {
            const string onlySecondRemains = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Transform(pos: (5f, 0f, 0f));
    }
}
";
            var prior = PriorMap();

            // The file BEFORE the delete is the ambiguous one — that is where the report fires, and
            // where Build would have refused / Sync would have healed.
            Assert.NotEmpty(BuilderParser.Parse(RigidbodyFirst, prior).Ambiguities);

            var reparsed = BuilderParser.Parse(onlySecondRemains, prior);
            var remapped = IdentityRemapper.Remap(reparsed.Model, prior);

            // The survivor is the x=5 Enemy, which is really goid-B — but it comes back as goid-A.
            var survivor = Assert.Single(reparsed.Model.Roots);
            var survivorEntry = Assert.Single(remapped.Entries, e => e.Kind == "GameObject" && e.LogicalId == survivor.LogicalId);
            Assert.Equal("goid-A", survivorEntry.GlobalObjectId);

            // goid-B — the object the user KEPT — is the one orphaned for destruction.
            Assert.Contains(remapped.Entries, e => e.Kind == "GameObject" && e.GlobalObjectId == "goid-B" && e.LogicalId != survivor.LogicalId);
        }

        // THE FIX, half 1: once the pair carries `.Id(...)`, a reorder is just a reorder. The id
        // lives IN the statement, so moving the statement carries it along.
        [Fact]
        public void Reorder_DisambiguatedSameNamedSiblings_PreservesIdentityAndComponent()
        {
            const string disambiguated = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Id(""Enemy-1"").Transform(pos: (0f, 0f, 0f)).Component<UnityEngine.Rigidbody>();
        scene.Add(""Enemy"").Id(""Enemy-2"").Transform(pos: (5f, 0f, 0f));
    }
}
";
            // The same two statements, SWAPPED.
            const string disambiguatedSwapped = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Id(""Enemy-2"").Transform(pos: (5f, 0f, 0f));
        scene.Add(""Enemy"").Id(""Enemy-1"").Transform(pos: (0f, 0f, 0f)).Component<UnityEngine.Rigidbody>();
    }
}
";
            var prior = new IdentityMap
            {
                Scene = "Assets/Scenes/Dup.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "Enemy-1", GlobalObjectId = "goid-A", Kind = "GameObject", Name = "Enemy", SiblingIndex = 0 },
                    new IdentityMapEntry
                    {
                        LogicalId = "Enemy-1/UnityEngine.Rigidbody#0",
                        GlobalObjectId = "goid-A-rb",
                        Kind = "Component",
                        ComponentType = "UnityEngine.Rigidbody",
                        ParentLogicalId = "Enemy-1",
                    },
                    new IdentityMapEntry { LogicalId = "Enemy-2", GlobalObjectId = "goid-B", Kind = "GameObject", Name = "Enemy", SiblingIndex = 1 },
                },
            };

            Assert.Empty(BuilderParser.Parse(disambiguated, prior).Ambiguities);

            var reparsed = BuilderParser.Parse(disambiguatedSwapped, prior);
            Assert.Empty(reparsed.Ambiguities);

            var remapped = IdentityRemapper.Remap(reparsed.Model, prior);

            // Identity SURVIVES the reorder, which is the entire point.
            var rigidbodyOwner = reparsed.Model.Roots.Single(r => r.Components.Length > 0);
            Assert.Equal("Enemy-1", rigidbodyOwner.LogicalId);
            Assert.Equal("goid-A", Assert.Single(remapped.Entries, e => e.Kind == "GameObject" && e.LogicalId == "Enemy-1").GlobalObjectId);
            Assert.Equal("goid-B", Assert.Single(remapped.Entries, e => e.Kind == "GameObject" && e.LogicalId == "Enemy-2").GlobalObjectId);

            // The live Rigidbody is NOT destroyed-and-recreated.
            Assert.Equal("goid-A-rb", Assert.Single(remapped.Entries, e => e.Kind == "Component").GlobalObjectId);
        }

        [Fact]
        public void Parse_SameNamedSiblingsWithHandles_ReportsNoAmbiguity()
        {
            const string handled = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var a = scene.Add(""Enemy"");
        var b = scene.Add(""Enemy"");
    }
}
";
            Assert.Empty(BuilderParser.Parse(handled).Ambiguities);
        }

        // The group is PER PARENT — two objects named "Enemy" under different parents are not
        // ambiguous, because their ids embed their parents'.
        [Fact]
        public void Parse_SameNameUnderDifferentParents_ReportsNoAmbiguity()
        {
            const string differentParents = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var left = scene.Add(""Left"");
        left.Add(""Enemy"");
        var right = scene.Add(""Right"");
        right.Add(""Enemy"");
    }
}
";
            Assert.Empty(BuilderParser.Parse(differentParents).Ambiguities);
        }

        // Three siblings named "Enemy" of which ONE carries an explicit id still leave TWO that only
        // position tells apart. An `All(members are positional)` test would score this group clean
        // and walk straight past a live instance of the defect.
        [Fact]
        public void Parse_MixedExplicitAndPositionalDuplicates_StillReportsTheTwoPositionalOnes()
        {
            const string mixed = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Id(""Enemy-1"");
        scene.Add(""Enemy"");
        scene.Add(""Enemy"");
    }
}
";
            var ambiguity = Assert.Single(BuilderParser.Parse(mixed).Ambiguities);
            Assert.Contains("Enemy/1", ambiguity.Reason);
            Assert.Contains("Enemy/2", ambiguity.Reason);
            Assert.DoesNotContain("Enemy-1", ambiguity.Reason);
        }

        // THE FIX, half 2: the write path can no longer CREATE the hazard. Appending an object whose
        // name already exists under that parent mints `.Id(...)` at that moment.
        [Fact]
        public void Reconcile_AppendDuplicateName_InjectsDeterministicSemanticId()
        {
            var existing = new GameObjectNode { LogicalId = "Enemy/0", Name = "Enemy" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { existing } };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-A", Name = "Enemy" },
                    new SnapshotNode { GlobalObjectId = "goid-NEW", Name = "Enemy" },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = "Enemy/0", GlobalObjectId = "goid-A", Kind = "GameObject" } },
            };

            var anchors = new Dictionary<string, SourceSpan> { ["Enemy/0"] = new SourceSpan(0, 10) };

            var result = Reconciler.Reconcile(model, snapshot, map, anchors);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendStatement>());
            // A duplicate-name create candidate heads its own `var` handle (b2-t2) — never a
            // positional id and never a minted `.Id(...)`. `reserved` is {Enemy/0} only, so
            // Derive yields "enemy", NOT "enemy2" (a positional id and the identifier namespace
            // are separate — see HandleNamingTests).
            Assert.Equal("enemy", append.Handle);
            Assert.Equal("enemy", append.NewLogicalId);

            // The sidecar entry must carry the SAME id, or the next sync reads the object as
            // unmapped and appends it a second time.
            Assert.Contains(result.AddedEntries, e => e.LogicalId == "enemy" && e.GlobalObjectId == "goid-NEW");
        }

        // Deterministic AND collision-checked: the identifier "enemy" already taken by an authored
        // handle => "enemy2".
        [Fact]
        public void Reconcile_AppendDuplicateName_MintedIdAvoidsCollisionWithAuthoredId()
        {
            // The incumbent is an authored HANDLE (not a minted `.Id(...)`) — a positional id like
            // "Enemy-2" no longer forces a suffix (separate namespaces), so the only way to force a
            // collision on the identifier "enemy" is for it to already be taken.
            var existing = new GameObjectNode { LogicalId = "enemy", Name = "Enemy" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { existing } };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-A", Name = "Enemy" },
                    new SnapshotNode { GlobalObjectId = "goid-NEW", Name = "Enemy" },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "enemy", GlobalObjectId = "goid-A", Kind = "GameObject" },
                },
            };

            var anchors = new Dictionary<string, SourceSpan>
            {
                ["enemy"] = new SourceSpan(0, 10),
            };

            var result = Reconciler.Reconcile(model, snapshot, map, anchors);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendStatement>());
            Assert.Equal("enemy2", append.Handle);
            Assert.Equal("enemy2", append.NewLogicalId);
        }

        // THE FIX, half 3: a pre-existing hand-authored ambiguous group is HEALED — while the
        // positional mapping is still trustworthy (no reorder pending) — so the region does not stay
        // permanently unsyncable. The GlobalObjectId must follow the id, or it is stranded and the
        // next sync appends the object a second time.
        [Fact]
        public void Reconcile_PreExistingAmbiguousGroup_InjectsIdAndRekeysSidecar()
        {
            var enemy0 = new GameObjectNode { LogicalId = "Enemy/0", Name = "Enemy" };
            var enemy1 = new GameObjectNode { LogicalId = "Enemy/1", Name = "Enemy" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { enemy0, enemy1 } };

            // SAME order as the model — no reorder, so the positional mapping is still trustworthy.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-A", Name = "Enemy" },
                    new SnapshotNode { GlobalObjectId = "goid-B", Name = "Enemy" },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "Enemy/0", GlobalObjectId = "goid-A", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "Enemy/1", GlobalObjectId = "goid-B", Kind = "GameObject" },
                },
            };

            var anchors = new Dictionary<string, SourceSpan>
            {
                ["Enemy/0"] = new SourceSpan(0, 10),
                ["Enemy/1"] = new SourceSpan(20, 10),
            };

            var result = Reconciler.Reconcile(model, snapshot, map, anchors);

            // Exactly ONE injection: leaving one member positional is enough, because the (parent,
            // name) claim queue then holds a single id that its sole positional statement claims
            // wherever it sits.
            var inject = Assert.Single(result.Patch.Edits.OfType<IntroduceHandle>());
            Assert.Equal("Enemy/1", inject.Anchor);
            Assert.Equal("enemy", inject.Handle);

            // The re-key: goid-B must move to the derived handle, and the old id must be dropped.
            Assert.Contains(result.AddedEntries, e => e.LogicalId == "enemy" && e.GlobalObjectId == "goid-B");
            Assert.Contains("Enemy/1", result.RemovedLogicalIds);
        }

        // THE WHOLE LOOP, against real source: parse an ambiguous file -> reconcile -> APPLY the patch
        // -> re-parse. The re-parsed file must no longer be ambiguous, and the identity that was
        // implied by position must now be written down. Everything else in this file tests one stage;
        // this asserts the stages actually compose, which is where the fix would silently fail to be a
        // fix (e.g. an id the applier emits but the parser reads back differently).
        [Fact]
        public void Reconcile_ThenApply_HealsAmbiguousSourceIntoAnUnambiguousFile()
        {
            const string ambiguous = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Transform(pos: (0f, 0f, 0f));
        scene.Add(""Enemy"").Transform(pos: (5f, 0f, 0f));
    }
}
";
            var prior = new IdentityMap
            {
                Scene = "Assets/Scenes/Dup.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "Enemy/0", GlobalObjectId = "goid-A", Kind = "GameObject", Name = "Enemy", SiblingIndex = 0 },
                    new IdentityMapEntry { LogicalId = "Enemy/1", GlobalObjectId = "goid-B", Kind = "GameObject", Name = "Enemy", SiblingIndex = 1 },
                },
            };

            var parsed = BuilderParser.Parse(ambiguous, prior);
            Assert.NotEmpty(parsed.Ambiguities);

            // The scene, in the SAME order as the source — nothing moved, so the mapping is still
            // trustworthy and the group is healable.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-A", Name = "Enemy", Transform = new TransformData { Position = new Vec3(0f, 0f, 0f) } },
                    new SnapshotNode { GlobalObjectId = "goid-B", Name = "Enemy", Transform = new TransformData { Position = new Vec3(5f, 0f, 0f) } },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, prior, parsed.Anchors);
            var healed = SourcePatchApplier.Apply(ambiguous, result.Patch, parsed.Anchors);

            // The handle is IN the statement now.
            Assert.Contains("var enemy", healed);

            // And the file is no longer ambiguous — which is the entire deliverable.
            var reparsed = BuilderParser.Parse(healed, prior);
            Assert.Empty(reparsed.Ambiguities);

            // The healed file still describes the same two Enemies, one of which owns the Rigidbody.
            Assert.Equal(2, reparsed.Model.Roots.Length);
            Assert.Single(reparsed.Model.Roots, r => r.LogicalId == "enemy");
        }

        // THE FIX, half 2 — spec test 18: once a handle is introduced, a reorder of the
        // now-disambiguated pair preserves both identities. A `var` lives IN the statement, so a
        // statement move carries it, whereas a sibling index is only IMPLIED BY position and is
        // destroyed by the same move (contrast with Reorder_TwoSameNamedPositionalSiblings... above).
        [Fact]
        public void Reorder_HandleHeadedSameNamedSiblings_PreservesBothIdentities()
        {
            // The healed shape Reconcile_ThenApply_HealsAmbiguousSourceIntoAnUnambiguousFile
            // produces: statement 1 stays positional (Enemy/0), statement 2 heads the handle.
            const string healed = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Transform(pos: (0f, 0f, 0f));
        var enemy = scene.Add(""Enemy"").Transform(pos: (5f, 0f, 0f));
    }
}
";
            // The same two statements, SWAPPED.
            const string healedSwapped = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Add(""Enemy"").Transform(pos: (5f, 0f, 0f));
        scene.Add(""Enemy"").Transform(pos: (0f, 0f, 0f));
    }
}
";
            // The healed sidecar: prior minus RemovedLogicalIds "Enemy/1" plus AddedEntry enemy<->goid-B.
            var healedMap = new IdentityMap
            {
                Scene = "Assets/Scenes/Dup.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "Enemy/0", GlobalObjectId = "goid-A", Kind = "GameObject", Name = "Enemy", SiblingIndex = 0 },
                    new IdentityMapEntry { LogicalId = "enemy", GlobalObjectId = "goid-B", Kind = "GameObject", Name = "Enemy", SiblingIndex = 1 },
                },
            };

            Assert.Empty(BuilderParser.Parse(healed, healedMap).Ambiguities);

            var reparsed = BuilderParser.Parse(healedSwapped, healedMap);

            // The reorder did NOT re-introduce ambiguity.
            Assert.Empty(reparsed.Ambiguities);

            var remapped = IdentityRemapper.Remap(reparsed.Model, healedMap);

            // Each GlobalObjectId still maps to the SAME LogicalId it had before the reorder.
            Assert.Equal("goid-A", Assert.Single(remapped.Entries, e => e.Kind == "GameObject" && e.LogicalId == "Enemy/0").GlobalObjectId);
            Assert.Equal("goid-B", Assert.Single(remapped.Entries, e => e.Kind == "GameObject" && e.LogicalId == "enemy").GlobalObjectId);

            // The scene, in the SAME order as the swapped source.
            var reorderedSnapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-B", Name = "Enemy", Transform = new TransformData { Position = new Vec3(5f, 0f, 0f) } },
                    new SnapshotNode { GlobalObjectId = "goid-A", Name = "Enemy", Transform = new TransformData { Position = new Vec3(0f, 0f, 0f) } },
                },
            };

            var result = Reconciler.Reconcile(reparsed.Model, reorderedSnapshot, healedMap, reparsed.Anchors);

            // A pure reorder creates and destroys nothing.
            Assert.Empty(result.Patch.Edits.OfType<RemoveStatement>());
            Assert.Empty(result.Patch.Edits.OfType<AppendStatement>());
        }
    }
}
