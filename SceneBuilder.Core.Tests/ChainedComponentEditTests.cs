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
    // m-ui-recttransform b3-t5: a statement-scoped edit (RemoveStatement/ReorderStatement) anchored
    // on a component whose source construct is a CHAINED call (not its own statement) must never
    // resolve against its ENCLOSING statement. Two guards, tested independently:
    //   (1) EMIT-side — ComponentReconciler never emits a ReorderStatement for an owner that has
    //       any chained component (the physical order is unrepresentable).
    //   (2) APPLY-side — SourcePatchApplier splices a chained call OUT of its own chain on
    //       RemoveStatement (or removes the whole configure-lambda argument when splicing would
    //       leave a non-reparseable body), and no-ops a ReorderStatement anchored on one, instead of
    //       destroying/reordering the enclosing statement — including when the chain is a
    //       closure-authored CHILD NODE (not just a component) and when a batch removes a parent
    //       and its closure-authored dependents together.
    // Both guards are implemented; the cases below are regression coverage for the current tree.
    public class ChainedComponentEditTests
    {
        // ---- (0) Parse-side: BuilderParser.Parse(...).ChainedComponents membership ------------

        private const string ChainedParseFixture = @"
public class ChainedParseScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Chained onto the SAME statement that creates the node.
        var a = scene.Add(""A"").Component<UnityEngine.Rigidbody>(rb => rb.Set(""m_Mass"", 1f));

        // Its OWN statement, using the handle — the established statement-form shape.
        a.Component<UnityEngine.BoxCollider>(bc => bc.Set(""m_IsTrigger"", true));

        // Chained spatial-authoring component onto the node's own creation statement.
        var q = scene.Add(""Q"").FitSize(height: 2f);

        // Expression-bodied configure lambda: no statement list of its own.
        scene.Add(""X"", x => x.Component<UnityEngine.MeshFilter>());
    }
}
";

        private static GameObjectNode FindNode(IEnumerable<GameObjectNode> roots, string name)
        {
            foreach (var root in roots)
            {
                if (root.Name == name)
                {
                    return root;
                }

                var found = FindNode(root.Children, name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        [Fact]
        public void Parse_ChainedComponentCall_IsChained_StatementFormIsNot()
        {
            var parsed = BuilderParser.Parse(ChainedParseFixture);

            var nodeA = FindNode(parsed.Model.Roots, "A");
            var nodeQ = FindNode(parsed.Model.Roots, "Q");
            var nodeX = FindNode(parsed.Model.Roots, "X");
            Assert.NotNull(nodeA);
            Assert.NotNull(nodeQ);
            Assert.NotNull(nodeX);

            var rigidbodyId = nodeA.Components.Single(c => c.Type.FullName == "UnityEngine.Rigidbody").LogicalId;
            var boxColliderId = nodeA.Components.Single(c => c.Type.FullName == "UnityEngine.BoxCollider").LogicalId;
            var fitSizeId = nodeQ.Components.Single(c => c.Type.FullName == SpatialComponents.FitSizeTypeName).LogicalId;
            var meshFilterId = nodeX.Components.Single(c => c.Type.FullName == "UnityEngine.MeshFilter").LogicalId;

            Assert.Contains(rigidbodyId, parsed.ChainedComponents);
            Assert.Contains(fitSizeId, parsed.ChainedComponents);
            Assert.Contains(meshFilterId, parsed.ChainedComponents);
            Assert.DoesNotContain(boxColliderId, parsed.ChainedComponents);
        }

        // ---- (1) Emit-side: ComponentReconciler's REORDER pass must suppress on any chained component ----

        [Fact]
        public void Reconcile_ChainedComponentOrderDiverges_EmitsNoEdits()
        {
            var rbFields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)) });
            var bcFields = FieldMap.Empty;

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "root-1",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "root-1/UnityEngine.Rigidbody#0", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = rbFields },
                            new ComponentData { LogicalId = "root-1/UnityEngine.BoxCollider#0", Type = new TypeRef("UnityEngine.BoxCollider"), Fields = bcFields },
                        },
                    },
                },
            };

            // Live scene order is the reverse: BoxCollider then Rigidbody.
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
                            new ComponentData { LogicalId = "unused-bc", Type = new TypeRef("UnityEngine.BoxCollider"), Fields = bcFields },
                            new ComponentData { LogicalId = "unused-rb", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = rbFields },
                        },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = "root-1/UnityEngine.Rigidbody#0",
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = "UnityEngine.Rigidbody",
                        ParentLogicalId = "root-1",
                    },
                    new IdentityMapEntry
                    {
                        LogicalId = "root-1/UnityEngine.BoxCollider#0",
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = "UnityEngine.BoxCollider",
                        ParentLogicalId = "root-1",
                    },
                },
            };

            // Rigidbody is a CHAINED component (e.g. `scene.Add("Root").Component<Rigidbody>(...)`)
            // — its physical order is partly frozen inside another statement, so the owner's
            // absolute component order is unrepresentable and NO reorder may be emitted at all.
            var chainedComponents = new[] { "root-1/UnityEngine.Rigidbody#0" };

            var result = Reconciler.Reconcile(model, snapshot, map, chainedComponents: chainedComponents);

            Assert.Empty(result.Patch.Edits);
        }

        // Narrowness proof: with NO chained component, order divergence on a purely statement-form
        // owner must still reorder exactly as before — the guard must not become a blanket disable.
        private const string StatementFormFixture = @"
