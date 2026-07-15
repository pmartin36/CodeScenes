using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;
using static SceneBuilder.Core.Tests.SourcePatchTestHelpers;

namespace SceneBuilder.Core.Tests
{
    public class SourcePatchPlacementTests
    {

        // Beta is authored as a root, then reparented under alpha AND reordered to sibling index 0.
        // A scene-graph sibling index is not a C# block index: re-seating Beta at block index 0 puts
        // `alpha.Add("Beta");` ahead of `var alpha = ...` — CS0841, use before declaration.
        private const string ReparentReorderFixture = @"
public class ReparentReorderScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var alpha = scene.Add(""Alpha"");
        scene.Add(""Beta"");
    }
}
";

        [Fact]
        public void Apply_ReorderToFirstSibling_DoesNotOutrunItsReceiverDeclaration()
        {
            var parsed = BuilderParser.Parse(ReparentReorderFixture);
            var anchors = MergeAnchors(parsed);
            var betaAnchor = anchors.Keys.Single(k => k.Contains("Beta"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new MoveStatement { Anchor = betaAnchor, NewParentAnchor = "alpha" },
                    new ReorderStatement { Anchor = betaAnchor, NewSiblingIndex = 0 },
                },
            };

            var result = SourcePatchApplier.Apply(ReparentReorderFixture, patch, anchors);

            // Reparented onto alpha's handle...
            Assert.Contains("alpha.Add(\"Beta\")", result);

            // ...but never ahead of the declaration of the handle it calls.
            Assert.True(
                result.IndexOf("var alpha = scene.Add(\"Alpha\")", StringComparison.Ordinal)
                    < result.IndexOf("alpha.Add(\"Beta\")", StringComparison.Ordinal),
                "Reordered statement must not precede the declaration of its receiver handle.\n" + result);
        }

        // ---- Statement placement: the three fuzz-found defects ---------------------------------
        //
        // Each of these reproduces a seed the SyncFuzzTests harness quarantined. They are pinned here
        // as well as in the (now unquarantined) EditMode fuzz seeds because these assert the EXACT
        // emitted text at the layer the defect lived in, which the fuzzer's invariants cannot.

        // Gamma/Delta is authored WITHOUT a `var`, so there is no receiver to reparent onto.
        private const string HandlelessParentFixture = @"
public class HandlelessParentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var alpha = scene.Add(""Alpha"");
        var gamma = scene.Add(""Gamma"");
        gamma.Add(""Delta"");
    }
}
";

        // BUG A (fuzz seed 11, step 0). Sync threw PatchException "New parent anchor
        // 'gamma/Delta/0' has no handle variable; reparent is not expressible" — a reparent onto any
        // handle-less object was a hard sync failure.
        [Fact]
        public void Apply_MoveOntoHandlelessParent_IntroducesTheParentHandle()
        {
            var parsed = BuilderParser.Parse(HandlelessParentFixture);
            var anchors = MergeAnchors(parsed);
            var deltaAnchor = anchors.Keys.Single(k => k.Contains("Delta"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new MoveStatement
                    {
                        Anchor = "alpha",
                        NewParentAnchor = deltaAnchor,
                        NewParentHandle = "delta",
                        IntroduceNewParentHandle = true,
                        NewSiblingIndex = 0,
                    },
                },
            };

            var result = SourcePatchApplier.Apply(HandlelessParentFixture, patch, anchors);

            // The handle-less parent now heads a handle, and Alpha is reparented onto it.
            Assert.Contains("var delta = gamma.Add(\"Delta\");", result);
            Assert.Contains("delta.Add(\"Alpha\")", result);

            var reparsed = BuilderParser.Parse(result);
            var gamma = Assert.Single(reparsed.Model.Roots, r => r.Name == "Gamma");
            var delta = Assert.Single(gamma.Children);
            Assert.Equal("Delta", delta.Name);
            Assert.Equal("Alpha", Assert.Single(delta.Children).Name);
        }

        private const string SiblingPlacementFixture = @"
