using System.Threading.Tasks;
using CodeScenes.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SceneBuilder.Analyzers.Tests
{
    // Thin harness over CSharpAnalyzerTest<BuilderAnalyzer, DefaultVerifier> (b2-t2 gap flagged by
    // b2-t1's ADVISORY). CompilerDiagnostics.None matches RecognizerTests.cs's posture: sample
    // builder sources reference unbound ISceneDefinition/SceneRoot/etc. — the analyzer is purely
    // syntactic and never needs those to resolve, so ordinary CS0246-family compiler diagnostics
    // must not be conflated with the SB1xxx diagnostics under test.
    internal static class AnalyzerVerifier
    {
        public static async Task VerifyAsync(string source, params DiagnosticResult[] expected)
        {
            var test = new CSharpAnalyzerTest<BuilderAnalyzer, DefaultVerifier>
            {
                TestCode = source,
                CompilerDiagnostics = CompilerDiagnostics.None,
            };
            test.ExpectedDiagnostics.AddRange(expected);

            await test.RunAsync();
        }

        // Built from (Id, DefaultSeverity), NOT the DiagnosticDescriptor ctor: the latter captures
        // MessageFormat and compares it verbatim against the analyzer's real message unless
        // .WithArguments(...) supplies the "{0}" substitution — SB1001-1003's MessageFormat is the
        // literal "{0}", which the analyzer can never emit (it always substitutes the recognizer's
        // real message). research.md's PUBLIC_SURFACE asserts Id+severity+span, not message text.
        public static DiagnosticResult Diagnostic(DiagnosticDescriptor descriptor) =>
            new(descriptor.Id, descriptor.DefaultSeverity);
    }
}