public class StatementFormScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var crate = scene.Add(""Crate"");
        crate.Component<UnityEngine.Rigidbody>(rb => rb.Set(""m_Mass"", 5f));
        crate.Component<UnityEngine.BoxCollider>(bc => bc.Set(""m_IsTrigger"", true));
    }
}
";

        [Fact]
        public void Reconcile_StatementFormComponentsStillReorder()
        {
            var parsed = BuilderParser.Parse(StatementFormFixture);
            var crateNode = Assert.Single(parsed.Model.Roots);
            Assert.Empty(parsed.ChainedComponents);

            var rigidbody = crateNode.Components.Single(c => c.Type.FullName == "UnityEngine.Rigidbody");
            var boxCollider = crateNode.Components.Single(c => c.Type.FullName == "UnityEngine.BoxCollider");

            // Live scene order is the reverse of source order.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-crate",
                        Name = "Crate",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused-bc", Type = new TypeRef("UnityEngine.BoxCollider"), Fields = boxCollider.Fields },
                            new ComponentData { LogicalId = "unused-rb", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = rigidbody.Fields },
                        },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = crateNode.LogicalId, GlobalObjectId = "goid-crate", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = rigidbody.LogicalId, GlobalObjectId = "", Kind = "Component", ComponentType = "UnityEngine.Rigidbody", ParentLogicalId = crateNode.LogicalId },
                    new IdentityMapEntry { LogicalId = boxCollider.LogicalId, GlobalObjectId = "", Kind = "Component", ComponentType = "UnityEngine.BoxCollider", ParentLogicalId = crateNode.LogicalId },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, chainedComponents: parsed.ChainedComponents);

            var reorders = result.Patch.Edits.OfType<ReorderStatement>().ToArray();
            Assert.Equal(2, reorders.Length);
            Assert.Contains(reorders, r => r.Anchor == boxCollider.LogicalId && r.NewSiblingIndex == 0);
            Assert.Contains(reorders, r => r.Anchor == rigidbody.LogicalId && r.NewSiblingIndex == 1);
        }

        // ---- (2) Apply-side: RemoveStatement/ReorderStatement anchored on a chained call ----

        // research.md M2's exact fixture: QuitButton chains .Component<Image>(...) onto its own
        // creation statement; CanvasRenderer is a separate statement-form component; a second
        // child "Other" follows QuitButton under Panel.
        private const string ChainedRemoveFixture = @"
