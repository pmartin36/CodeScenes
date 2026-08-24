using System;
using System.Linq;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;
using static SceneBuilder.Core.Tests.SourcePatchTestHelpers;

namespace SceneBuilder.Core.Tests
{
    // Spec 43 gave a MOVED declaration a floor (it can't be seated above a handle it calls) but no
    // CEILING (nothing hoists it back above an external referrer when the move itself pushes the
    // declaration below that referrer). Both placement edit kinds that can move a declaration --
    // ReorderStatement and MoveStatement (reparent) -- must inherit the same ceiling the introduce
    // fold already has, so neither one can emit a `var` used before it is declared.
    public class DeclareBeforeUseGuardTests
    {
        private const string LinkerStub = @"
public class Linker : UnityEngine.MonoBehaviour { public UnityEngine.GameObject target; }
";

        private const string DoorOpenerScene = @"using SceneBuilder.Authoring;
public class DoorOpenerScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var opener = scene.Add(""Opener"");
        opener.Component<Linker>(c => c.Set(""target"", door));
    }
}
";

        // A scene-side sibling drag of Door below Opener reconciles to a ReorderStatement that
        // relocates `var door` past the component statement that names it as an argument. The
        // component's own peer seat isn't in `door`'s dependent group (it is chained off `opener`,
        // not `door`), so relocating `door` alone must not leave that component referencing it
        // before its declaration.
        [Fact]
        public void Apply_ReorderDeclarationBelowItsExternalReferrer_HoistsDeclarationAboveReferrer()
        {
            var parsed = BuilderParser.Parse(DoorOpenerScene);
            var anchors = MergeAnchors(parsed);

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new ReorderStatement { Anchor = "door", NewSiblingIndex = 1 } },
            };

            var result = SourcePatchApplier.Apply(DoorOpenerScene, patch, anchors);

            AssertNoForwardLocalReference(result);

            var errors = AuthoringBindHarness.BindErrors(result, LinkerStub);
            Assert.Empty(errors);

            Assert.True(
                result.IndexOf("var door = ", StringComparison.Ordinal)
                    < result.IndexOf("c.Set(\"target\", door)", StringComparison.Ordinal),
                "A reordered declaration must not end up used before it is declared.\n" + result);
        }

        private const string DoorLobbyOpenerScene = @"using SceneBuilder.Authoring;
public class DoorLobbyOpenerScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var lobby = scene.Add(""Lobby"");
        var opener = lobby.Add(""Opener"");
        opener.Component<Linker>(c => c.Set(""target"", door));
    }
}
";

        // A reparent (MoveStatement) is the other edit kind that relocates a declaration through the
        // same placement path. Reparenting Door under Lobby, after Opener, seats `var door` past the
        // component that references it -- the reparent twin of the reorder case above.
        [Fact]
        public void Apply_ReparentDeclarationBelowItsExternalReferrer_HoistsDeclarationAboveReferrer()
        {
            var parsed = BuilderParser.Parse(DoorLobbyOpenerScene);
            var anchors = MergeAnchors(parsed);

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new MoveStatement { Anchor = "door", NewParentAnchor = "lobby", NewSiblingIndex = 1 },
                },
            };

            var result = SourcePatchApplier.Apply(DoorLobbyOpenerScene, patch, anchors);

            AssertNoForwardLocalReference(result);

            var errors = AuthoringBindHarness.BindErrors(result, LinkerStub);
            Assert.Empty(errors);

            Assert.True(
                result.IndexOf("var door = ", StringComparison.Ordinal)
                    < result.IndexOf("c.Set(\"target\", door)", StringComparison.Ordinal),
                "A reparented declaration must not end up used before it is declared.\n" + result);
        }

        private const string NoReferrerScene = @"using SceneBuilder.Authoring;
public class NoReferrerScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var alpha = scene.Add(""Alpha"");
        var beta = scene.Add(""Beta"");
    }
}
";

        // Neither declaration is referenced by another statement's argument, so the new hoist repair
        // has nothing to do -- a reorder among un-referenced peers is unaffected by the ceiling.
        [Fact]
        public void Apply_ReorderDeclarationWithNoExternalReferrer_PlacesAtRequestedSibling()
        {
            var parsed = BuilderParser.Parse(NoReferrerScene);
            var anchors = MergeAnchors(parsed);

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new ReorderStatement { Anchor = "beta", NewSiblingIndex = 0 } },
            };

            var result = SourcePatchApplier.Apply(NoReferrerScene, patch, anchors);

            AssertNoForwardLocalReference(result);
            Assert.True(
                result.IndexOf("var beta = ", StringComparison.Ordinal)
                    < result.IndexOf("var alpha = ", StringComparison.Ordinal),
                "An un-referenced declaration reorders to the requested sibling seat.\n" + result);
        }
    }
}
