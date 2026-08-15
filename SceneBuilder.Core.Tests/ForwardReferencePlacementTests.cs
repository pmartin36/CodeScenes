using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;
using static SceneBuilder.Core.Tests.SourcePatchTestHelpers;

namespace SceneBuilder.Core.Tests
{
    // The placement floor must consider every in-block local a placed statement names -- its
    // RECEIVER (already covered by SourcePatchPlacementTests) AND its ARGUMENTS. A component
    // statement carrying a reference to a later-declared local must never be seated above that
    // local's declaration.
    public class ForwardReferencePlacementTests
    {
        private const string LinkerStub = @"
public class Linker : UnityEngine.MonoBehaviour { public UnityEngine.GameObject target; }
";

        private const string OpenerDoorScene = @"using SceneBuilder.Authoring;
public class OpenerDoorScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Opener"");
        var door = scene.Add(""Door"");
        opener.Component<Linker>(c => c.Set(""target"", door));
    }
}
";

        // A statement's peer-seat alone (receiver-only floor) would re-insert this component
        // between `opener` and `door` -- above the declaration of the handle its argument names.
        [Fact]
        public void Apply_ReorderCausesForwardReference_SeatsComponentAfterReferencedHandleDeclaration()
        {
            var parsed = BuilderParser.Parse(OpenerDoorScene);
            var anchors = MergeAnchors(parsed);
            var linkerAnchor = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith("Linker#0"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new ReorderStatement { Anchor = linkerAnchor, NewSiblingIndex = 0 } },
            };

            var result = SourcePatchApplier.Apply(OpenerDoorScene, patch, anchors);

            AssertNoForwardLocalReference(result);

            var errors = AuthoringBindHarness.BindErrors(result, LinkerStub);
            Assert.Empty(errors);
        }

        private const string NoReferenceComponentScene = @"
public class NoReferenceComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Opener"");
        var door = scene.Add(""Door"");
        opener.Component<UnityEngine.Rigidbody>();
    }
}
";

        // A component whose only extra identifiers are non-local (a type name) must land at
        // exactly its pre-existing receiver-only floor -- the argument scan contributes nothing
        // when it names nothing local.
        [Fact]
        public void Apply_ReorderComponentWithNoReferenceArgument_KeepsReceiverOnlyPlacement()
        {
            var parsed = BuilderParser.Parse(NoReferenceComponentScene);
            var anchors = MergeAnchors(parsed);
            var rigidbodyAnchor = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith("UnityEngine.Rigidbody#0"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new ReorderStatement { Anchor = rigidbodyAnchor, NewSiblingIndex = 0 } },
            };

            var result = SourcePatchApplier.Apply(NoReferenceComponentScene, patch, anchors);

            Assert.True(
                result.IndexOf("opener.Component<UnityEngine.Rigidbody>()", StringComparison.Ordinal)
                    < result.IndexOf("var door = scene.Add(\"Door\")", StringComparison.Ordinal),
                "A component with no reference argument must be seated right after its receiver's declaration, unaffected by the forward-reference floor.\n" + result);
        }

        private const string BackwardReferenceScene = @"
public class BackwardReferenceScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var opener = scene.Add(""Opener"");
        opener.Component<Linker>(c => c.Set(""target"", door));
    }
}
";

        // A component referencing an EARLIER-declared handle already satisfies declare-before-use;
        // the floor must only ever RAISE a placement, never perturb source that is already legal.
        [Fact]
        public void Apply_ReorderComponentReferencingEarlierDeclaredHandle_LeavesSourceUnchanged()
        {
            var parsed = BuilderParser.Parse(BackwardReferenceScene);
            var anchors = MergeAnchors(parsed);
            var linkerAnchor = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith("Linker#0"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new ReorderStatement { Anchor = linkerAnchor, NewSiblingIndex = 0 } },
            };

            var result = SourcePatchApplier.Apply(BackwardReferenceScene, patch, anchors);

            Assert.Equal(BackwardReferenceScene, result);
        }

        // Validates the oracle itself: an independent re-implementation of the declare-before-use
        // scan (deliberately NOT calling into the production floor), generalizing the self-reference
        // case (decl index == statement index) to the forward case (decl index > statement index).
        [Fact]
        public void AssertNoForwardLocalReference_ThrowsOnForwardReference_PassesOnCorrectedOrder()
        {
            const string forward = @"
public class ForwardScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Opener"");
        opener.Component<Linker>(c => c.Set(""target"", door));
        var door = scene.Add(""Door"");
    }
}
";
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoForwardLocalReference(forward));

            const string corrected = @"
public class ForwardScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Opener"");
        var door = scene.Add(""Door"");
        opener.Component<Linker>(c => c.Set(""target"", door));
    }
}
";
            AssertNoForwardLocalReference(corrected);
        }

        // For every block, for every statement at index i, every identifier it uses that matches an
        // in-block local declaration must be declared strictly before i. Independent of the
        // production floor (StatementPlacement.cs) so it is a real oracle, not a tautology.
        private static void AssertNoForwardLocalReference(string source)
        {
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();

            foreach (var block in root.DescendantNodes().OfType<BlockSyntax>())
            {
                var statements = block.Statements;
                var declIndex = new Dictionary<string, int>();
                for (var i = 0; i < statements.Count; i++)
                {
                    if (statements[i] is LocalDeclarationStatementSyntax local)
                    {
                        foreach (var variable in local.Declaration.Variables)
                        {
                            declIndex[variable.Identifier.Text] = i;
                        }
                    }
                }

                for (var i = 0; i < statements.Count; i++)
                {
                    foreach (var id in statements[i].DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                    {
                        if (declIndex.TryGetValue(id.Identifier.Text, out var declaredAt))
                        {
                            Assert.True(
                                declaredAt < i,
                                $"'{id.Identifier.Text}' used at statement {i} before its declaration at {declaredAt}: {statements[i]}");
                        }
                    }
                }
            }
        }
    }
}