public class ChainedRemoveScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var panel = scene.Add(""Panel"");
        var quitButton = panel.Add(""QuitButton"").Component<UnityEngine.UI.Image>(_ => { });
        quitButton.Component<UnityEngine.CanvasRenderer>();
        panel.Add(""Other"");
    }
}
";

        [Fact]
        public void Apply_RemoveChainedComponentCall_SplicesOnlyThatCall()
        {
            var source = ChainedRemoveFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var imageId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("Image"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new RemoveStatement { Anchor = imageId } },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            var expected = source.Replace(
                "        var quitButton = panel.Add(\"QuitButton\").Component<UnityEngine.UI.Image>(_ => { });\n",
                "        var quitButton = panel.Add(\"QuitButton\");\n");
            Assert.Equal(expected, result);

            var reparsed = BuilderParser.Parse(result);
            var quitButtonNode = FindNode(reparsed.Model.Roots, "QuitButton");
            Assert.NotNull(quitButtonNode);
            var onlyComponent = Assert.Single(quitButtonNode.Components);
            Assert.Equal("UnityEngine.CanvasRenderer", onlyComponent.Type.FullName);
        }

        private const string ChainedFitSizeRemoveFixture = @"
public class ChainedFitSizeRemoveScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var q = scene.Add(""Q"").FitSize(height: 2f);
        q.Component<UnityEngine.BoxCollider>();
    }
}
";

        [Fact]
        public void Apply_RemoveChainedFitSizeCall_KeepsTheNodeStatement()
        {
            var source = ChainedFitSizeRemoveFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var fitSizeId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("FitSize"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new RemoveStatement { Anchor = fitSizeId } },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            var expected = source.Replace(
                "        var q = scene.Add(\"Q\").FitSize(height: 2f);\n",
                "        var q = scene.Add(\"Q\");\n");
            Assert.Equal(expected, result);

            var reparsed = BuilderParser.Parse(result);
            var qNode = FindNode(reparsed.Model.Roots, "Q");
            Assert.NotNull(qNode);
            var onlyComponent = Assert.Single(qNode.Components);
            Assert.Equal("UnityEngine.BoxCollider", onlyComponent.Type.FullName);
        }

        private const string TwoChainedComponentsFixture = @"
public class TwoChainedComponentsScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var crate = scene.Add(""Crate"");
        crate.Component<UnityEngine.BoxCollider>().Component<UnityEngine.Rigidbody>();
    }
}
";

        [Fact]
        public void Apply_RemoveFirstOfTwoChainedComponentCalls_KeepsTheSecond()
        {
            var source = TwoChainedComponentsFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var boxColliderId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("BoxCollider"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new RemoveStatement { Anchor = boxColliderId } },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            var expected = source.Replace(
                "        crate.Component<UnityEngine.BoxCollider>().Component<UnityEngine.Rigidbody>();\n",
                "        crate.Component<UnityEngine.Rigidbody>();\n");
            Assert.Equal(expected, result);

            var reparsed = BuilderParser.Parse(result);
            var crateNode = Assert.Single(reparsed.Model.Roots);
            var onlyComponent = Assert.Single(crateNode.Components);
            Assert.Equal("UnityEngine.Rigidbody", onlyComponent.Type.FullName);
        }

        [Fact]
        public void Apply_RemoveOwnerAndItsChainedComponentInOneBatch_DoesNotThrow()
        {
            var source = ChainedRemoveFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var imageId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("Image"));
            var canvasRendererId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("CanvasRenderer"));
            var quitButtonNodeId = FindNode(parsed.Model.Roots, "QuitButton").LogicalId;

            // Mirrors Reconciler.cs's REAL DetectRemovals cascade (Reconciler.cs:799-813): removing
            // a node emits a RemoveStatement for the owner AND for EVERY one of its Kind=="Component"
            // dependents — QuitButton has two (the chained Image and the statement-form
            // CanvasRenderer) — all in the SAME patch, owner first.
            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new RemoveStatement { Anchor = quitButtonNodeId },
                    new RemoveStatement { Anchor = imageId },
                    new RemoveStatement { Anchor = canvasRendererId },
                },
            };

            var exception = Record.Exception(() => SourcePatchApplier.Apply(source, patch, anchors));
            Assert.Null(exception);

            var result = SourcePatchApplier.Apply(source, patch, anchors);
            Assert.DoesNotContain("quitButton", result);
            Assert.Contains("var panel = scene.Add(\"Panel\");", result);
            Assert.Contains("panel.Add(\"Other\");", result);
        }

        // m-ui-recttransform b3-t5 (iteration 4, routed from validator.md iteration 3's BLOCKING
        // finding): the THIRD shape IsChainedNonStatementCall's own doc comment enumerates
        // (expression-bodied configure lambda) was previously misclassified as "own statement"
        // because its receiver is a bare identifier just like `crate.Component<T>(...);` — the
        // parse-side predicate (StatementAnchored) already got this right (see
        // Parse_ChainedComponentCall_IsChained_StatementFormIsNot's meshFilterId assertion); this
        // pins the apply-side copy to agree, on the exact shape the product itself emits
        // (SourcePatchApplier.Instances.cs:198-200's `cfg => cfg.Component<T>(...)` for a
        // one-component prefab child).
        private const string ChainedLambdaRemoveFixture = @"
public class ChainedLambdaRemoveScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""X"", x => x.Component<UnityEngine.MeshFilter>());
        scene.Add(""Other"");
    }
}
";

        [Fact]
        public void Apply_RemoveChainedExpressionLambdaComponentCall_KeepsTheNodeStatement()
        {
            var source = ChainedLambdaRemoveFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var meshFilterId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("MeshFilter"));
            Assert.Contains(meshFilterId, parsed.ChainedComponents);

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new RemoveStatement { Anchor = meshFilterId } },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Pre-fix, the whole `scene.Add("X", x => ...);` statement was deleted, taking node
            // "X" with it. The node's declaration must survive; only the component call is removed.
            var reparsed = BuilderParser.Parse(result);
            var nodeX = FindNode(reparsed.Model.Roots, "X");
            Assert.NotNull(nodeX);
            Assert.Empty(nodeX.Components);
            Assert.Contains("scene.Add(\"Other\");", result);
        }

        // m-ui-recttransform b3-t5 (iteration 5, routed from validator.md iteration 4's BLOCKING
        // finding): iteration 4's fix for the single-call expression-lambda shape above
        // (Apply_RemoveChainedExpressionLambdaComponentCall_KeepsTheNodeStatement) removes the
        // WHOLE lambda argument whenever the anchored call is a SimpleLambdaExpressionSyntax
        // expression body, without checking that the anchor is the body's ONLY call. When the
        // lambda body is itself a chain and the anchor is its outermost link, everything else in
        // that chain must survive — a sibling Component call (this fixture) or, per the next test,
        // a whole child GameObject.
        private const string MultiCallExpressionLambdaFixture = @"
public class MultiLambdaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""X"", x => x.Component<UnityEngine.MeshFilter>().Component<UnityEngine.BoxCollider>());
        scene.Add(""Other"");
    }
}
";

        [Fact]
        public void Apply_RemoveOuterOfTwoChainedComponentCallsInExpressionLambda_KeepsTheInnerCall()
        {
            var source = MultiCallExpressionLambdaFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var boxColliderId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("BoxCollider"));
            Assert.Contains(boxColliderId, parsed.ChainedComponents);

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new RemoveStatement { Anchor = boxColliderId } },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Pre-fix, the whole `scene.Add("X", x => ...);` statement was deleted, taking the
            // MeshFilter the user never touched with it. Only the BoxCollider link may go.
            var expected = source.Replace(
                "        scene.Add(\"X\", x => x.Component<UnityEngine.MeshFilter>().Component<UnityEngine.BoxCollider>());\n",
                "        scene.Add(\"X\", x => x.Component<UnityEngine.MeshFilter>());\n");
            Assert.Equal(expected, result);

            var reparsed = BuilderParser.Parse(result);
            var nodeX = FindNode(reparsed.Model.Roots, "X");
            Assert.NotNull(nodeX);
            var onlyComponent = Assert.Single(nodeX.Components);
            Assert.Equal("UnityEngine.MeshFilter", onlyComponent.Type.FullName);
            Assert.NotNull(FindNode(reparsed.Model.Roots, "Other"));
        }

        // Same defect class, one link further out: the anchored call is chained after a NESTED
        // `Add` rather than after another `Component` call, so deleting it must keep the child
        // GameObject the `Add` created.
        private const string NestedAddThenComponentInExpressionLambdaFixture = @"
