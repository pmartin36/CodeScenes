namespace SceneBuilder.Core.Tests.Fixtures
{
    // Builder-file source held as plain C# strings (NOT compiled into this test project) —
    // the parser is a syntax-only Roslyn walk (§6); these fixtures reference the
    // ISceneDefinition/SceneRoot authoring surface which does not exist in Core.
    public static class BuilderFixtures
    {
        public const string TwoRootsWithOrderedChildren = @"
public class TwoRootsScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root1 = scene.Add(""Root1"").Tag(""Player"").Layer(8).Active(true).Static();
        root1.Add(""ChildA"");
        root1.Add(""ChildB"");

        var root2 = scene.Add(""Root2"");
        root2.Add(""ChildC"");
    }
}
";

        public const string TransformWithEulerRotation = @"
public class TransformScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Rotated"").Transform(pos: (1f, 2f, 3f), rot: (0, 90, 0), scale: (2f, 2f, 2f));
    }
}
";

        public const string BareAdd = @"
public class BareAddScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Bare"");
    }
}
";

        public const string ClosureNestedChild = @"
public class ClosureScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.Add(""Muzzle"", m => m.Transform(pos: (0, 0, 1)));
    }
}
";

        public const string InterleavedForLoop = @"
public class LoopScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Root"");

        for (int i = 0; i < 3; i++)
        {
            scene.Add(""Item"" + i);
        }
    }
}
";

        public const string HandleNamedRoot = @"
public class HandleNamedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var player = scene.Add(""Player"");
    }
}
";

        public const string ExplicitIdRoot = @"
public class ExplicitIdScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Id(""boss"");
    }
}
";

        // Sibling-insertion pair for synthesized-LogicalId stability (Priority 3):
        // parsing SiblingInsertion_B with the IdentityMap produced by parsing
        // SiblingInsertion_A must keep "Wall"'s synthesized id stable even though a
        // new sibling was inserted before it, shifting its positional index.
        public const string SiblingInsertion_A = @"
public class SiblingSceneA : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var parent = scene.Add(""Parent"");
        parent.Add(""A"");
        parent.Add(""B"");
        parent.Add(""Wall"");
    }
}
";

        public const string SiblingInsertion_B = @"
public class SiblingSceneB : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var parent = scene.Add(""Parent"");
        parent.Add(""A"");
        parent.Add(""B"");
        parent.Add(""NewSibling"");
        parent.Add(""Wall"");
    }
}
";
    }
}