public class SiblingPlacementScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var gamma = scene.Add(""Gamma"");
        gamma.Add(""Delta"");
    }
}
";

        // BUG B (fuzz seed 20, step 0). A created child was inserted immediately after its parent's
        // statement — i.e. at sibling index 0 — whatever the scene said. The emission parsed to the
        // wrong order and the NEXT sync silently re-Reordered it: the file churned twice per edit and
        // sync never converged.
        [Theory]
        [InlineData(0, new[] { "Fuzz0", "Delta" })]
        [InlineData(1, new[] { "Delta", "Fuzz0" })]
        public void Apply_AppendUnderMappedParent_HonoursTheTargetSiblingIndex(int siblingIndex, string[] expectedOrder)
        {
            var parsed = BuilderParser.Parse(SiblingPlacementFixture);
            var anchors = MergeAnchors(parsed);

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendStatement
                    {
                        NewLogicalId = "gamma/Fuzz0/" + siblingIndex,
                        ParentAnchor = "gamma",
                        ParentHandle = "gamma",
                        NewSiblingIndex = siblingIndex,
                        Name = "Fuzz0",
                    },
                },
            };

            var result = SourcePatchApplier.Apply(SiblingPlacementFixture, patch, anchors);

            var reparsed = BuilderParser.Parse(result);
            var gamma = Assert.Single(reparsed.Model.Roots, r => r.Name == "Gamma");
            Assert.Equal(expectedOrder, gamma.Children.Select(c => c.Name).ToArray());
        }

        // Delta heads a handle that a LATER statement calls — the shape a move must not break.
        private const string MoveToRootFixture = @"
public class MoveToRootScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var gamma = scene.Add(""Gamma"");
        var delta = gamma.Add(""Delta"");
        delta.Add(""Fuzz1"");
    }
}
";

        // BUG C (fuzz seed 17). Move-to-root appended the moved statement to the END of the block,
        // leaving `var delta = scene.Add("Delta");` BELOW the `delta.Add("Fuzz1");` that calls it —
        // use-before-declaration (ParseException "Unknown receiver 'delta'"; CS0841 to the compiler).
        [Fact]
        public void Apply_MoveDeclarationToRoot_KeepsItAboveTheStatementsThatCallIt()
        {
            var parsed = BuilderParser.Parse(MoveToRootFixture);
            var anchors = MergeAnchors(parsed);

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    // Roots after the move: Gamma at 0, Delta at 1.
                    new MoveStatement { Anchor = "delta", NewParentAnchor = null, NewSiblingIndex = 1 },
                },
            };

            var result = SourcePatchApplier.Apply(MoveToRootFixture, patch, anchors);

            Assert.Contains("var delta = scene.Add(\"Delta\");", result);
            Assert.True(
                result.IndexOf("var delta = scene.Add(\"Delta\")", StringComparison.Ordinal)
                    < result.IndexOf("delta.Add(\"Fuzz1\")", StringComparison.Ordinal),
                "A moved declaration must stay above its own users.\n" + result);

            // Parses (it did not before), and the dependent child travelled with the declaration
            // rather than being stranded or re-seated under the old parent.
            var reparsed = BuilderParser.Parse(result);
            Assert.Equal(new[] { "Gamma", "Delta" }, reparsed.Model.Roots.Select(r => r.Name).ToArray());
            var delta = reparsed.Model.Roots[1];
            Assert.Equal("Fuzz1", Assert.Single(delta.Children).Name);
            Assert.Empty(reparsed.Model.Roots[0].Children);
        }

        // The two mechanisms meeting: one sync both reparents onto a handle-less object AND attaches
        // a component to it. Introduced per-resolver, the second rewrite finds a declaration already
        // there and hard-fails; this is what the single introduction pre-pass exists to prevent.
        [Fact]
        public void Apply_MoveAndComponentOntoSameHandlelessParent_IntroducesOneHandleOnce()
        {
            var parsed = BuilderParser.Parse(HandlelessParentFixture);
            var anchors = MergeAnchors(parsed);
            var deltaAnchor = anchors.Keys.Single(k => k.Contains("Delta"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new MoveStatement
                    {
                        Anchor = "alpha",
                        NewParentAnchor = deltaAnchor,
                        NewParentHandle = "delta",
                        IntroduceNewParentHandle = true,
                        NewSiblingIndex = 0,
                    },
                    new AppendComponentStatement
                    {
                        Anchor = deltaAnchor,
                        ComponentLogicalId = "delta/UnityEngine.Rigidbody#0",
                        TypeFullName = "UnityEngine.Rigidbody",
                        OwnerHandle = "delta",
                        IntroduceOwnerHandle = true,
                    },
                },
            };

            var result = SourcePatchApplier.Apply(HandlelessParentFixture, patch, anchors);

            // Exactly one declaration of the handle, serving both edits.
            Assert.Equal(1, CountOccurrences(result, "var delta = "));
            Assert.Contains("delta.Component<UnityEngine.Rigidbody>()", result);
            Assert.Contains("delta.Add(\"Alpha\")", result);

            var reparsed = BuilderParser.Parse(result);
            var gamma = Assert.Single(reparsed.Model.Roots, r => r.Name == "Gamma");
            var delta = Assert.Single(gamma.Children);
            Assert.Equal("Alpha", Assert.Single(delta.Children).Name);
            Assert.Equal("UnityEngine.Rigidbody", Assert.Single(delta.Components).Type.FullName);
        }

        private const string ExistingComponentFixture = @"
