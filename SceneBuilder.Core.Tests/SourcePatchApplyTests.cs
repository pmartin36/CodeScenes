using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using SceneBuilder.Core.Tests.Fixtures;
using Xunit;
using static SceneBuilder.Core.Tests.SourcePatchTestHelpers;

namespace SceneBuilder.Core.Tests
{
    public class SourcePatchApplyTests
    {
        // Three isolated groups so PatchArgument/MoveStatement/ReorderStatement don't interact
        // positionally: root1/childA (transform patch), root2/ChildB (reparent into root1),
        // root3's closure (own BlockSyntax, so ChildX/ChildY reorder is unambiguous).
        private const string PatchFixture = @"
public class PatchScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Root one holds a transformed child.
        var root1 = scene.Add(""Root1"");
        var childA = root1.Add(""ChildA"").Transform(pos: (1, 2, 3));

        // Root two holds a child that gets reparented.
        var root2 = scene.Add(""Root2"");
        root2.Add(""ChildB"");

        // Root three holds two children that swap order.
        scene.Add(""Root3"", r3 =>
        {
            r3.Add(""ChildX"");
            r3.Add(""ChildY"");
        });
    }
}
";

        [Fact]
        public void SourcePatch_Apply_PreservesUnrelatedFormattingAndComments()
        {
            var source = PatchFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchArgument { Anchor = "childA", ArgName = "pos", NewExpr = "(9, 9, 9)" },
                    new MoveStatement { Anchor = "root2/ChildB/0", NewParentAnchor = "root1" },
                    new ReorderStatement { Anchor = "Root3/2/ChildY/1", NewSiblingIndex = 0 },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Comments survive untouched.
            Assert.Contains("// Root one holds a transformed child.", result);
            Assert.Contains("// Root two holds a child that gets reparented.", result);
            Assert.Contains("// Root three holds two children that swap order.", result);

            // Statements untouched by any edit are byte-identical, including indentation.
            Assert.Contains("        var root1 = scene.Add(\"Root1\");", result);
            Assert.Contains("        var root2 = scene.Add(\"Root2\");", result);

            // PatchArgument: only the targeted Transform arg changed.
            Assert.Contains("pos: (9, 9, 9)", result);
            Assert.DoesNotContain("pos: (1, 2, 3)", result);

            // MoveStatement: ChildB relocated under root1, receiver rewritten to the new parent handle.
            Assert.Contains("root1.Add(\"ChildB\")", result);
            Assert.DoesNotContain("root2.Add(\"ChildB\")", result);

            // ReorderStatement: ChildY now precedes ChildX within root3's closure block.
            Assert.True(
                result.IndexOf("ChildY", StringComparison.Ordinal) < result.IndexOf("ChildX", StringComparison.Ordinal),
                "Expected ChildY to precede ChildX after reorder.");
        }

        [Fact]
        public void Apply_RenameNameArg_ReplacesOnlyLiteral()
        {
            const string source = @"
public class RenameScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""OldName"");
    }
}
";
            var anchors = BuilderParser.Parse(source).Anchors;
            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchArgument { Anchor = "root", ArgName = "name", NewExpr = "\"NewName\"" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Only the name literal changes; every other byte (including quotes/whitespace) is identical.
            Assert.Equal(source.Replace("OldName", "NewName"), result);
        }

        // Neighbors each own a comment as their leading trivia; the target's own comment is
        // its leading trivia too. Blank line before the target belongs to the target's
        // leading trivia (removed with it); blank line after the target belongs to the
        // "below" neighbor's leading trivia (preserved).
        private const string RemoveFixture = @"
