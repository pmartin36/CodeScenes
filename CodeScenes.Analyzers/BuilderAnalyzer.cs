using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CodeScenes.Analyzers
{
    /// <summary>
    /// SupportedDiagnostics delegates to the DiagnosticDescriptors registry. b2-t2 wires the
    /// SB100x flat-shape check; b2-t3 wires the SB11xx nudges; b2-t4 wires the SB120x UnityEvent
    /// signature scaffold.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class BuilderAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => DiagnosticDescriptors.All;

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(FlatShapeAnalysis.AnalyzeCompilationUnit, SyntaxKind.CompilationUnit);
            context.RegisterSyntaxNodeAction(NudgeAnalysis.AnalyzeCompilationUnit, SyntaxKind.CompilationUnit);
            context.RegisterSyntaxNodeAction(UnityEventAnalysis.AnalyzeCompilationUnit, SyntaxKind.CompilationUnit);
        }
    }
}
