using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using CodeScenes.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Analyzers.Tests
{
    // Test #20 (b4-t1): "generated C# must compile" strengthened to "compiles AND is
    // analyzer-clean". Drives the REAL Reconcile/SourcePatch emit path (never fabricated output
    // strings) through the real BuilderAnalyzer and asserts zero Error-severity diagnostics.
    // Non-Error nudges (SB11xx Info/Warning) on emitted code are expected and tolerated — see
    // .agent_handoffs/codescenes-analyzers/b4-t1/research.md.
    public class EmittedCodeAnalyzerCleanTests
    {
        private static Dictionary<string, SourceSpan> MergeAnchors(ParseResult parsed)
        {
            var merged = new Dictionary<string, SourceSpan>(parsed.Anchors);
            foreach (var kv in parsed.ComponentAnchors)
            {
                merged[kv.Key] = kv.Value;
            }
            return merged;
        }

        private static string Emit(string seed, SourcePatch patch)
        {
            var parsed = BuilderParser.Parse(seed);
            var anchors = MergeAnchors(parsed);
            return SourcePatchApplier.Apply(seed, patch, anchors);
        }

        private static ImmutableArray<Diagnostic> AnalyzerErrors(string builderSource)
        {
            var compilation = CSharpCompilation.Create(
                "EmitCleanCorpus",
                new[] { CSharpSyntaxTree.ParseText(builderSource) },
                references: System.Array.Empty<MetadataReference>(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var withAnalyzers = compilation.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new BuilderAnalyzer()));

            var diagnostics = withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();

            return diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        }

        private const string AppendFlagsFixture = @"
public class AppendFlagsScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var existing = scene.Add(""Existing"");
    }
}
";

        private const string AppendIntroduceHandleFixture = @"
public class AppendIntroduceHandleScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Parent has no handle yet.
        scene.Add(""Parent"");

        // Unrelated sibling below, must stay untouched.
        var sibling = scene.Add(""Sibling"");
    }
}
";

        private const string AppendComponentFixture = @"
public class AppendComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Existing crate, untouched otherwise.
        var crate = scene.Add(""Crate"");

        // Unrelated sibling below, must stay untouched.
        var sibling = scene.Add(""Sibling"");
    }
}
";

        private const string PlainInstanceFixture = @"
public class PlainInstanceScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
    }
}
";

        private const string PatchComponentFieldFixture = @"
public class PatchComponentFieldScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Existing crate with an attached Rigidbody.
        var crate = scene.Add(""Crate"");
        crate.Component<UnityEngine.Rigidbody>(rb => rb.Set(""m_Mass"", 5f));

        // Unrelated sibling below, must stay untouched.
        var sibling = scene.Add(""Sibling"");
    }
}
";

        public static IEnumerable<object[]> EmitScenarios()
        {
            // APPEND root w/ flags+transform (SourcePatchAppendStatementTests.AppendFlagsFixture family).
            yield return new object[]
            {
                "AppendFlags",
                AppendFlagsFixture,
                new SourcePatch
                {
                    Edits = new SourceEdit[]
                    {
                        new AppendStatement
                        {
                            NewLogicalId = "Flagged/1",
                            Name = "Flagged",
                            Tag = "Player",
                            Layer = 5,
                            Active = false,
                            IsStatic = true,
                        },
                    },
                },
            };

            // APPEND child + introduce parent handle (SourcePatchAppendStatementTests.AppendIntroduceHandleFixture family).
            yield return new object[]
            {
                "AppendIntroduceParentHandle",
                AppendIntroduceHandleFixture,
                new SourcePatch
                {
                    Edits = new SourceEdit[]
                    {
                        new AppendStatement
                        {
                            NewLogicalId = "parent/NewChild/0",
                            ParentAnchor = "Parent/0",
                            ParentHandle = "parent",
                            IntroduceParentHandle = true,
                            Name = "NewChild",
                        },
                    },
                },
            };

            // APPEND component, string-key Set (SourcePatchApplyTests.AppendComponentFixture family).
            yield return new object[]
            {
                "AppendComponentStringKeySet",
                AppendComponentFixture,
                new SourcePatch
                {
                    Edits = new SourceEdit[]
                    {
                        new AppendComponentStatement
                        {
                            Anchor = "crate",
                            ComponentLogicalId = "crate/UnityEngine.Rigidbody#0",
                            TypeFullName = "UnityEngine.Rigidbody",
                            Fields = new FieldMap(new[]
                            {
                                new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(7f)),
                            }),
                        },
                    },
                },
            };

            // INSTANCE override append, typed selector (SourcePatchInstanceTests family).
            yield return new object[]
            {
                "InstanceOverrideTypedSelector",
                PlainInstanceFixture,
                new SourcePatch
                {
                    Edits = new SourceEdit[]
                    {
                        new AppendInstanceOverride
                        {
                            Anchor = "enemy",
                            Sets = new[]
                            {
                                new OverrideSetSpec
                                {
                                    TypeFullName = "Health",
                                    PropertyPath = "member:health",
                                    Value = ValueNode.Primitive.Int(50),
                                },
                            },
                        },
                    },
                },
            };
        }

        [Theory]
        [MemberData(nameof(EmitScenarios))]
        public void EmittedBuilder_IsAnalyzerClean(string scenarioName, string seed, SourcePatch patch)
        {
            var emitted = Emit(seed, patch);

            var errors = AnalyzerErrors(emitted);

            Assert.True(
                errors.IsEmpty,
                $"Scenario '{scenarioName}' tripped {errors.Length} Error diagnostic(s) on emitted code: " +
                string.Join("; ", errors.Select(d => $"{d.Id} @ {d.Location.GetLineSpan()}")) +
                $"\nEmitted:\n{emitted}");
        }

        [Fact]
        public void EmittedBuilder_PatchComponentField_IsAnalyzerClean()
        {
            var parsed = BuilderParser.Parse(PatchComponentFieldFixture);
            var anchors = MergeAnchors(parsed);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);
            var valueSpan = parsed.FieldArgumentSpans[compId]["m_Mass"];

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchComponentField
                    {
                        Anchor = compId,
                        ValueSpan = valueSpan,
                        NewExpr = SourceExpr.ValueNodeLiteral(ValueNode.Primitive.Float(8f)),
                    },
                },
            };

            var emitted = SourcePatchApplier.Apply(PatchComponentFieldFixture, patch, anchors);

            var errors = AnalyzerErrors(emitted);

            Assert.True(
                errors.IsEmpty,
                $"PatchComponentField tripped {errors.Length} Error diagnostic(s) on emitted code: " +
                string.Join("; ", errors.Select(d => $"{d.Id} @ {d.Location.GetLineSpan()}")) +
                $"\nEmitted:\n{emitted}");
        }
    }
}
