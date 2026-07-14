using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // M2c (b2-t1): Reconciler lowers SetTag/SetLayer/SetActive/SetStatic diff ops into
    // Patch/Introduce/Remove flag SourceEdits, using flag-call PRESENCE (from BuilderParser's
    // FlagPresence) to distinguish "patch the existing argument" from "introduce the call".
    public class FlagReconcileTests
    {
        private static (ParseResult Parsed, IdentityMap Map) ParseWithSingleMappedRoot(string source, string logicalId, string goid)
        {
            var parsed = BuilderParser.Parse(source);
            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = logicalId, GlobalObjectId = goid, Kind = "GameObject" } },
            };

            return (parsed, map);
        }

        [Fact]
        public void Reconcile_ActiveChanged_PatchesExistingActiveArgument()
        {
            const string source = @"
public class Scene1 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var player = scene.Add(""Player"").Active(true);
    }
}
";
            var (parsed, map) = ParseWithSingleMappedRoot(source, "player", "goid-player");

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-player", Name = "Player", Active = false } },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, null, parsed.FlagPresence);

            Assert.Empty(result.Conflicts);
            var edit = Assert.Single(result.Patch.Edits);
            var patch = Assert.IsType<PatchFlagArgument>(edit);
            Assert.Equal("player", patch.Anchor);
            Assert.Equal(FlagKind.Active, patch.Flag);
            Assert.Equal("false", patch.NewExpr);
        }

        [Fact]
        public void Reconcile_ActiveDeactivated_IntroducesActiveFalse_WhenCallAbsent()
        {
            const string source = @"
public class Scene2 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var player = scene.Add(""Player"");
    }
}
";
            var (parsed, map) = ParseWithSingleMappedRoot(source, "player", "goid-player");

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-player", Name = "Player", Active = false } },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, null, parsed.FlagPresence);

            Assert.Empty(result.Conflicts);
            var edit = Assert.Single(result.Patch.Edits);
            var introduce = Assert.IsType<IntroduceFlagCall>(edit);
            Assert.Equal("player", introduce.Anchor);
            Assert.Equal(FlagKind.Active, introduce.Flag);
            Assert.Equal("false", introduce.ArgExpr);
        }

        [Fact]
        public void Reconcile_TagChanged_PatchesOrIntroducesTagCall()
        {
            const string presentSource = @"
public class Scene3 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Add(""Enemy"").Tag(""Neutral"");
    }
}
";
            var (presentParsed, presentMap) = ParseWithSingleMappedRoot(presentSource, "enemy", "goid-enemy");
            var presentSnapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-enemy", Name = "Enemy", Tag = "Hostile" } },
            };

            var presentResult = Reconciler.Reconcile(presentParsed.Model, presentSnapshot, presentMap, presentParsed.Anchors, null, presentParsed.FlagPresence);

            Assert.Empty(presentResult.Conflicts);
            var presentEdit = Assert.Single(presentResult.Patch.Edits);
            var patch = Assert.IsType<PatchFlagArgument>(presentEdit);
            Assert.Equal(FlagKind.Tag, patch.Flag);
            Assert.Equal("\"Hostile\"", patch.NewExpr);

            const string absentSource = @"
public class Scene4 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Add(""Enemy"");
    }
}
";
            var (absentParsed, absentMap) = ParseWithSingleMappedRoot(absentSource, "enemy", "goid-enemy");
            var absentSnapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-enemy", Name = "Enemy", Tag = "Hostile" } },
            };

            var absentResult = Reconciler.Reconcile(absentParsed.Model, absentSnapshot, absentMap, absentParsed.Anchors, null, absentParsed.FlagPresence);

            Assert.Empty(absentResult.Conflicts);
            var absentEdit = Assert.Single(absentResult.Patch.Edits);
            var introduce = Assert.IsType<IntroduceFlagCall>(absentEdit);
            Assert.Equal(FlagKind.Tag, introduce.Flag);
            Assert.Equal("\"Hostile\"", introduce.ArgExpr);
        }

        [Fact]
        public void Reconcile_LayerChanged_PatchesOrIntroducesLayerCall()
        {
            const string presentSource = @"
public class Scene5 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var obj = scene.Add(""Obj"").Layer(3);
    }
}
";
            var (presentParsed, presentMap) = ParseWithSingleMappedRoot(presentSource, "obj", "goid-obj");
            var presentSnapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-obj", Name = "Obj", Layer = 6 } },
            };

            var presentResult = Reconciler.Reconcile(presentParsed.Model, presentSnapshot, presentMap, presentParsed.Anchors, null, presentParsed.FlagPresence);

            Assert.Empty(presentResult.Conflicts);
            var presentEdit = Assert.Single(presentResult.Patch.Edits);
            var patch = Assert.IsType<PatchFlagArgument>(presentEdit);
            Assert.Equal(FlagKind.Layer, patch.Flag);
            Assert.Equal("6", patch.NewExpr);

            const string absentSource = @"