public class RemoveScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Neighbor above, with its own comment.
        var above = scene.Add(""Above"");

        // Target statement to be removed.
        var target = scene.Add(""Target"");

        // Neighbor below, with its own comment.
        var below = scene.Add(""Below"");
    }
}
";

        [Fact]
        public void Remove_DeletesStatement_PreservingSurroundingFormatting()
        {
            var source = RemoveFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new RemoveStatement { Anchor = "target" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Target statement and its own comment are gone entirely.
            Assert.DoesNotContain("Target", result);
            Assert.DoesNotContain("// Target statement to be removed.", result);

            // Neighbors are byte-identical, including indentation and their own comments.
            Assert.Contains("        // Neighbor above, with its own comment.\n        var above = scene.Add(\"Above\");", result);
            Assert.Contains("        // Neighbor below, with its own comment.\n        var below = scene.Add(\"Below\");", result);

            // Exactly one blank line survives between the neighbors (the target's own
            // leading blank line is removed with it; the "below" neighbor's leading
            // blank line is untouched) — proves neighbor trivia was not double-preserved
            // or accidentally collapsed.
            Assert.Contains(
                "        var above = scene.Add(\"Above\");\n\n        // Neighbor below, with its own comment.",
                result);

            // Result re-parses cleanly: no orphaned/malformed trivia broke the syntax.
            var reparsed = BuilderParser.Parse(result);
            Assert.True(reparsed.Anchors.ContainsKey("above"));
            Assert.True(reparsed.Anchors.ContainsKey("below"));
            Assert.False(reparsed.Anchors.ContainsKey("target"));
        }

        [Fact]
        public void Remove_MissingAnchor_ThrowsLocatedPatchException()
        {
            var source = BuilderFixtures.BareAdd;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new RemoveStatement { Anchor = "ghost" },
                },
            };

            var ex = Assert.Throws<PatchException>(() => SourcePatchApplier.Apply(source, patch, anchors));

            Assert.Contains("ghost", ex.Message);
            Assert.True(ex.Line >= 0);
            Assert.True(ex.Column >= 0);
        }

        [Fact]
        public void Apply_MissingAnchor_ThrowsLocatedPatchException()
        {
            var source = BuilderFixtures.BareAdd;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchArgument { Anchor = "ghost", ArgName = "name", NewExpr = "\"X\"" },
                },
            };

            var ex = Assert.Throws<PatchException>(() => SourcePatchApplier.Apply(source, patch, anchors));

            Assert.Contains("ghost", ex.Message);
            Assert.True(ex.Line >= 0);
            Assert.True(ex.Column >= 0);
        }

        // ---- AppendComponentStatement (b3-t1) ------------------------------------------------

        private const string AppendComponentFixture = @"
