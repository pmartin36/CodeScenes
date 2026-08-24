using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Convergence regression for the forward-reference / sibling-order interaction.
    //
    // When a node's OWN Add-chain statement inline-references a handle declared by a LATER sibling
    // (the "add A at sibling 0, add B at sibling 1, A references B" case), declare-before-use forces
    // B's declaration to lead A's statement in source. B therefore parses at source-sibling 0 while
    // the scene holds it at sibling 1. The differ then sees source order != scene order and asks for
    // a reorder every sync -- but applying it re-breaks declare-before-use, so the applier hoists B
    // back and the reorder applies byte-identically forever. The hoist-forced order IS the converged
    // target; no reorder must be emitted.
    public class ForwardReferenceConvergenceTests
    {
        private const string LinkerType = "Game.Linker";
        private const string TargetField = "target";

        // Source that already carries the declare-before-use hoist: `beta` (scene sibling 1) is
        // declared ahead of `Alpha`'s statement (scene sibling 0) because Alpha's own chained
        // component inline-references beta.
        private const string ConvergedSource = @"using SceneBuilder.Authoring;
public class ForwardRefConvergeScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var beta = scene.Add(""Beta"");
        scene.Add(""Alpha"").Component<Game.Linker>(c => c.Set(""target"", beta));
    }
}
";

        [Fact]
        public void Reconcile_InlineForwardReference_ReferrerSiblingBelowReferenced_IsTrueFixedPoint()
        {
            var parsed = BuilderParser.Parse(ConvergedSource);

            var betaLid = parsed.Model.Roots.Single(r => r.Name == "Beta").LogicalId;
            var alphaLid = parsed.Model.Roots.Single(r => r.Name == "Alpha").LogicalId;
            var componentLid = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(LinkerType + "#0"));

            // Guard against a vacuous fixture: Alpha's Linker MUST be a chained (inline) component,
            // otherwise there is no forward-reference sibling constraint to converge on.
            Assert.Contains(componentLid, parsed.ChainedComponents);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = alphaLid, GlobalObjectId = "goid-alpha", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = betaLid, GlobalObjectId = "goid-beta", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = componentLid,
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = LinkerType,
                        ParentLogicalId = alphaLid,
                    },
                },
            };

            // Scene order: Alpha at sibling 0, Beta at sibling 1 -- the referrer sits BELOW the
            // handle it references. This is the shape the source's hoist can never match.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-alpha",
                        Name = "Alpha",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                Type = new TypeRef(LinkerType),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>(TargetField, new ValueNode.ObjectRef(betaLid)),
                                }),
                            },
                        },
                    },
                    new SnapshotNode { GlobalObjectId = "goid-beta", Name = "Beta" },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null,
                parsed.ComponentAnchors, parsed.FieldArgumentSpans, parsed.Handles,
                chainedComponents: parsed.ChainedComponents);

            Assert.Empty(result.Conflicts);

            // True fixed point: NOT byte-identical suppression downstream. The reconcile itself must
            // produce zero reorder edits -- and, on this fully converged builder, zero edits at all.
            Assert.Empty(result.Patch.Edits.OfType<ReorderStatement>());
            Assert.Empty(result.Patch.Edits);
        }
    }
}
