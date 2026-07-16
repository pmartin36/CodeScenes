using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Validation;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t3: HeadlessValidator.Validate(builderFile, layout) — pure wiring of
    // file-read -> BuilderParser.Parse -> DiskResolutionProvider(layout) -> PlanningValidator
    // (the SAME shared walk the editor Build drives). No Unity process. See research.md
    // Test surface for the four suggested cases.
    public class HeadlessValidatorTests : IDisposable
    {
        private readonly string _tempRoot;

        public HeadlessValidatorTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "HeadlessValidatorTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }

        private string MakeUnityProject(string name)
        {
            var root = Path.Combine(_tempRoot, name);
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            Directory.CreateDirectory(Path.Combine(root, "Library", "ScriptAssemblies"));
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            return root;
        }

        // Compiles a tiny in-memory C# source to a real on-disk DLL, mirroring
        // DiskResolutionProviderTests.EmitFixtureAssembly, so the managed-override dir holds a
        // real Roslyn-emitted assembly without any Unity install.
        private string EmitFixtureAssembly(string root, string source, string assemblyName)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { tree },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var dllPath = Path.Combine(root, assemblyName + ".dll");
            var emitResult = compilation.Emit(dllPath);
            Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics));
            return dllPath;
        }

        private static string BuilderSource(string typeFullName) => $@"
public class CleanScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Add(""Player"").Component<{typeFullName}>(c => c.Set(""m_Mass"", 1f));
    }}
}}
";

        [Fact]
        public void Validate_CleanBuilder_ManagedAvailable_OkNoSkips()
        {
            var root = MakeUnityProject("proj1");
            var managedDir = Path.Combine(_tempRoot, "Managed1");
            Directory.CreateDirectory(managedDir);
            EmitFixtureAssembly(managedDir, "namespace Demo { public class Gadget {} }", "HeadlessValidatorTests_Fixture1");

            var builderPath = Path.Combine(root, "Foo.cs");
            File.WriteAllText(builderPath, BuilderSource("Demo.Gadget"));

            var layout = ProjectLayout.Infer(builderPath, managedOverride: managedDir);

            var result = HeadlessValidator.Validate(builderPath, layout);

            Assert.True(result.Ok);
            Assert.Empty(result.Result.Diagnostics);
            Assert.Empty(result.Skipped);
        }

        [Fact]
        public void Validate_ManagedMissing_ReportsSkippedNotPassed()
        {
            var root = MakeUnityProject("proj2");
            var builderPath = Path.Combine(root, "Foo.cs");
            File.WriteAllText(builderPath, BuilderSource("Demo.DoesNotExistAnywhere"));

            var layout = ProjectLayout.Infer(builderPath, managedOverride: Path.Combine(_tempRoot, "NoSuchManagedDir"));
            Assert.False(layout.ManagedDllsAvailable); // sanity: managed genuinely unavailable

            var result = HeadlessValidator.Validate(builderPath, layout);

            Assert.Equal(new[] { "type", "asset" }, result.Skipped);
            Assert.True(result.Ok);
            Assert.Empty(result.Result.Diagnostics);
        }

        [Fact]
        public void Validate_BadType_WiresSharedWalk_MatchesDirectPlanningValidatorRun()
        {
            var root = MakeUnityProject("proj3");
            var managedDir = Path.Combine(_tempRoot, "Managed3");
            Directory.CreateDirectory(managedDir);
            // The fixture assembly deliberately does NOT define Demo.NotThere.
            EmitFixtureAssembly(managedDir, "namespace Demo { public class Gadget {} }", "HeadlessValidatorTests_Fixture3");

            var builderPath = Path.Combine(root, "Foo.cs");
            var source = BuilderSource("Demo.NotThere");
            File.WriteAllText(builderPath, source);

            var layout = ProjectLayout.Infer(builderPath, managedOverride: managedDir);

            var result = HeadlessValidator.Validate(builderPath, layout);

            var diagnostic = Assert.Single(result.Result.Diagnostics);
            Assert.Equal(DiagnosticCodes.UnresolvedType, diagnostic.Code);
            Assert.False(result.Ok);

            // Prove HeadlessValidator only wires the shared walk, adding no logic of its own: a
            // direct PlanningValidator run over the same parse + a fresh DiskResolutionProvider
            // must yield the identical diagnostic set.
            var parse = BuilderParser.Parse(source);
            var directResult = PlanningValidator.Validate(parse, source, new DiskResolutionProvider(layout), builderPath);

            Assert.Equal(
                directResult.Diagnostics.Select(d => (d.Code, d.Line, d.Col)),
                result.Result.Diagnostics.Select(d => (d.Code, d.Line, d.Col)));
        }

        [Fact]
        public void Validate_File_PopulatesDiagnosticFileAndLocation()
        {
            var root = MakeUnityProject("proj4");
            var managedDir = Path.Combine(_tempRoot, "Managed4");
            Directory.CreateDirectory(managedDir);
            EmitFixtureAssembly(managedDir, "namespace Demo { public class Gadget {} }", "HeadlessValidatorTests_Fixture4");

            var builderPath = Path.Combine(root, "Foo.cs");
            File.WriteAllText(builderPath, BuilderSource("Demo.NotThereEither"));

            var layout = ProjectLayout.Infer(builderPath, managedOverride: managedDir);

            var result = HeadlessValidator.Validate(builderPath, layout);

            var diagnostic = Assert.Single(result.Result.Diagnostics);
            Assert.Equal(builderPath, diagnostic.File);
            Assert.True(diagnostic.Line > 0);
        }
    }
}