public class AppendComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Existing crate, untouched otherwise.
        var crate = scene.Add(""Crate"");

        // Unrelated sibling below, must stay untouched.
        var sibling = scene.Add(""Sibling"");
    }
}
";

        [Fact]
        public void Apply_AppendComponent_ExistingHandleOwner_InsertsComponentStatementAfterOwner()
        {
            var source = AppendComponentFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendComponentStatement
                    {
                        Anchor = "crate",
                        ComponentLogicalId = "crate/UnityEngine.Rigidbody#0",
                        TypeFullName = "UnityEngine.Rigidbody",
                        Fields = new FieldMap(new[]
                        {
                            new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(7f)),
                        }),
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Unrelated statement + its comment are byte-identical.
            Assert.Contains(
                "        // Unrelated sibling below, must stay untouched.\n        var sibling = scene.Add(\"Sibling\");",
                result);

            // Component call + f-suffixed field value present.
            Assert.Contains("crate.Component<UnityEngine.Rigidbody>", result);
            Assert.Contains(".Set(\"m_Mass\", 7f)", result);

            // Inserted strictly between the owner statement and the next sibling.
            var ownerIndex = result.IndexOf("var crate = scene.Add(\"Crate\");", StringComparison.Ordinal);
            var componentIndex = result.IndexOf("crate.Component<UnityEngine.Rigidbody>", StringComparison.Ordinal);
            var siblingIndex = result.IndexOf("var sibling = scene.Add(\"Sibling\");", StringComparison.Ordinal);
            Assert.True(ownerIndex < componentIndex && componentIndex < siblingIndex,
                "Expected the component statement immediately after the owner and before the next sibling.");

            // Re-parses to the intended component on the owner, with the LogicalId the Reconciler predicted.
            var reparsed = BuilderParser.Parse(result);
            var crateNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crateNode.Components);
            Assert.Equal("UnityEngine.Rigidbody", component.Type.FullName);
            Assert.Equal("crate/UnityEngine.Rigidbody#0", component.LogicalId);
            Assert.Equal(ValueNode.Primitive.Float(7f), component.Fields["m_Mass"]);
        }

        [Fact]
        public void Apply_AppendComponent_MultipleFields_ReparsesBothInOrder()
        {
            var source = AppendComponentFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendComponentStatement
                    {
                        Anchor = "crate",
                        ComponentLogicalId = "crate/UnityEngine.Rigidbody#0",
                        TypeFullName = "UnityEngine.Rigidbody",
                        Fields = new FieldMap(new[]
                        {
                            new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(7f)),
                            new KeyValuePair<string, ValueNode>("m_Drag", ValueNode.Primitive.Float(0.5f)),
                        }),
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            var reparsed = BuilderParser.Parse(result);
            var crateNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crateNode.Components);
            Assert.Equal(2, component.Fields.Count);
            Assert.Equal("m_Mass", component.Fields[0].Key);
            Assert.Equal("m_Drag", component.Fields[1].Key);
            Assert.Equal(ValueNode.Primitive.Float(7f), component.Fields["m_Mass"]);
            Assert.Equal(ValueNode.Primitive.Float(0.5f), component.Fields["m_Drag"]);
        }

        [Fact]
        public void Apply_AppendComponent_NoFields_EmitsFieldlessCall()
        {
            var source = AppendComponentFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendComponentStatement
                    {
                        Anchor = "crate",
                        ComponentLogicalId = "crate/UnityEngine.Rigidbody#0",
                        TypeFullName = "UnityEngine.Rigidbody",
                        Fields = FieldMap.Empty,
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains("crate.Component<UnityEngine.Rigidbody>();", result);

            var reparsed = BuilderParser.Parse(result);
            var crateNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crateNode.Components);
            Assert.Equal("UnityEngine.Rigidbody", component.Type.FullName);
            Assert.Empty(component.Fields);
        }

        private const string AppendComponentIntroduceHandleFixture = @"
public class AppendComponentIntroduceHandleScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Crate has no handle yet.
        scene.Add(""Crate"");

        // Unrelated sibling below, must stay untouched.
        var sibling = scene.Add(""Sibling"");
    }
}
";

        [Fact]
        public void Apply_AppendComponent_IntroduceOwnerHandle_RewritesOwnerAndAttachesComponent()
        {
            var source = AppendComponentIntroduceHandleFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendComponentStatement
                    {
                        Anchor = "Crate/0",
                        ComponentLogicalId = "crate/UnityEngine.Rigidbody#0",
                        TypeFullName = "UnityEngine.Rigidbody",
                        Fields = FieldMap.Empty,
                        OwnerHandle = "crate",
                        IntroduceOwnerHandle = true,
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Owner statement rewritten to declare the introduced handle; component follows it.
            Assert.Contains(
                "        // Crate has no handle yet.\n        var crate = scene.Add(\"Crate\");\n        crate.Component<UnityEngine.Rigidbody>();\n",
                result);

            // Unrelated sibling untouched.
            Assert.Contains(
                "        // Unrelated sibling below, must stay untouched.\n        var sibling = scene.Add(\"Sibling\");",
                result);

            var reparsed = BuilderParser.Parse(result);
            Assert.True(reparsed.Anchors.ContainsKey("crate"));
            var crateNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crateNode.Components);
            Assert.Equal("UnityEngine.Rigidbody", component.Type.FullName);
        }

        // ---- PatchComponentField / IntroduceComponentField (b3-t2) --------------------------


        private const string PatchComponentFieldFixture = @"