public class ExistingComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var crate = scene.Add(""Crate"");
        crate.Component<UnityEngine.Rigidbody>();
    }
}
";

        // The component-list instance of BUG B (fuzz seeds 2 and 19). A component appended to an owner
        // that ALREADY has one was inserted immediately after the owner — i.e. at component index 0 —
        // so it landed AHEAD of the existing component and the next sync re-Reordered it. It went
        // unnoticed because a stale sidecar was suppressing that reorder pass.
        [Theory]
        [InlineData(0, new[] { "UnityEngine.BoxCollider", "UnityEngine.Rigidbody" })]
        [InlineData(1, new[] { "UnityEngine.Rigidbody", "UnityEngine.BoxCollider" })]
        public void Apply_AppendComponent_HonoursTheTargetSiblingIndex(int siblingIndex, string[] expectedOrder)
        {
            var parsed = BuilderParser.Parse(ExistingComponentFixture);
            var anchors = MergeAnchors(parsed);

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendComponentStatement
                    {
                        Anchor = "crate",
                        ComponentLogicalId = "crate/UnityEngine.BoxCollider#0",
                        TypeFullName = "UnityEngine.BoxCollider",
                        OwnerHandle = "crate",
                        NewSiblingIndex = siblingIndex,
                    },
                },
            };

            var result = SourcePatchApplier.Apply(ExistingComponentFixture, patch, anchors);

            var reparsed = BuilderParser.Parse(result);
            var crate = Assert.Single(reparsed.Model.Roots);
            Assert.Equal(expectedOrder, crate.Components.Select(c => c.Type.FullName).ToArray());
        }

        private const string HandlelessWithTransformFixture = @"
public class HandlelessWithTransformScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var gamma = scene.Add(""Gamma"");
        gamma.Add(""Delta"").Transform(rot: (4.5f, 33f, 61f));
    }
}
";

        // Found by the 200x40 soak (seed 7, step 27). Introducing a handle on a statement while ANOTHER
        // edit in the same batch patches an argument INSIDE that same statement threw
        // NullReferenceException: the introduction re-parsed the statement from text, so the patch's
        // tracked node no longer existed in the tree.
        [Fact]
        public void Apply_IntroduceHandleWhilePatchingAnArgumentOfTheSameStatement_KeepsBothEdits()
        {
            var parsed = BuilderParser.Parse(HandlelessWithTransformFixture);
            var anchors = MergeAnchors(parsed);
            var deltaAnchor = anchors.Keys.Single(k => k.Contains("Delta"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    // Delta gains a component (so it must gain a handle)...
                    new AppendComponentStatement
                    {
                        Anchor = deltaAnchor,
                        ComponentLogicalId = "delta/UnityEngine.Rigidbody#0",
                        TypeFullName = "UnityEngine.Rigidbody",
                        OwnerHandle = "delta",
                        IntroduceOwnerHandle = true,
                        NewSiblingIndex = 0,
                    },
                    // ...while its rotation is patched in the SAME sync.
                    new PatchArgument { Anchor = deltaAnchor, ArgName = "rot", NewExpr = "(1f, 2f, 3f)" },
                },
            };

            var result = SourcePatchApplier.Apply(HandlelessWithTransformFixture, patch, anchors);

            Assert.Contains("var delta = gamma.Add(\"Delta\").Transform(rot: (1f, 2f, 3f));", result);
            Assert.Contains("delta.Component<UnityEngine.Rigidbody>()", result);

            var reparsed = BuilderParser.Parse(result);
            var gamma = Assert.Single(reparsed.Model.Roots);
            var delta = Assert.Single(gamma.Children);
            Assert.Equal("UnityEngine.Rigidbody", Assert.Single(delta.Components).Type.FullName);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }
}
