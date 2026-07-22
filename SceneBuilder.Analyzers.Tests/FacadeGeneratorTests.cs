using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using CodeScenes.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Analyzers.Tests
{
    // Tests for b3-t1: FacadeEmit — manifest JSON -> façade source (Prefabs catalog + per-prefab
    // containers). Unlike CatalogSourceGenerator (GeneratorTests.cs), there is no
    // IIncrementalGenerator yet (that is b3-t2) — FacadeEmit is called directly, and the emitted
    // source is compile-checked via FacadeCompileHarness. See
    // .agent_handoffs/typed-prefab-facades/b3-t1/research.md "Test surface".
    public class FacadeGeneratorTests
    {
        // Tank/Turret/Barrel/Chassis/Wheel/Wheel2/FrontWheel — mirrors this task's DELIVERABLE
        // probe exactly. Wheel and Wheel2 share Node.TypeName "WheelRef" (dedup-by-TypeName:
        // composition-by-reference means both properties must resolve to ONE emitted type).
        // internal (not private): reused by PrefabFacadeGeneratorTests (b3-t2) below — same manifest
        // shape drives both the direct-FacadeEmit tests (b3-t1) and the generator-wiring tests (b3-t2).
        internal static FacadeCatalog TankCatalog() => new FacadeCatalog
        {
            SchemaVersion = 1,
            Entries = new[]
            {
                new FacadeEntry
                {
                    PropertyName = "Tank",
                    Guid = "11111111111111111111111111111111",
                    Root = new FacadeNode
                    {
                        TypeName = "TankRef",
                        Children = new[]
                        {
                            new FacadeChild
                            {
                                PropertyName = "Turret",
                                RealName = "Turret",
                                LocalId = 100,
                                Node = new FacadeNode
                                {
                                    TypeName = "TurretRef",
                                    Children = new[]
                                    {
                                        new FacadeChild
                                        {
                                            PropertyName = "Barrel",
                                            RealName = "Barrel",
                                            LocalId = 101,
                                            Node = new FacadeNode { TypeName = "BarrelRef" },
                                        },
                                    },
                                },
                            },
                            new FacadeChild
                            {
                                PropertyName = "Chassis",
                                RealName = "Chassis",
                                LocalId = 200,
                                Node = new FacadeNode
                                {
                                    TypeName = "ChassisRef",
                                    Children = new[]
                                    {
                                        new FacadeChild
                                        {
                                            PropertyName = "Wheel",
                                            RealName = "Wheel",
                                            LocalId = 201,
                                            Node = new FacadeNode { TypeName = "WheelRef" },
                                        },
                                        new FacadeChild
                                        {
                                            PropertyName = "Wheel2",
                                            RealName = "Wheel",
                                            LocalId = 202,
                                            Node = new FacadeNode { TypeName = "WheelRef" },
                                        },
                                        new FacadeChild
                                        {
                                            PropertyName = "FrontWheel",
                                            RealName = "Front Wheel",
                                            LocalId = 203,
                                            Node = new FacadeNode { TypeName = "FrontWheelRef" },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        [Fact]
        public void Generate_SameHierarchy_ByteIdentical()
        {
            var json = TankCatalog().Serialize();

            var okA = FacadeEmit.TryEmitFacades(json, out var sourceA);
            var okB = FacadeEmit.TryEmitFacades(json, out var sourceB);

            Assert.True(okA);
            Assert.True(okB);
            // Guard against a vacuous empty-string pass: byte-stability is only a meaningful
            // contract if there IS generated source for a present, non-empty manifest.
            Assert.False(string.IsNullOrEmpty(sourceA));
            Assert.Equal(sourceA, sourceB);
        }

        [Fact]
        public void Generator_SanitizesToValidUniqueIdentifiers()
        {
            var ok = FacadeEmit.TryEmitFacades(TankCatalog().Serialize(), out var source);
            Assert.True(ok);

            const string probe = @"
public class Probe
{
    public void Use()
    {
        var t = Prefabs.Tank;
        var b = t.Turret.Barrel;
        var w = t.Chassis.Wheel;
        var w2 = t.Chassis.Wheel2;
        var f = t.Chassis.FrontWheel;
    }
}
";
            var compilation = FacadeCompileHarness.Compile(source, probe);
            var hasErrors = FacadeCompileHarness.HasErrors(compilation, out var errorText);
            Assert.False(
                hasErrors,
                "expected the emitted façade source + probe to compile clean (no CS0102 duplicate-member for the deduped WheelRef type):\n" + errorText);
        }

        [Fact]
        public void Generator_TwoPrefabsSameChildName_PerPrefabContainersIsolate_Compile()
        {
            var catalog = new FacadeCatalog
            {
                SchemaVersion = 1,
                Entries = new[]
                {
                    new FacadeEntry
                    {
                        PropertyName = "Alpha",
                        Guid = "11111111111111111111111111111111",
                        Root = new FacadeNode
                        {
                            TypeName = "AlphaRef",
                            Children = new[]
                            {
                                new FacadeChild
                                {
                                    PropertyName = "Turret",
                                    RealName = "Turret",
                                    LocalId = 1,
                                    Node = new FacadeNode { TypeName = "TurretRef" },
                                },
                            },
                        },
                    },
                    new FacadeEntry
                    {
                        PropertyName = "Beta",
                        Guid = "22222222222222222222222222222222",
                        Root = new FacadeNode
                        {
                            TypeName = "BetaRef",
                            Children = new[]
                            {
                                new FacadeChild
                                {
                                    PropertyName = "Turret",
                                    RealName = "Turret",
                                    LocalId = 1,
                                    Node = new FacadeNode { TypeName = "TurretRef" },
                                },
                            },
                        },
                    },
                },
            };

            var ok = FacadeEmit.TryEmitFacades(catalog.Serialize(), out var source);
            Assert.True(ok);

            const string probe = @"
public class Probe
{
    public void Use()
    {
        var a = Prefabs.Alpha.Turret;
        var b = Prefabs.Beta.Turret;
    }
}
";
            var compilation = FacadeCompileHarness.Compile(source, probe);
            var hasErrors = FacadeCompileHarness.HasErrors(compilation, out var errorText);
            Assert.False(
                hasErrors,
                "expected per-prefab namespace isolation (both prefabs emit their own TurretRef type in different namespaces) — no CS0101 assembly-scope collision:\n" + errorText);
        }
    }

    // Compiles [façade source] + [probe] + [PrefabRef stub] via a plain CSharpCompilation — there
    // is no IIncrementalGenerator to drive yet (that is b3-t2), so this does NOT use
    // GeneratorHarness/InMemoryAdditionalText; it mirrors only their diagnostic-inspection pattern.
    internal static class FacadeCompileHarness
    {
        private const string PrefabRefStub =
            "namespace SceneBuilder.Authoring { public class PrefabRef {} }";

        public static Compilation Compile(string facadeSource, string? probeSource = null)
        {
            var syntaxTrees = new List<SyntaxTree>
            {
                CSharpSyntaxTree.ParseText(PrefabRefStub),
                CSharpSyntaxTree.ParseText(facadeSource),
            };
            if (probeSource is not null)
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(probeSource));

            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            };

            return CSharpCompilation.Create(
                "FacadeCompileHarnessAssembly",
                syntaxTrees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        public static bool HasErrors(Compilation compilation, out string errorText)
        {
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();
            errorText = string.Join("\n", errors.Select(d => d.ToString()));
            return errors.Length > 0;
        }
    }

    // Tests for b3-t2: PrefabFacadeGenerator (IIncrementalGenerator) — the wiring layer that keys
    // an AdditionalTextsProvider on ManifestFileName, calls FacadeEmit (b3-t1), and AddSources the
    // result into the compilation. Mirrors GeneratorTests.cs (CatalogSourceGenerator) but drives
    // PrefabFacadeGenerator via FacadeGeneratorHarness below. See
    // .agent_handoffs/typed-prefab-facades/b3-t2/test-writer.md "Contract".
    public class PrefabFacadeGeneratorTests
    {
        [Fact]
        public void PrefabFacadeGenerator_PresentManifest_TypedRefsResolve()
        {
            const string probe = @"
public class Probe
{
    public void Use()
    {
        var t = Prefabs.Tank;
        var b = t.Turret.Barrel;
    }
}
";
            var json = FacadeGeneratorTests.TankCatalog().Serialize();
            var (_, compilation) = FacadeGeneratorHarness.Run(json, probe);

            var hasErrors = FacadeGeneratorHarness.HasErrors(compilation, out var errorText);
            Assert.False(hasErrors, "expected zero compile errors referencing Prefabs.Tank / t.Turret.Barrel:\n" + errorText);
        }

        [Fact]
        public void PrefabFacadeGenerator_MissingManifest_EmitsNothing_ReferenceFailsToCompile()
        {
            const string probe = @"
public class Probe
{
    public object A = Prefabs.Tank;
}
";
            var (runResult, compilation) = FacadeGeneratorHarness.Run(manifestJson: null, probeSource: probe);

            Assert.Empty(runResult.GeneratedTrees);

            var hasErrors = FacadeGeneratorHarness.HasErrors(compilation, out var errorText);
            Assert.True(hasErrors, "expected Prefabs.Tank to fail to resolve when no manifest AdditionalFile is present");
            Assert.Contains("CS0103", errorText);
        }

        [Fact]
        public void PrefabFacadeGenerator_Deterministic_ByteStable()
        {
            var json = FacadeGeneratorTests.TankCatalog().Serialize();

            var (runA, _) = FacadeGeneratorHarness.Run(json);
            var (runB, _) = FacadeGeneratorHarness.Run(json);

            var textA = FacadeGeneratorHarness.CombinedGeneratedText(runA);
            var textB = FacadeGeneratorHarness.CombinedGeneratedText(runB);

            // Byte-stability is only a meaningful contract if there IS generated source for a
            // present, non-empty manifest — guard against a vacuous "emits nothing" pass.
            Assert.Contains("Prefabs", textA);
            Assert.Equal(textA, textB);
        }

        // b3-t3: the headless half of the rename safety net (spec behavior #8). A REGENERATED
        // manifest (Turret renamed to Cannon) drops the old `Turret` accessor from TankRef; a
        // builder still referencing `t.Turret` must fail to compile with a LOCATED CS1061 at the
        // stale `Turret` token — never a silent wrong value. Contrast: the same regenerated
        // catalog with the NEW `t.Cannon` accessor compiles clean, proving the emit itself is
        // healthy and only the stale accessor fails.
        //
        // NOTE: this behavior is already delivered by b3-t1 (FacadeEmit) + b3-t2
        // (PrefabFacadeGenerator) — this is a characterization/safety-net test, not a genuine RED
        // baseline (it passes immediately). Validity was confirmed by temporarily inverting the
        // located-diagnostic assertion (asserting zero errors for the stale probe) and observing
        // that inverted assertion fail — see test-writer.md RED_OUTPUT.
        [Fact]
        public void StaleChain_AfterRegen_FailsCompileCheck()
        {
            var json = RegeneratedTankCatalog().Serialize();

            const string staleProbe = @"
public class Probe
{
    public void Use()
    {
        var t = Prefabs.Tank;
        var b = t.Turret.Barrel;
    }
}
";
            var (_, staleCompilation) = FacadeGeneratorHarness.Run(json, staleProbe);
            var staleErrors = staleCompilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            var cs1061 = Assert.Single(staleErrors, d => d.Id == "CS1061");
            var span = cs1061.Location.SourceTree!.GetText().ToString(cs1061.Location.SourceSpan);
            Assert.Equal(
                "Turret",
                span);

            const string validProbe = @"
public class Probe
{
    public void Use()
    {
        var t = Prefabs.Tank;
        var b = t.Cannon.Barrel;
    }
}
";
            var (_, validCompilation) = FacadeGeneratorHarness.Run(json, validProbe);
            var hasErrors = FacadeGeneratorHarness.HasErrors(validCompilation, out var errorText);
            Assert.False(
                hasErrors,
                "expected the regenerated Cannon accessor to compile clean (proves the emit itself is healthy and only the stale Turret accessor fails):\n" + errorText);
        }

        // Tank/Cannon(was Turret)/Barrel — the same manifest as TankCatalog() with the Turret
        // child renamed to Cannon (PropertyName "Cannon", Node.TypeName "CannonRef"), LocalId
        // unchanged. Mirrors b1-t3's Rename re-derivation semantics (RealName/PropertyName/
        // TypeName re-derive off the same LocalId). Chassis subtree omitted — not needed to prove
        // the stale-member contract.
        internal static FacadeCatalog RegeneratedTankCatalog() => new FacadeCatalog
        {
            SchemaVersion = 1,
            Entries = new[]
            {
                new FacadeEntry
                {
                    PropertyName = "Tank",
                    Guid = "11111111111111111111111111111111",
                    Root = new FacadeNode
                    {
                        TypeName = "TankRef",
                        Children = new[]
                        {
                            new FacadeChild
                            {
                                PropertyName = "Cannon",
                                RealName = "Cannon",
                                LocalId = 100,
                                Node = new FacadeNode
                                {
                                    TypeName = "CannonRef",
                                    Children = new[]
                                    {
                                        new FacadeChild
                                        {
                                            PropertyName = "Barrel",
                                            RealName = "Barrel",
                                            LocalId = 101,
                                            Node = new FacadeNode { TypeName = "BarrelRef" },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    // Runs PrefabFacadeGenerator via CSharpGeneratorDriver — mirrors GeneratorHarness
    // (InMemoryAdditionalText.cs), which is hardwired to CatalogSourceGenerator. Reuses the same
    // InMemoryAdditionalText fixture (same namespace/assembly; not modified here).
    internal static class FacadeGeneratorHarness
    {
        private const string PrefabRefStub =
            "namespace SceneBuilder.Authoring { public class PrefabRef {} }";

        public static (GeneratorDriverRunResult RunResult, Compilation OutputCompilation) Run(
            string? manifestJson,
            string? probeSource = null,
            string manifestPath = "/fake/SceneBuilders/Generated/" + PrefabFacadeGenerator.ManifestFileName)
        {
            var additionalTexts = manifestJson is null
                ? ImmutableArray<AdditionalText>.Empty
                : ImmutableArray.Create<AdditionalText>(new InMemoryAdditionalText(manifestPath, manifestJson));

            var syntaxTrees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(PrefabRefStub) };
            if (probeSource is not null)
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(probeSource));

            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            };

            var compilation = CSharpCompilation.Create(
                "PrefabFacadeGeneratorHarnessAssembly",
                syntaxTrees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(new PrefabFacadeGenerator())
                .AddAdditionalTexts(additionalTexts);

            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

            return (driver.GetRunResult(), outputCompilation);
        }

        public static string CombinedGeneratedText(GeneratorDriverRunResult runResult) =>
            string.Join(
                "\n",
                runResult.Results
                    .SelectMany(r => r.GeneratedSources)
                    .Select(s => s.SourceText.ToString()));

        public static bool HasErrors(Compilation compilation, out string errorText)
        {
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();
            errorText = string.Join("\n", errors.Select(d => d.ToString()));
            return errors.Length > 0;
        }
    }
}
