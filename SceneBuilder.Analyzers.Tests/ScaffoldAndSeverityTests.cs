using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeScenes.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace SceneBuilder.Analyzers.Tests
{
    // Tests #13-#14 for b2-t4: the SB120x UnityEvent-signature scaffold must be provably inert
    // against today's (M8-less) sources yet real against a bound M8-shaped stub, and the
    // severity-discipline partition over DiagnosticDescriptors.All must hold. See
    // .agent_handoffs/codescenes-analyzers/b2-t4/research.md.
    public class ScaffoldAndSeverityTests
    {
        private static DiagnosticResult ExpectedAt(DiagnosticDescriptor descriptor, SyntaxNode node)
        {
            var lineSpan = node.GetLocation().GetLineSpan();
            return AnalyzerVerifier.Diagnostic(descriptor).WithSpan(
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1,
                lineSpan.EndLinePosition.Line + 1,
                lineSpan.EndLinePosition.Character + 1);
        }

        private static SimpleNameSyntax MethodNameNode(SyntaxTree tree, string methodName) =>
            tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression as MemberAccessExpressionSyntax)
                .Where(m => m != null && m!.Name.Identifier.Text == methodName)
                .Select(m => m!.Name)
                .Single();

        [Fact]
        public async Task Analyzer_UnityEventSignatureScaffold_InertToday()
        {
            // (a) INERT: `.OnClick(...)` with a method-lambda arg but nothing in scope for it to
            // bind to (no OnClick member exists anywhere) -> the wiring call itself does not
            // resolve to a real method symbol, so the semantic guard must suppress SB120x even
            // though the name matches. Placed in a class with no Build method so FlatShape/Nudge
            // stay inert too.
            const string inertSource = @"
public class NotWired
{
    public void CallSite()
    {
        object t = null;
        Unbound.OnClick(t, x => x.Foo());
    }
}
";
            await AnalyzerVerifier.VerifyAsync(inertSource);

            // (b) A bound OnClick wiring a static in-call argument that matches the referenced
            // method's own single parameter is a LEGAL persistent-call form: zero diagnostics.
            // Matched against the 0-arity clean case below (isolates that silence holds at both
            // arities, not just zero).
            const string oneArgLegalSource = @"
public static class Btn
{
    public static void OnClick<T>(T target, System.Action<T> listener) { }
}

public class Target
{
    public void Open() { }
    public void SetLevel(int n) { }
}

public class Caller
{
    public void Wire()
    {
        var t = new Target();
        Btn.OnClick(t, x => x.SetLevel(3));
    }
}
";
            await AnalyzerVerifier.VerifyAsync(oneArgLegalSource);

            const string cleanSource = @"
public static class Btn
{
    public static void OnClick<T>(T target, System.Action<T> listener) { }
}

public class Target
{
    public void Open() { }
}

public class Caller
{
    public void Wire()
    {
        var t = new Target();
        Btn.OnClick(t, x => x.Open());
    }
}
";
            await AnalyzerVerifier.VerifyAsync(cleanSource);

            // (c) A genuine mismatch: Unity persists at most one static argument, so a lambda
            // call supplying two is a mismatch regardless of the referenced method's own arity.
            const string twoArgsSuppliedSource = @"
public static class Btn
{
    public static void OnClick<T>(T target, System.Action<T> listener) { }
}

public class Target
{
    public void SetTwo(int a, int b) { }
}

public class Caller
{
    public void Wire()
    {
        var t = new Target();
        Btn.OnClick(t, x => x.SetTwo(1, 2));
    }
}
";
            var twoArgsTree = CSharpSyntaxTree.ParseText(twoArgsSuppliedSource);
            var expectedTwoArgs = ExpectedAt(DiagnosticDescriptors.SB1201, MethodNameNode(twoArgsTree, "SetTwo"));

            await AnalyzerVerifier.VerifyAsync(twoArgsSuppliedSource, expectedTwoArgs);

            // (d) A genuine mismatch reachable only once arity is judged against the CALL, not a
            // constant: one argument supplied against a method whose second parameter is
            // optional (2 declared parameters) — Unity matches by exact signature, so this does
            // not bind at runtime even though plain C# overload resolution accepts the call.
            const string optionalParamMismatchSource = @"
public static class Btn
{
    public static void OnClick<T>(T target, System.Action<T> listener) { }
}

public class Target
{
    public void SetWithDefault(int a, int b = 0) { }
}

public class Caller
{
    public void Wire()
    {
        var t = new Target();
        Btn.OnClick(t, x => x.SetWithDefault(5));
    }
}
";
            var optionalParamTree = CSharpSyntaxTree.ParseText(optionalParamMismatchSource);
            var expectedOptionalParam = ExpectedAt(DiagnosticDescriptors.SB1201, MethodNameNode(optionalParamTree, "SetWithDefault"));

            await AnalyzerVerifier.VerifyAsync(optionalParamMismatchSource, expectedOptionalParam);

            // (e) SB1202 is reachable now that a matching arity no longer short-circuits at a
            // fixed 0: a 0-arity but non-public referenced method still fires the accessibility
            // diagnostic.
            const string nonPublicZeroArgSource = @"
public static class Btn
{
    public static void OnClick<T>(T target, System.Action<T> listener) { }
}

public class Target
{
    internal void Close() { }
}

public class Caller
{
    public void Wire()
    {
        var t = new Target();
        Btn.OnClick(t, x => x.Close());
    }
}
";
            var nonPublicTree = CSharpSyntaxTree.ParseText(nonPublicZeroArgSource);
            var expectedNonPublic = ExpectedAt(DiagnosticDescriptors.SB1202, MethodNameNode(nonPublicTree, "Close"));

            await AnalyzerVerifier.VerifyAsync(nonPublicZeroArgSource, expectedNonPublic);
        }

        [Fact]
        public void Analyzer_NudgeSeverities_MatchDisciplineRule()
        {
            var errorIds = DiagnosticDescriptors.All
                .Where(d => d.DefaultSeverity == DiagnosticSeverity.Error)
                .Select(d => d.Id)
                .ToHashSet();

            Assert.Equal(new HashSet<string> { "SB1001", "SB1002", "SB1003", "SB1201" }, errorIds);

            foreach (var descriptor in DiagnosticDescriptors.All.Where(d => d.Id.StartsWith("SB11")))
            {
                Assert.True(
                    descriptor.DefaultSeverity is DiagnosticSeverity.Warning or DiagnosticSeverity.Info,
                    $"{descriptor.Id} must be Warning or Info, was {descriptor.DefaultSeverity}");
            }

            Assert.Equal(DiagnosticSeverity.Warning, DiagnosticDescriptors.ById["SB1202"].DefaultSeverity);
        }
    }
}