public class AddChainScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""P"", p => p.Add(""L"").Component<UnityEngine.MeshFilter>());
        scene.Add(""Other"");
    }
}
";

        [Fact]
        public void Apply_RemoveComponentChainedAfterNestedAddInExpressionLambda_KeepsTheChildAdd()
        {
            var source = NestedAddThenComponentInExpressionLambdaFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var meshFilterId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("MeshFilter"));
            Assert.Contains(meshFilterId, parsed.ChainedComponents);

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new RemoveStatement { Anchor = meshFilterId } },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Pre-fix, the whole `scene.Add("P", p => ...);` statement was deleted, taking the
            // child GameObject "L" the user's `p.Add("L")` created with it.
            var expected = source.Replace(
                "        scene.Add(\"P\", p => p.Add(\"L\").Component<UnityEngine.MeshFilter>());\n",
                "        scene.Add(\"P\", p => p.Add(\"L\"));\n");
            Assert.Equal(expected, result);

            var reparsed = BuilderParser.Parse(result);
            var nodeP = FindNode(reparsed.Model.Roots, "P");
            Assert.NotNull(nodeP);
            var childL = Assert.Single(nodeP.Children);
            Assert.Equal("L", childL.Name);
            Assert.Empty(childL.Components);
            Assert.NotNull(FindNode(reparsed.Model.Roots, "Other"));
        }

        // Narrowness control for the previous case: removing the INNER call (chained BELOW the
        // anchor's own chain root) must still splice just that one link via the ordinary path —
        // ConfigureLambdaArgumentToRemove only fires for the chain's OUTERMOST link.
        [Fact]
        public void Apply_RemoveInnerChainedComponentInLambdaBody_KeepsTheOuterOne()
        {
            var source = MultiCallExpressionLambdaFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var meshFilterId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("MeshFilter"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new RemoveStatement { Anchor = meshFilterId } },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            var expected = source.Replace(
                "        scene.Add(\"X\", x => x.Component<UnityEngine.MeshFilter>().Component<UnityEngine.BoxCollider>());\n",
                "        scene.Add(\"X\", x => x.Component<UnityEngine.BoxCollider>());\n");
            Assert.Equal(expected, result);

            var reparsed = BuilderParser.Parse(result);
            var nodeX = FindNode(reparsed.Model.Roots, "X");
            Assert.NotNull(nodeX);
            var onlyComponent = Assert.Single(nodeX.Components);
            Assert.Equal("UnityEngine.BoxCollider", onlyComponent.Type.FullName);
        }

        // ---- (3) Apply-side, M2: a closure-authored CHILD NODE (not just a component) chained on
        // the configure lambda's bottom link ----

        // research.md M2's F2 fixture: Btn is authored as the SOLE link of Panel's configure
        // closure. Deleting Btn (a GameObject RemoveStatement, not a component one) must collapse
        // the whole configure argument to Panel's bare creation call — never destroy Panel's own
        // statement, which is what an enclosing-statement resolution would do.
        private const string ClosureAuthoredChildFixture = @"
public class ClosureAuthoredChildScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"", p => p.Add(""Btn""));
        scene.Add(""Other"");
    }
}
";

        [Fact]
        public void Apply_RemoveClosureAuthoredChild_KeepsTheParentStatement()
        {
            var source = ClosureAuthoredChildFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var btnNode = FindNode(parsed.Model.Roots, "Btn");

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new RemoveStatement { Anchor = btnNode.LogicalId } },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Pre-fix, `IsChainedNonStatementCall` returned false for a node-creating call chained
            // at statement level, so this resolved against the ENCLOSING statement and deleted the
            // whole `scene.Add("Panel", ...);` line, taking "Other" untouched but Panel gone.
            var expected = source.Replace(
                "        scene.Add(\"Panel\", p => p.Add(\"Btn\"));\n",
                "        scene.Add(\"Panel\");\n");
            Assert.Equal(expected, result);

            var reparsed = BuilderParser.Parse(result);
            var panelNode = FindNode(reparsed.Model.Roots, "Panel");
            Assert.NotNull(panelNode);
            Assert.Empty(panelNode.Children);
            Assert.NotNull(FindNode(reparsed.Model.Roots, "Other"));
        }

        private const string ClosureChildWithComponentFixture = @"
