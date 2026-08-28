using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Grammar;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Test #5 for b1-t2: FlatShapeRecognizer.Analyze and BuilderParser.Parse must agree on every
    // builder body — the recognizer returns empty IFF the parser does not throw a shape error, and
    // when it throws, violations[0] reproduces the SAME (Message, Line, Column). This is the
    // anti-drift contract that pins the two sides together after BuilderParser is refactored to
    // DERIVE its recognition decision from FlatShapeRecognizer (b1-t2). See
    // .agent_handoffs/codescenes-analyzers/b1-t2/research.md.
    public class RecognizerAgreementTests
    {
        public static IEnumerable<object[]> Corpus()
        {
            // ---- valid (both sides accept, zero violations) ------------------------------------
            yield return Case("Valid_MultiCallWithComponentClosure", @"
public class ValidScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Tag(""Player"").Layer(8);
        root.Component<Rigidbody>(c => c.Set(""mass"", 1f));
    }
}
");
            yield return Case("Valid_InstanceChain", @"
public class ValidInstanceScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"");
    }
}
");
            yield return Case("Valid_InlineInstanceComponent", @"
public class ValidInlineInstanceComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"").Component<Rigidbody>();
    }
}
");
            yield return Case("Valid_CapturedInstanceComponent", @"
public class ValidCapturedInstanceComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var ball = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        ball.Component<Rigidbody>();
    }
}
");
            yield return Case("Valid_CapturedInstanceAddComponent", @"
public class ValidCapturedInstanceAddComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var ball = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        ball.AddComponent<Rigidbody>();
    }
}
");
            yield return Case("Valid_CapturedInstanceMultiConfigure", @"
public class ValidCapturedInstanceMultiConfigureScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var ball = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        ball.Component<SphereCollider>(c => { c.Set(""m_Radius"", 2f); c.Set(""m_IsTrigger"", true); });
    }
}
");
            yield return Case("Valid_CapturedInstanceOverride", @"
public class ValidCapturedInstanceOverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var ball = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        ball.Override(e => e.Set<Rigidbody>(""m_Mass"", 2f));
    }
}
");
            yield return Case("Valid_CapturedInstanceRemoveComponent", @"
public class ValidCapturedInstanceRemoveComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var ball = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        ball.RemoveComponent<BoxCollider>();
    }
}
");
            yield return Case("Valid_CapturedInstanceOn", @"
public class ValidCapturedInstanceOnScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var ball = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        ball.On(""Turret"", b => { });
    }
}
");
            yield return Case("Valid_CapturedInstanceAddChild", @"
public class ValidCapturedInstanceAddChildScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var ball = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        ball.AddChild("""", ""Muzzle"");
    }
}
");
            yield return Case("Valid_CapturedInstanceRemoveChild", @"
public class ValidCapturedInstanceRemoveChildScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var ball = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        ball.RemoveChild(""Turret"");
    }
}
");
            yield return Case("Valid_FitSize", @"
public class ValidFitSizeScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").FitSize(height: 2f);
    }
}
");
            yield return Case("Valid_AlignTo", @"
public class ValidAlignToScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, y: AxisAlign.AbutMax);
    }
}
");
            yield return Case("Valid_NestedComponentClosureBlock", @"
public class ValidNestedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.Component<Rigidbody>(c => { c.Set(""mass"", 1f); c.Set(""drag"", 0.1f); });
    }
}
");

            // ---- REFINED-#1: setter-only chain headed by the scene root itself -----------------
            yield return Case("SceneRootSetterOnly_UnknownReceiver", @"
public class SceneRootSetterScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Tag(""x"");
    }
}
");

            // ---- control-flow (SB1001) ----------------------------------------------------------
            yield return Case("InterleavedIf_SB1001", @"
public class InterleavedIfScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        if (true) { scene.Add(""A""); }
    }
}
");

            // ---- unknown node-chain call (SB1002) ------------------------------------------------
            yield return Case("UnknownChainedCall_SB1002", @"
public class UnknownChainedCallScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""A"").Wiggle();
    }
}
");

            // ---- non-.Set in component closure (SB1003) -------------------------------------------
            yield return Case("NonSetComponentClosure_SB1003", @"
public class NonSetComponentClosureScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.Component<Rigidbody>(c => c.Nope());
    }
}
");
            yield return Case("NestedComponentClosureBlock_SecondStatementBad", @"
public class NestedBadScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.Component<Rigidbody>(c => { c.Set(""mass"", 1f); c.Nope(); });
    }
}
");

            // ---- Instance chain errors ------------------------------------------------------------
            yield return Case("Instance_UnknownReceiver", @"
public class InstanceUnknownReceiverScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        foo.Instance(""Assets/Prefabs/Enemy.prefab"");
    }
}
");
            yield return Case("Instance_OverrideUntypedSelector", @"
public class InstanceOverrideUntypedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"").Override(e => e.Set(x => x.member, 1f));
    }
}
");
            yield return Case("Instance_AddComponentNoTypeArg", @"
public class InstanceAddComponentNoTypeScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"").AddComponent();
    }
}
");
            yield return Case("Instance_RemoveComponentNoTypeArg", @"
public class InstanceRemoveComponentNoTypeScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"").RemoveComponent();
    }
}
");

            // ---- FitSize structural errors ---------------------------------------------------------
            yield return Case("FitSize_UnnamedArg", @"
public class FitSizeUnnamedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").FitSize(2f);
    }
}
");
            yield return Case("FitSize_UnknownArg", @"
public class FitSizeUnknownArgScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").FitSize(bogus: 2f);
    }
}
");
            yield return Case("FitSize_Contradiction", @"
public class FitSizeContradictionScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").FitSize(width: 2f, height: 3f);
    }
}
");
            yield return Case("FitSize_None", @"
public class FitSizeNoneScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").FitSize();
    }
}
");

            // ---- AlignTo structural errors ----------------------------------------------------------
            yield return Case("AlignTo_SecondArgUnnamed", @"
public class AlignToSecondArgUnnamedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, true);
    }
}
");
            yield return Case("AlignTo_UnknownArg", @"
public class AlignToUnknownArgScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, bogus: true);
    }
}
");

            // ---- AlignTo value-level facts (both sides ACCEPT — non-literal/unknown values are
            // TOTAL, never a structural violation; the parser stores them Unsupported instead). -------
            yield return Case("AlignTo_NonLiteralOffsetAccepted", @"
public class AlignToNonLiteralOffsetScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, y: AxisAlign.AbutMax.Offset(GetOffset()));
    }
}
");
            yield return Case("AlignTo_NegativeOffsetAccepted", @"
public class AlignToNegativeOffsetScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, y: AxisAlign.AbutMax.Offset(-0.5f));
    }
}
");
            yield return Case("AlignTo_UnknownPresetMemberAccepted", @"
public class AlignToUnknownPresetMemberScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, y: AxisAlign.Bogus);
    }
}
");

            // ---- RectTransform valid cases (b1-t3 — deferred by b1-t2 until the parse arm exists) ----
            yield return Case("Valid_RectTransform_AllArgs", @"
public class ValidRectTransformScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform(anchoredPos: (-10, -10), sizeDelta: (200, 120), anchorMin: (1, 1), anchorMax: (1, 1), pivot: (1, 1));
    }
}
");
            yield return Case("Valid_RectTransform_Bare", @"
public class ValidRectTransformBareScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform();
    }
}
");

            // ---- RectTransform structural errors (b1-t2) --------------------------------------------
            yield return Case("RectTransform_UnnamedArg", @"
public class RectTransformUnnamedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform((1,2));
    }
}
");
            yield return Case("RectTransform_UnknownArg", @"
public class RectTransformUnknownArgScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform(bogus: (1,2));
    }
}
");
            yield return Case("RectTransform_WrongTupleArity", @"
public class RectTransformWrongArityScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform(pivot: (1,2,3));
    }
}
");
            yield return Case("RectTransform_NonLiteralElement", @"
public class RectTransformNonLiteralScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform(pivot: (a, b));
    }
}
");

            // ---- ComponentRef<T> / .OnClick(...) -------------------------------------------------
            yield return Case("Valid_ComponentRef_OnClick", @"
public class ValidComponentRefOnClickScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Door"").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();
        scene.Add(""QuitButton"").Component<UnityEngine.UI.Button>(b => b.OnClick(opener, o => o.Open()));
    }
}
");

            // ---- .SetRef(...) managed reference ----------------------------------------------------
            yield return Case("Valid_SetRefManagedReference", @"
public class ValidSetRefManagedReferenceScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Component<AiBrain>(c => c.SetRef(x => x.strategy, new Aggressive { range = 5f }));
    }
}
");
            yield return Case("Valid_SetRefNull", @"
public class ValidSetRefNullScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Component<AiBrain>(c => c.SetRef(x => x.strategy, null));
    }
}
");

            // ---- COMPLETENESS EXTENSION: one case per body-grammar throw site NOT already above,
            // so acceptance-parity (recognizer flags IFF parser throws) is proven at EVERY throw
            // the dedup removes. Each body wraps the offending statement in a valid Build shell.
            foreach (var c in CompletenessCases())
            {
                yield return c;
            }
        }

        // Every remaining body-shape throw site in BuilderParser{,.Instance,.Spatial}.cs, keyed by a
        // minimal triggering builder body. Shared with RecognizerCompletenessTests (the forward guard).
        public static IEnumerable<object[]> CompletenessCases()
        {
            // BuilderParser.cs statement/chain/literal sites -----------------------------------------
            yield return Body("LocalDecl_MultipleVariables", @"int a = 0, b = 0;");
            yield return Body("LocalDecl_NoInitializer", @"int x;");
            yield return Body("ExpressionStatement_NoCallChain", @"scene;");
            yield return Body("SetterOnly_UnknownHandle", @"foo.Tag(""x"");");
            yield return Body("Add_UnknownReceiver", @"foo.Add(""A"");");
            yield return Body("Add_NoNameArgument", @"scene.Add();");
            yield return Body("Add_NameNotStringLiteral", @"scene.Add(5);");
            yield return Body("AddSecondArg_NotLambda", @"scene.Add(""A"", 5);");
            yield return Body("Component_NoTypeArgument", @"scene.Add(""A"").Component(c => c.Set(""x"", 1f));");
            yield return Body("ComponentClosure_NotLambda", @"scene.Add(""A"").Component<Rigidbody>(5);");
            yield return Body("ComponentClosure_NonExpressionStatement", @"scene.Add(""A"").Component<Rigidbody>(c => { int z = 0; });");
            yield return Body("ComponentSet_WrongArgCount", @"scene.Add(""A"").Component<Rigidbody>(c => c.Set(""x""));");
            yield return Body("ComponentSet_UnsupportedKey", @"scene.Add(""A"").Component<Rigidbody>(c => c.Set(5, 1f));");
            yield return Body("Unwrap_UnsupportedInvocationForm", @"Foo();");
            yield return Body("Unwrap_UnsupportedReceiverExpression", @"this.Add(""A"");");
            yield return Body("Transform_TooManyArguments", @"scene.Add(""A"").Transform((0,0,0),(0,0,0),(0,0,0),(0,0,0));");
            yield return Body("Transform_NotA3Tuple", @"scene.Add(""A"").Transform(5);");
            yield return Body("Transform_UnknownArgument", @"scene.Add(""A"").Transform(bogus: (0,0,0));");
            yield return Body("Transform_ComponentNotNumeric", @"scene.Add(""A"").Transform((""x"",0,0));");
            yield return Body("Tag_NotStringLiteral", @"scene.Add(""A"").Tag(5);");
            yield return Body("Active_NotBoolLiteral", @"scene.Add(""A"").Active(5);");
            yield return Body("Layer_NotNumericLiteral", @"scene.Add(""A"").Layer(""x"");");
            // A verb reserved for an instance receiver is still refused on a plain node.
            yield return Body("NodeReceiver_AddComponent", @"var n = scene.Add(""A""); n.AddComponent<Rigidbody>();");

            // BuilderParser.Instance.cs sites --------------------------------------------------------
            yield return Body("Instance_NoPathArgument", @"scene.Instance();");
            yield return Body("Instance_PathNotStringLiteral", @"scene.Instance(5);");
            yield return Body("Override_WrongArgCount", @"scene.Instance(""p.prefab"").Override();");
            yield return Body("Override_ClosureNotLambda", @"scene.Instance(""p.prefab"").Override(5);");
            yield return Body("Override_NonExpressionStatement", @"scene.Instance(""p.prefab"").Override(e => { int z = 0; });");
            yield return Body("Override_UnknownReceiver", @"scene.Instance(""p.prefab"").Override(e => other.Set<Health>(""x"", 1f));");
            yield return Body("Override_NonSetCall", @"scene.Instance(""p.prefab"").Override(e => e.Nope());");
            yield return Body("OverrideSet_WrongArgCount", @"scene.Instance(""p.prefab"").Override(e => e.Set<Health>(""x""));");
            yield return Body("OverrideSet_ParenthesizedUntypedSelector", @"scene.Instance(""p.prefab"").Override(e => e.Set((x) => x.member, 1f));");
            yield return Body("OverrideSet_StringKeyNoGeneric", @"scene.Instance(""p.prefab"").Override(e => e.Set(""m_Path"", 1f));");
            yield return Body("OverrideSet_UnsupportedKey", @"scene.Instance(""p.prefab"").Override(e => e.Set(5, 1f));");

            // BuilderParser.Spatial.cs sites (the combinations not already in the corpus) ------------
            yield return Body("FitSize_AspectPlusExplicit", @"scene.Add(""A"").FitSize(width: 2f, size: (1,1,1));");
            yield return Body("AlignTo_UnnamedThirdArg", @"var floor = scene.Add(""Floor""); scene.Add(""A"").AlignTo(floor, x: AxisAlign.AbutMin, floor);");
            yield return Body("AlignTo_LegacyUpKeywordRejected", @"var floor = scene.Add(""Floor""); scene.Add(""A"").AlignTo(floor, up: true);");

            // ComponentRef<T> / .OnClick(...) ----------------------------------------------------------
            yield return Body("Ref_NotLastInChain", @"var x = scene.Add(""A"").Ref<Rigidbody>().Tag(""y"");");
            yield return Body("Ref_MultipleTypeArguments", @"var x = scene.Add(""A"").Ref<Rigidbody, Collider>();");
            yield return Body("Ref_NonNumericArgument", @"var x = scene.Add(""A"").Ref<Rigidbody>(""oops"");");
            yield return Body("OnClick_WrongArgCount", @"scene.Add(""A"").Component<Button>(b => b.OnClick(other));");
            yield return Body("OnClick_FirstArgNotIdentifier", @"scene.Add(""A"").Component<Button>(b => b.OnClick(5, o => o.Open()));");
            yield return Body("OnClick_SecondArgNotLambda", @"scene.Add(""A"").Component<Button>(b => b.OnClick(other, 5));");
            // A single in-call static argument is a legal persistent-call form (Unity persists at
            // most one), so the still-rejected shape is TWO supplied arguments, not one.
            yield return Body("OnClick_TooManyStaticArgs", @"scene.Add(""A"").Component<Button>(b => b.OnClick(other, o => o.Open(1, 2)));");
            // An enum member access is not a persistable Unity static-argument kind — no ArgMode
            // slot exists for it.
            yield return Body("OnClick_EnumStaticArgument", @"scene.Add(""A"").Component<Button>(b => b.OnClick(other, o => o.SetMode(MyEnum.Fast)));");
            // A long literal outside int range cannot narrow into the persistent call's int slot.
            yield return Body("OnClick_StaticArgumentOutOfIntRange", @"scene.Add(""A"").Component<Button>(b => b.OnClick(other, o => o.SetCount(9999999999L)));");
            // An unknown callState member name is not one of Off/RuntimeOnly/EditorAndRuntime — no
            // ListenerCallState slot exists for it.
            yield return Body("OnClick_BogusCallState", @"scene.Add(""A"").Component<Button>(b => b.OnClick(other, o => o.Open(), callState: SomeType.Bogus));");

            // .SetRef(...) managed reference -----------------------------------------------------------
            yield return Body("SetRef_WrongArgCount", @"scene.Add(""A"").Component<AiBrain>(c => c.SetRef(x => x.strategy));");
            // .SetRef accepts only the `x => x.field` selector key, not the string-key form .Set also allows.
            yield return Body("SetRef_NonSelectorKey", @"scene.Add(""A"").Component<AiBrain>(c => c.SetRef(""strategy"", new Aggressive { range = 5f }));");
        }

        private static object[] Body(string name, string statements) => Case(name, $@"
public class {name}Scene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        {statements}
    }}
}}
");

        private static object[] Case(string name, string source) => new object[] { name, source };

        [Theory]
        [MemberData(nameof(Corpus))]
        public void Recognizer_AgreesWithBuilderParser(string caseName, string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetRoot();
            var buildMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "Build");
            var sceneParamName = buildMethod.ParameterList.Parameters[0].Identifier.Text;
            var body = buildMethod.Body!;

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            ParseException? parserException = null;
            try
            {
                BuilderParser.Parse(source);
            }
            catch (ParseException ex)
            {
                parserException = ex;
            }

            Assert.True((violations.Count == 0) == (parserException == null),
                $"[{caseName}] recognizer/parser disagreed on acceptance: " +
                $"violations={violations.Count}, parserThrew={parserException != null}");

            if (parserException == null)
            {
                return;
            }

            var first = violations[0];
            var location = Location.Create(tree, new TextSpan(first.Span.Start, first.Span.Length));
            var position = location.GetLineSpan().StartLinePosition;
            var expectedLine = position.Line + 1;
            var expectedColumn = position.Character + 1;
            var expectedMessage = $"{first.Message} at line {expectedLine}, column {expectedColumn}.";

            Assert.Equal(expectedMessage, parserException.Message);
            Assert.Equal(expectedLine, parserException.Line);
            Assert.Equal(expectedColumn, parserException.Column);
        }

        // The Corpus agreement Theory above only proves the two sides AGREE on accept/reject — a
        // .SetRef(...) call both sides reject would satisfy that theory without .SetRef ever being
        // accepted. This pins the actual acceptance: a well-formed .SetRef(...) call reports ZERO
        // shape violations.
        [Theory]
        [InlineData("Valid_SetRefManagedReference")]
        [InlineData("Valid_SetRefNull")]
        public void Analyze_WellFormedSetRefCall_ReportsZeroViolations(string caseName)
        {
            var source = (string)Corpus().Single(c => (string)c[0] == caseName)[1];

            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetRoot();
            var buildMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "Build");
            var sceneParamName = buildMethod.ParameterList.Parameters[0].Identifier.Text;
            var body = buildMethod.Body!;

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            Assert.Empty(violations);
        }

        // The Corpus agreement Theory only proves the two sides AGREE -- an `.AlignTo(...)` call both
        // sides reject would satisfy it without AlignTo ever being accepted. This pins the actual
        // acceptance: a well-formed `.AlignTo(target, y: AxisAlign.AbutMax)` call reports ZERO shape
        // violations.
        [Fact]
        public void Analyze_WellFormedAlignToCall_ReportsZeroViolations()
        {
            var source = (string)Corpus().Single(c => (string)c[0] == "Valid_AlignTo")[1];

            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetRoot();
            var buildMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "Build");
            var sceneParamName = buildMethod.ParameterList.Parameters[0].Identifier.Text;
            var body = buildMethod.Body!;

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            Assert.Empty(violations);
        }

        // The Corpus agreement Theory only proves the two sides AGREE -- a captured-instance verb
        // call BOTH sides reject (as SB1002) would satisfy that theory without the verb ever being
        // accepted on a captured handle. This pins the actual acceptance: each instance verb, used
        // on a handle captured in an EARLIER statement, reports ZERO shape violations -- the same
        // as it already does chained straight off `Instance(...)`.
        [Theory]
        [InlineData("Valid_CapturedInstanceAddComponent")]
        [InlineData("Valid_CapturedInstanceOverride")]
        [InlineData("Valid_CapturedInstanceRemoveComponent")]
        [InlineData("Valid_CapturedInstanceOn")]
        [InlineData("Valid_CapturedInstanceAddChild")]
        [InlineData("Valid_CapturedInstanceRemoveChild")]
        public void Analyze_CapturedInstanceVerb_ReportsZeroViolations(string caseName)
        {
            var source = (string)Corpus().Single(c => (string)c[0] == caseName)[1];

            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetRoot();
            var buildMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "Build");
            var sceneParamName = buildMethod.ParameterList.Parameters[0].Identifier.Text;
            var body = buildMethod.Body!;

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            Assert.Empty(violations);
        }
    }
}
