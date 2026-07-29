using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // live-verify-bug-fixes b3-t1 (Bug 2): a single SourcePatch carrying BOTH a DropInstanceCall
    // AND a sibling AppendInstance* on the SAME anchor must apply through one combined
    // ReplaceNode-per-anchor fold, mirroring the append-only fold ResolveInstanceChainedCallAppends
    // already performs. Today the drop is resolved independently (ResolveDropInstanceCall), tracking
    // the `.Override(...)` invocation node; when the append applier's ReplaceNode re-parses the
    // chain first, that tracked node is orphaned and `GetCurrentNode(target)!` throws an NRE
    // (SourcePatchApplier.Instances.cs:208-209). See research.md.
    public class InstanceDropAppendApplierTests
    {
        [Fact]
        public void Apply_DropOverride_AppendAddComponent_SameAnchor_AnchorlessStatement_AppliesWithoutThrowing()
        {
            const string source = @"
public class DropAppendAnchorlessScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Tank.prefab"").Override(e => e.Set((Health x) => x.health, 50));
    }
}
";
            var anchors = BuilderParser.Parse(source).Anchors;
            var anchor = anchors.Keys.Single();

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new DropInstanceCall { Anchor = anchor, Kind = InstanceCallKind.Override, PropertyPath = "member:health" },
                    new AppendInstanceAddComponent { Anchor = anchor, TypeFullName = "Light" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.DoesNotContain(".Override(", result);
            Assert.Contains(".AddComponent<Light>()", result);

            var reparsed = BuilderParser.Parse(result);
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            Assert.Empty(instance.Overrides);
            var added = Assert.Single(instance.AddedComponents);
            Assert.Equal("Light", added.Component.Type.FullName);
        }

        [Fact]
        public void Apply_DropOverride_AppendAddComponent_SameAnchor_VarAnchoredStatement_AppliesWithoutThrowing()
        {
            const string source = @"
public class DropAppendVarScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var tank = scene.Instance(""Assets/Prefabs/Tank.prefab"").Override(e => e.Set((Health x) => x.health, 50));
    }
}
";
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new DropInstanceCall { Anchor = "tank", Kind = InstanceCallKind.Override, PropertyPath = "member:health" },
                    new AppendInstanceAddComponent { Anchor = "tank", TypeFullName = "Light" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.DoesNotContain(".Override(", result);
            Assert.Contains(".AddComponent<Light>()", result);

            var reparsed = BuilderParser.Parse(result);
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            Assert.Empty(instance.Overrides);
            var added = Assert.Single(instance.AddedComponents);
            Assert.Equal("Light", added.Component.Type.FullName);
        }
    }
}