public class Scene6 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var obj = scene.Add(""Obj"");
    }
}
";
            var (absentParsed, absentMap) = ParseWithSingleMappedRoot(absentSource, "obj", "goid-obj");
            var absentSnapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-obj", Name = "Obj", Layer = 6 } },
            };

            var absentResult = Reconciler.Reconcile(absentParsed.Model, absentSnapshot, absentMap, absentParsed.Anchors, null, absentParsed.FlagPresence);

            Assert.Empty(absentResult.Conflicts);
            var absentEdit = Assert.Single(absentResult.Patch.Edits);
            var introduce = Assert.IsType<IntroduceFlagCall>(absentEdit);
            Assert.Equal(FlagKind.Layer, introduce.Flag);
            Assert.Equal("6", introduce.ArgExpr);
        }

        [Fact]
        public void Reconcile_StaticEnabled_IntroducesStaticCall_NoArg()
        {
            const string source = @"
public class Scene7 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var obj = scene.Add(""Obj"");
    }
}
";
            var (parsed, map) = ParseWithSingleMappedRoot(source, "obj", "goid-obj");

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-obj", Name = "Obj", IsStatic = true } },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, null, parsed.FlagPresence);

            Assert.Empty(result.Conflicts);
            var edit = Assert.Single(result.Patch.Edits);
            var introduce = Assert.IsType<IntroduceFlagCall>(edit);
            Assert.Equal(FlagKind.Static, introduce.Flag);
            Assert.Null(introduce.ArgExpr);
        }

        [Fact]
        public void Reconcile_StaticDisabled_RemovesStaticCall()
        {
            const string source = @"
public class Scene8 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var obj = scene.Add(""Obj"").Static();
    }
}
";
            var (parsed, map) = ParseWithSingleMappedRoot(source, "obj", "goid-obj");

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-obj", Name = "Obj", IsStatic = false } },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, null, parsed.FlagPresence);

            Assert.Empty(result.Conflicts);
            var edit = Assert.Single(result.Patch.Edits);
            var remove = Assert.IsType<RemoveFlagCall>(edit);
            Assert.Equal("obj", remove.Anchor);
            Assert.Equal(FlagKind.Static, remove.Flag);
        }

        [Fact]
        public void Reconcile_DefaultValueWithAbsentCall_EmitsNoEdit()
        {
            const string source = @"
public class Scene9 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var obj = scene.Add(""Obj"");
    }
}
";
            var (parsed, map) = ParseWithSingleMappedRoot(source, "obj", "goid-obj");

            // All-default snapshot, matching name too, so NO op fires at all for this node.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-obj", Name = "Obj" } },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, null, parsed.FlagPresence);

            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Patch.Edits);
        }

        [Fact]
        public void Reconcile_RedundantDefault_WithPresentCall_RemovesCall()
        {
            const string source = @"
public class Scene10 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var obj = scene.Add(""Obj"").Tag(""Enemy"");
    }
}
";
            var (parsed, map) = ParseWithSingleMappedRoot(source, "obj", "goid-obj");

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-obj", Name = "Obj", Tag = "Untagged" } },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, null, parsed.FlagPresence);

            Assert.Empty(result.Conflicts);
            var edit = Assert.Single(result.Patch.Edits);
            var remove = Assert.IsType<RemoveFlagCall>(edit);
            Assert.Equal(FlagKind.Tag, remove.Flag);
        }

        [Fact]
        public void Reconcile_MultipleFlagsOneObject_EmitsIndependentSpanLocalEdits()
        {
            const string source = @"
public class Scene11 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var obj = scene.Add(""Obj"").Tag(""Enemy"").Layer(3).Active(true);
    }
}
";
            var (parsed, map) = ParseWithSingleMappedRoot(source, "obj", "goid-obj");

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-obj", Name = "Obj", Tag = "Hostile", Layer = 6, Active = false } },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, null, parsed.FlagPresence);

            Assert.Empty(result.Conflicts);
            Assert.Equal(3, result.Patch.Edits.Length);
            Assert.All(result.Patch.Edits, e => Assert.Equal("obj", e.Anchor));

            var tagEdit = Assert.Single(result.Patch.Edits.OfType<PatchFlagArgument>().Where(p => p.Flag == FlagKind.Tag));
            Assert.Equal("\"Hostile\"", tagEdit.NewExpr);

            var layerEdit = Assert.Single(result.Patch.Edits.OfType<PatchFlagArgument>().Where(p => p.Flag == FlagKind.Layer));
            Assert.Equal("6", layerEdit.NewExpr);

            var activeEdit = Assert.Single(result.Patch.Edits.OfType<PatchFlagArgument>().Where(p => p.Flag == FlagKind.Active));
            Assert.Equal("false", activeEdit.NewExpr);
        }

        [Fact]
        public void Reconcile_FlagEditWithNoSourceAnchor_SurfacesLocatedConflict()
        {
            const string source = @"
public class Scene12 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var obj = scene.Add(""Obj"");
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = "obj", GlobalObjectId = "goid-obj", Kind = "GameObject" } },
            };

            // Deliberately empty: "obj" has no source anchor, unlike the M2 case which reuses
            // the same missing-anchor path.
            var anchors = new Dictionary<string, SourceSpan>();

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-obj", Name = "Obj", Active = false } },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, anchors, null, parsed.FlagPresence);

            var conflict = Assert.Single(result.Conflicts, c => c.Kind == ConflictKind.MissingSourceAnchor);
            Assert.Equal("obj", conflict.LogicalId);
            Assert.Equal("goid-obj", conflict.GlobalObjectId);

            Assert.DoesNotContain(result.Patch.Edits, e => e.Anchor == "obj");
        }

        [Fact]
        public void Reconcile_NoFlagPresence_EmitsNoFlagEdits()
        {
            const string source = @"
public class Scene13 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var obj = scene.Add(""Obj"");
    }
}
";
            var (parsed, map) = ParseWithSingleMappedRoot(source, "obj", "goid-obj");

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-obj", Name = "Obj", Active = false } },
            };

            // 4-arg back-compat call: no flagPresence supplied -> flag ops must still be dropped,
            // exactly like pre-M2c behavior (no regression for existing non-opted-in callers).
            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            Assert.Empty(result.Patch.Edits.OfType<PatchFlagArgument>());
            Assert.Empty(result.Patch.Edits.OfType<IntroduceFlagCall>());
            Assert.Empty(result.Patch.Edits.OfType<RemoveFlagCall>());
        }
    }
}
