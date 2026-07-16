using System.Linq;
using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // ParseResult.NodeAnchors is the UN-COLLAPSED sibling of ParseResult.Anchors: Anchors is a
    // LogicalId-keyed dict, so two nodes resolving to the SAME LogicalId (a colliding
    // hand-authored `.Id(...)`) silently collapse to one entry — the exact data loss
    // b1-t3/b3-t1 need to see past. NodeAnchors is a List, so both survive.
    public class NodeAnchorTests
    {
        // Spec test 12: two nodes explicitly `.Id("Enemy-2")`'d collide. Anchors (a dict)
        // collapses them to 1; NodeAnchors (a list) must keep both.
        [Fact]
        public void NodeAnchors_CollidingIds_AreNotCollapsed()
        {
            const string colliding = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Id(""Enemy-2"");
        scene.Add(""Enemy"").Id(""Enemy-2"");
    }
}
";
            var parsed = BuilderParser.Parse(colliding);

            Assert.Equal(2, parsed.NodeAnchors.Count(a => a.LogicalId == "Enemy-2"));
            Assert.Single(parsed.Anchors);
        }

        // IdCallSpan is new plumbing the parser does not record today: it must slice EXACTLY the
        // `.Id("...")` invocation text (dot through closing paren).
        [Fact]
        public void NodeAnchor_IdCallSpan_SlicesExactlyTheIdInvocation()
        {
            const string source = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Id(""Enemy-2"");
    }
}
";
            var parsed = BuilderParser.Parse(source);

            var anchor = Assert.Single(parsed.NodeAnchors);
            Assert.NotNull(anchor.IdCallSpan);
            var span = anchor.IdCallSpan!.Value;
            Assert.Equal(".Id(\"Enemy-2\")", source.Substring(span.Start, span.Length));
        }

        // A node with no `.Id(...)` call must carry a null IdCallSpan, not a zero-length/garbage
        // span.
        [Fact]
        public void NodeAnchor_IdCallSpan_IsNull_WhenNoIdCall()
        {
            const string source = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"");
    }
}
";
            var parsed = BuilderParser.Parse(source);

            var anchor = Assert.Single(parsed.NodeAnchors);
            Assert.Null(anchor.IdCallSpan);
        }

        // Handle mirrors NodeBuilder.Handle: the authored `var` name for a handled node, null
        // for a bare statement.
        [Fact]
        public void NodeAnchor_Handle_IsVarName_ElseNull()
        {
            const string source = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Add(""A"");
        scene.Add(""B"");
    }
}
";
            var parsed = BuilderParser.Parse(source);

            Assert.Equal(2, parsed.NodeAnchors.Count);
            var first = parsed.NodeAnchors.Single(a => a.Name == "A");
            var second = parsed.NodeAnchors.Single(a => a.Name == "B");
            Assert.Equal("enemy", first.Handle);
            Assert.Null(second.Handle);
        }
    }
}
