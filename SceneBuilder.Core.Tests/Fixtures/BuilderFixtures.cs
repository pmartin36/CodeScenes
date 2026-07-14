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

        // b3-t1: .Component<T>() fixtures. Type arguments are authored FULLY QUALIFIED
        // (Core does no namespace resolution — Type.FullName == typeArg.ToString() verbatim).

        public const string ComponentWithRawField = @"
public class ComponentRawFieldScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Player"").Component<UnityEngine.Rigidbody>(rb => rb.Set(""m_Mass"", 12f));
    }
}
";

        public const string ComponentWithTypedSetter = @"
public class ComponentTypedSetterScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Player"").Component<UnityEngine.Rigidbody>(rb => rb.Set(r => r.mass, 12f));
    }
}
";

        public const string ComponentWithPrivateField = @"
public class ComponentPrivateFieldScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Player"").Component<Game.Health>(h => h.Set(""_maxHealth"", 100));
    }
}
";

        // Two components chained onto the same node, in SOURCE order, for order/identity/anchor tests.
        public const string ComponentSourceOrder = @"
public class ComponentSourceOrderScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Player"").Component<UnityEngine.Rigidbody>(rb => rb.Set(""m_Mass"", 12f)).Component<Game.Health>(h => h.Set(""_maxHealth"", 100));
    }
}
";

        // b3-t2: one field per ValueNode kind (enum type authored fully-qualified per
        // research Verdict #1/R1). Nested/List fixtures exercise structural (non-semantic)
        // dispatch — Game.ImpactData/Game.Kitchen/Game.Oddity/Game.Trigger need not resolve.
        public const string ComponentAllValueKinds = @"
public class ComponentAllValueKindsScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Subject"").Component<Game.Kitchen>(c =>
        {
            c.Set(""flagBool"", true);
            c.Set(""countInt"", 7);
            c.Set(""bigLong"", 100L);
            c.Set(""massFloat"", 12f);
            c.Set(""ratioDouble"", 2.5);
            c.Set(""label"", ""hello"");
            c.Set(""faction"", Game.Faction.Enemy);
            c.Set(""dir2"", new Vector2(1f, 2f));
            c.Set(""dir3"", new Vector3(1f, 2f, 3f));
            c.Set(""dir4"", new Vector4(1f, 2f, 3f, 4f));
            c.Set(""rot"", new Quaternion(0f, 0f, 0f, 1f));
            c.Set(""tint"", new Color(1f, 0f, 0f, 1f));
            c.Set(""impact"", new Game.ImpactData { damage = 10, knockback = 2.5f });
            c.Set(""order"", new int[] { 3, 1, 2 });
        });
    }
}
";

        // b3-t2: flags-enum member order must not depend on source operand order.
        public const string ComponentFlagsEnumGroundWater = @"
public class ComponentFlagsEnumSceneA : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Zone"").Component<Game.Trigger>(t => t.Set(""layers"", Game.Layers.Ground | Game.Layers.Water));
    }
}
";

        public const string ComponentFlagsEnumWaterGround = @"
public class ComponentFlagsEnumSceneB : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Zone"").Component<Game.Trigger>(t => t.Set(""layers"", Game.Layers.Water | Game.Layers.Ground));
    }
}
";

        // b3-t2: an unrecognized value form falls back to Unsupported(rawToken) — never fail-loud.
        public const string ComponentUnsupportedValue = @"
public class ComponentUnsupportedValueScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Weird"").Component<Game.Oddity>(o => o.Set(""m_Weird"", SomeWeirdExpr()));
    }
}
";
    }
}