public class PatchComponentFieldScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Existing crate with an attached Rigidbody.
        var crate = scene.Add(""Crate"");
        crate.Component<UnityEngine.Rigidbody>(rb => rb.Set(""m_Mass"", 5f));

        // Unrelated sibling below, must stay untouched.
        var sibling = scene.Add(""Sibling"");
    }
}
";

        [Fact]
        public void Apply_PatchComponentField_RewritesOnlyValueToken_ByteIdenticalElsewhere()
        {
            var source = PatchComponentFieldFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);
            var valueSpan = parsed.FieldArgumentSpans[compId]["m_Mass"];

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchComponentField
                    {
                        Anchor = compId,
                        ValueSpan = valueSpan,
                        NewExpr = SourceExpr.ValueNodeLiteral(ValueNode.Primitive.Float(8f)),
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Only the "5f" value token changes; every other byte is identical.
            Assert.Equal(source.Replace("5f", "8f"), result);

            var reparsed = BuilderParser.Parse(result);
            var crateNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crateNode.Components);
            Assert.Equal(ValueNode.Primitive.Float(8f), component.Fields["m_Mass"]);
        }

        private const string PatchComponentFieldTypedSelectorFixture = @"
public class PatchComponentFieldTypedSelectorScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var crate = scene.Add(""Crate"");
        crate.Component<UnityEngine.Rigidbody>(rb => rb.Set(r => r.mass, 5f));
    }
}
";

        [Fact]
        public void Apply_PatchComponentField_TypedSelectorKeyUntouched()
        {
            var source = PatchComponentFieldTypedSelectorFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);
            var valueSpan = parsed.FieldArgumentSpans[compId]["member:mass"];

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchComponentField
                    {
                        Anchor = compId,
                        ValueSpan = valueSpan,
                        NewExpr = SourceExpr.ValueNodeLiteral(ValueNode.Primitive.Float(8f)),
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Selector untouched; only the value literal changes.
            Assert.Contains("rb.Set(r => r.mass, 8f)", result);
            Assert.DoesNotContain("5f", result);

            var reparsed = BuilderParser.Parse(result);
            var crateNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crateNode.Components);
            Assert.Equal(ValueNode.Primitive.Float(8f), component.Fields["member:mass"]);
        }

        private const string IntroduceComponentFieldExpressionFixture = @"
public class IntroduceComponentFieldExpressionScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var crate = scene.Add(""Crate"");
        crate.Component<UnityEngine.Rigidbody>(rb => rb.Set(""m_Mass"", 5f));
    }
}
";

        [Fact]
        public void Apply_IntroduceComponentField_ExpressionBodiedClosure_ConvertsToBlockAndAddsSet()
        {
            var source = IntroduceComponentFieldExpressionFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new IntroduceComponentField
                    {
                        Anchor = compId,
                        FieldKey = "m_Drag",
                        Value = ValueNode.Primitive.Float(0.5f),
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains(".Set(\"m_Drag\", 0.5f)", result);
            Assert.Contains(".Set(\"m_Mass\", 5f)", result);

            var reparsed = BuilderParser.Parse(result);
            var crateNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crateNode.Components);
            Assert.Equal(2, component.Fields.Count);
            Assert.Equal(ValueNode.Primitive.Float(5f), component.Fields["m_Mass"]);
            Assert.Equal(ValueNode.Primitive.Float(0.5f), component.Fields["m_Drag"]);
        }

        private const string IntroduceComponentFieldBlockFixture = @"
public class IntroduceComponentFieldBlockScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var crate = scene.Add(""Crate"");
        crate.Component<UnityEngine.Rigidbody>(rb =>
        {
            rb.Set(""m_Mass"", 5f);
        });
    }
}
";

        [Fact]
        public void Apply_IntroduceComponentField_BlockBodiedClosure_AppendsSet()
        {
            var source = IntroduceComponentFieldBlockFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new IntroduceComponentField
                    {
                        Anchor = compId,
                        FieldKey = "m_Drag",
                        Value = ValueNode.Primitive.Float(0.5f),
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains(".Set(\"m_Drag\", 0.5f)", result);
            Assert.Contains(".Set(\"m_Mass\", 5f)", result);

            var reparsed = BuilderParser.Parse(result);
            var crateNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crateNode.Components);
            Assert.Equal(2, component.Fields.Count);
            Assert.Equal(ValueNode.Primitive.Float(5f), component.Fields["m_Mass"]);
            Assert.Equal(ValueNode.Primitive.Float(0.5f), component.Fields["m_Drag"]);
        }

        // ---- RemoveStatement / ReorderStatement on component anchors (b3-t3) ------------------

        // crate is block index 0; the two component statements are indices 1 and 2; the
        // unrelated sibling is index 3. Component AnchorSpans start mid-statement (at the
        // `.Component` dot), unlike GameObject AnchorSpans which start at the statement's own
        // invocation — this is what currently defeats FindAnchorInvocation for these anchors.
        private const string ComponentRemoveReorderFixture = @"
public class ComponentRemoveReorderScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var crate = scene.Add(""Crate"");
        crate.Component<UnityEngine.Rigidbody>(rb => rb.Set(""m_Mass"", 5f));
        crate.Component<UnityEngine.BoxCollider>(bc => bc.Set(""m_IsTrigger"", true));

        var sibling = scene.Add(""Sibling"");
    }
}
";

        [Fact]
        public void Apply_RemoveComponentStatement_DeletesOnlyThatComponentStatement()
        {
            var source = ComponentRemoveReorderFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var rigidbodyId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("Rigidbody"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new RemoveStatement { Anchor = rigidbodyId },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // The Rigidbody statement's own line is gone; every other line (including the
            // BoxCollider statement, the sibling, and all formatting) is byte-identical.
            var expected = source.Replace(
                "        crate.Component<UnityEngine.Rigidbody>(rb => rb.Set(\"m_Mass\", 5f));\n",
                string.Empty);
            Assert.Equal(expected, result);

            var reparsed = BuilderParser.Parse(result);
            var crateNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crateNode.Components);
            Assert.Equal("UnityEngine.BoxCollider", component.Type.FullName);
        }

        [Fact]
        public void Apply_ReorderComponentStatements_ReordersWithinBuildBlock()
        {
            var source = ComponentRemoveReorderFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var boxColliderId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("BoxCollider"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    // NewSiblingIndex is the component's ordinal among its OWNER's components — the
                    // index the Reconciler emits (ComponentReconcileTests: BoxCollider -> 0,
                    // Rigidbody -> 1) — not a raw block offset. 0 seats BoxCollider first.
                    new ReorderStatement { Anchor = boxColliderId, NewSiblingIndex = 0 },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            var boxColliderIndex = result.IndexOf("BoxCollider", StringComparison.Ordinal);
            var rigidbodyIndex = result.IndexOf("Rigidbody", StringComparison.Ordinal);
            Assert.True(boxColliderIndex < rigidbodyIndex, "Reordered BoxCollider statement should now precede Rigidbody.");

            // Each moved statement's own .Set(...) call is untouched.
            Assert.Contains("bc.Set(\"m_IsTrigger\", true)", result);
            Assert.Contains("rb.Set(\"m_Mass\", 5f)", result);

            var reparsed = BuilderParser.Parse(result);
            var crateNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Crate");
            Assert.Equal(2, crateNode.Components.Length);
            Assert.Equal("UnityEngine.BoxCollider", crateNode.Components[0].Type.FullName);
            Assert.Equal("UnityEngine.Rigidbody", crateNode.Components[1].Type.FullName);
        }

    }
}