public class ClosureChildWithComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""P"", p => p.Add(""L"").Component<UnityEngine.MeshFilter>());
    }
}
";

        // research.md M2's F2' cascade: removing the closure-authored child NODE and its own
        // cascaded component in the SAME batch (Reconciler's real DetectRemovals cascade) must
        // converge to the bare parent creation call regardless of which edit is emitted first.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Apply_RemoveClosureAuthoredChildAndItsCascadedComponent_ConvergesToBareAdd(bool removeChildFirst)
        {
            var source = ClosureChildWithComponentFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var childL = FindNode(parsed.Model.Roots, "L");
            var meshFilterId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("MeshFilter"));

            var removeChild = new RemoveStatement { Anchor = childL.LogicalId };
            var removeComponent = new RemoveStatement { Anchor = meshFilterId };

            var patch = new SourcePatch
            {
                Edits = removeChildFirst
                    ? new SourceEdit[] { removeChild, removeComponent }
                    : new SourceEdit[] { removeComponent, removeChild },
            };

            string result = null;
            var exception = Record.Exception(() => result = SourcePatchApplier.Apply(source, patch, anchors));
            Assert.Null(exception);

            Assert.Contains("scene.Add(\"P\");", result);
            Assert.DoesNotContain("Add(\"L\")", result);
            Assert.DoesNotContain("Component<UnityEngine.MeshFilter>", result);
        }

        private const string ClosureAuthoredParentChildFixture = @"
public class ClosureAuthoredParentChildScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""P"", p => p.Add(""L""));
        scene.Add(""Other"");
    }
}
";

        // research.md M3a: the SAME batch carries a RemoveStatement for the closure-authored PARENT
        // (resolved via the statement arm) and one for its closure-authored CHILD (resolved via the
        // lambda-argument-removal branch) — both landing on the SAME physical statement. Pre-fix
        // this threw NullReferenceException out of Apply, aborting every other edit in the sync.
        [Fact]
        public void Apply_RemoveClosureAuthoredParentAndChild_DoesNotThrow()
        {
            var source = ClosureAuthoredParentChildFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var nodeP = FindNode(parsed.Model.Roots, "P");
            var nodeL = FindNode(parsed.Model.Roots, "L");

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new RemoveStatement { Anchor = nodeP.LogicalId },
                    new RemoveStatement { Anchor = nodeL.LogicalId },
                },
            };

            string result = null;
            var exception = Record.Exception(() => result = SourcePatchApplier.Apply(source, patch, anchors));
            Assert.Null(exception);

            Assert.DoesNotContain("Add(\"P\"", result);
            Assert.Contains("scene.Add(\"Other\");", result);
        }

        private const string BlockClosureParentTwoChildrenFixture = @"
public class BlockClosureParentTwoChildrenScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""P"", p =>
        {
            p.Add(""Btn"");
            p.Add(""Label"");
        });
        scene.Add(""Other"");
    }
}
";

        // research.md M3b: block-closure form of the same cascade — the parent's own statement AND
        // both of its block-scoped children's own (nested) statements are removed in one batch.
        [Fact]
        public void Apply_RemoveBlockClosureParentAndChildren_DoesNotThrow()
        {
            var source = BlockClosureParentTwoChildrenFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var nodeP = FindNode(parsed.Model.Roots, "P");
            var nodeBtn = FindNode(parsed.Model.Roots, "Btn");
            var nodeLabel = FindNode(parsed.Model.Roots, "Label");

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new RemoveStatement { Anchor = nodeP.LogicalId },
                    new RemoveStatement { Anchor = nodeBtn.LogicalId },
                    new RemoveStatement { Anchor = nodeLabel.LogicalId },
                },
            };

            string result = null;
            var exception = Record.Exception(() => result = SourcePatchApplier.Apply(source, patch, anchors));
            Assert.Null(exception);

            Assert.DoesNotContain("Add(\"P\"", result);
            Assert.Contains("scene.Add(\"Other\");", result);
        }

        // Narrowness control: removing only ONE block-closure child leaves the parent statement and
        // its sibling child untouched — the guard must not become a blanket "never touch a
        // block-closure statement" disable.
        [Fact]
        public void Apply_RemoveBlockClosureChild_RemovesOnlyItsStatement()
        {
            var source = BlockClosureParentTwoChildrenFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var nodeBtn = FindNode(parsed.Model.Roots, "Btn");

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new RemoveStatement { Anchor = nodeBtn.LogicalId } },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.DoesNotContain("Add(\"Btn\")", result);
            Assert.Contains("p.Add(\"Label\");", result);
            Assert.Contains("scene.Add(\"P\", p =>", result);
            Assert.Contains("scene.Add(\"Other\");", result);
        }

        // research.md M2 reorder (O1): a ReorderStatement anchored on a closure-authored child has
        // no representable absolute position of its own — applying it must be a pure no-op, never a
        // reorder of the ENCLOSING statement among the roots (which would silently move "Other").
        [Fact]
        public void Apply_ReorderClosureAuthoredChild_LeavesSourceUnchanged()
        {
            var source = ClosureAuthoredChildFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var btnNode = FindNode(parsed.Model.Roots, "Btn");

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new ReorderStatement { Anchor = btnNode.LogicalId, NewSiblingIndex = 0 } },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Equal(source, result);
            var panelIndex = result.IndexOf("scene.Add(\"Panel\"", StringComparison.Ordinal);
            var otherIndex = result.IndexOf("scene.Add(\"Other\");", StringComparison.Ordinal);
            Assert.True(panelIndex < otherIndex, "Panel must stay BEFORE \"Other\" in root order (no root-order corruption).\n" + result);
        }

        // Narrowness control: a ReorderStatement anchored on a BLOCK-closure child still reorders —
        // the no-op guard must not swallow the statement-form reorder path too.
        [Fact]
        public void Apply_ReorderBlockClosureChild_StillReordersWithinItsBlock()
        {
            var source = BlockClosureParentTwoChildrenFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var nodeLabel = FindNode(parsed.Model.Roots, "Label");

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[] { new ReorderStatement { Anchor = nodeLabel.LogicalId, NewSiblingIndex = 0 } },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            var labelIndex = result.IndexOf("p.Add(\"Label\");", StringComparison.Ordinal);
            var btnIndex = result.IndexOf("p.Add(\"Btn\");", StringComparison.Ordinal);
            Assert.True(labelIndex >= 0 && btnIndex >= 0 && labelIndex < btnIndex,
                "Label must be reordered BEFORE Btn inside the block.\n" + result);
        }

        // ---- (4) Full loop: Parse -> Reconcile -> Apply -> fold map -> re-Parse -> Reconcile again
        // (StructuralSyncbackTests idiom) — proves the fix converges rather than trading corruption
        // for perpetual churn ----

        [Fact]
        public void Reconcile_DeletedClosureAuthoredChild_ConvergesAndSecondReconcileIsNoOp()
        {
            var source = ClosureAuthoredChildFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var panelNode = FindNode(parsed.Model.Roots, "Panel");
            var btnNode = FindNode(parsed.Model.Roots, "Btn");
            var otherNode = FindNode(parsed.Model.Roots, "Other");

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = panelNode.LogicalId, GlobalObjectId = "goid-panel", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = btnNode.LogicalId, GlobalObjectId = "goid-btn", Kind = "GameObject", ParentLogicalId = panelNode.LogicalId },
                    new IdentityMapEntry { LogicalId = otherNode.LogicalId, GlobalObjectId = "goid-other", Kind = "GameObject" },
                },
            };

            // Live scene: the user deleted Btn in the Inspector — Panel and Other survive.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-panel", Name = "Panel" },
                    new SnapshotNode { GlobalObjectId = "goid-other", Name = "Other" },
                },
            };

            var recon1 = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors,
                componentAnchors: parsed.ComponentAnchors, chainedComponents: parsed.ChainedComponents);

            Assert.NotEmpty(recon1.Patch.Edits);
            Assert.Empty(recon1.Conflicts);

            var patched = SourcePatchApplier.Apply(source, recon1.Patch, anchors);

            // Pre-fix, the whole Panel statement (and Other, via a root-order corruption) could be
            // lost here; the fix must leave exactly Panel (bare) and Other.
            Assert.Contains("scene.Add(\"Panel\");", patched);
            Assert.Contains("scene.Add(\"Other\");", patched);

            var foldedEntries = map.Entries
                .Where(e => !recon1.RemovedLogicalIds.Contains(e.LogicalId))
                .Concat(recon1.AddedEntries)
                .ToArray();
            var updatedMap = map with { Entries = foldedEntries };

            var reparsed = BuilderParser.Parse(patched, updatedMap);
            var recon2 = Reconciler.Reconcile(
                reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors,
                componentAnchors: reparsed.ComponentAnchors, chainedComponents: reparsed.ChainedComponents);

            Assert.Empty(recon2.Patch.Edits);
            Assert.Empty(recon2.Conflicts);
        }

        [Fact]
        public void Reconcile_DeletedChainedComponent_KeepsSiblingAndSecondReconcileIsNoOp()
        {
            var source = MultiCallExpressionLambdaFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var nodeX = FindNode(parsed.Model.Roots, "X");
            var nodeOther = FindNode(parsed.Model.Roots, "Other");
            var meshFilter = nodeX.Components.Single(c => c.Type.FullName == "UnityEngine.MeshFilter");
            var boxCollider = nodeX.Components.Single(c => c.Type.FullName == "UnityEngine.BoxCollider");

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = nodeX.LogicalId, GlobalObjectId = "goid-x", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = meshFilter.LogicalId, GlobalObjectId = "", Kind = "Component", ComponentType = "UnityEngine.MeshFilter", ParentLogicalId = nodeX.LogicalId },
                    new IdentityMapEntry { LogicalId = boxCollider.LogicalId, GlobalObjectId = "", Kind = "Component", ComponentType = "UnityEngine.BoxCollider", ParentLogicalId = nodeX.LogicalId },
                    new IdentityMapEntry { LogicalId = nodeOther.LogicalId, GlobalObjectId = "goid-other", Kind = "GameObject" },
                },
            };

            // Live scene: the user deleted the BoxCollider component in the Inspector — MeshFilter
            // survives.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-x",
                        Name = "X",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused-mf", Type = new TypeRef("UnityEngine.MeshFilter"), Fields = FieldMap.Empty },
                        },
                    },
                    new SnapshotNode { GlobalObjectId = "goid-other", Name = "Other" },
                },
            };

            var recon1 = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors,
                componentAnchors: parsed.ComponentAnchors, chainedComponents: parsed.ChainedComponents);

            Assert.NotEmpty(recon1.Patch.Edits);
            Assert.Empty(recon1.Conflicts);

            var patched = SourcePatchApplier.Apply(source, recon1.Patch, anchors);

            // Pre-fix, the whole `scene.Add("X", x => ...);` statement (and the MeshFilter the user
            // never touched) was deleted here.
            Assert.Contains("scene.Add(\"X\", x => x.Component<UnityEngine.MeshFilter>());", patched);
            Assert.Contains("scene.Add(\"Other\");", patched);

            var foldedEntries = map.Entries
                .Where(e => !recon1.RemovedLogicalIds.Contains(e.LogicalId))
                .Concat(recon1.AddedEntries)
                .ToArray();
            var updatedMap = map with { Entries = foldedEntries };

            var reparsed = BuilderParser.Parse(patched, updatedMap);
            var recon2 = Reconciler.Reconcile(
                reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors,
                componentAnchors: reparsed.ComponentAnchors, chainedComponents: reparsed.ChainedComponents);

            Assert.Empty(recon2.Patch.Edits);
            Assert.Empty(recon2.Conflicts);
        }

        [Fact]
        public void Apply_ReorderChainedComponent_LeavesSourceUnchanged()
        {
            // research.md M1's exact corruption fixture: applying the two ReorderStatements
            // Reconcile would (pre-guard-1) emit for this owner directly against the applier
            // (guard 2, the forgotten-wiring case) must be a no-op — never a reorder that also
            // silently moves "Other" out from under Panel.
            var source = ChainedRemoveFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var imageId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("Image"));
            var canvasRendererId = parsed.ComponentAnchors.Keys.Single(k => k.Contains("CanvasRenderer"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new ReorderStatement { Anchor = canvasRendererId, NewSiblingIndex = 0 },
                    new ReorderStatement { Anchor = imageId, NewSiblingIndex = 1 },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Equal(source, result);
            var otherIndex = result.IndexOf("panel.Add(\"Other\");", StringComparison.Ordinal);
            var quitButtonIndex = result.IndexOf("var quitButton", StringComparison.Ordinal);
            Assert.True(quitButtonIndex < otherIndex, "\"Other\" must stay AFTER QuitButton in Panel's child order.\n" + result);
        }
    }
}
