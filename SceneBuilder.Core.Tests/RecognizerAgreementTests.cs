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
            yield return Case("Valid_FitSize", @"
public class ValidFitSizeScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").FitSize(height: 2f);
    }
}
");
            yield return Case("Valid_SurfaceSnap", @"
public class ValidSurfaceSnapScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").SurfaceSnap(down: true);
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

            // ---- SurfaceSnap structural errors -----------------------------------------------------
            yield return Case("SurfaceSnap_UnnamedArg", @"
public class SurfaceSnapUnnamedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").SurfaceSnap(true);
    }
}
");
            yield return Case("SurfaceSnap_UnknownArg", @"
public class SurfaceSnapUnknownArgScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").SurfaceSnap(bogus: true);
    }
}
");
            yield return Case("SurfaceSnap_Contradiction", @"
public class SurfaceSnapContradictionScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").SurfaceSnap(up: true, down: true);
    }
}
");
            yield return Case("SurfaceSnap_None", @"
public class SurfaceSnapNoneScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").SurfaceSnap();
    }
}
");
        }

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
    }
}
