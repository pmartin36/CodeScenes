using System;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Validation;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t2: DiskResolutionProvider — the headless, off-disk twin of the editor's
    // UnityResolutionProvider (b3-t1). Exercised against real Roslyn-emitted DLLs and real
    // .meta files on disk, without any Unity process, so it must mirror
    // ComponentTypeResolver's usings-aware name resolution (see research.md).
    public class DiskResolutionProviderTests : IDisposable
    {
        private readonly string _tempRoot;

        public DiskResolutionProviderTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "DiskResolutionProviderTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }

        // Compiles a tiny in-memory C# source to a real on-disk DLL so the provider can be
        // exercised against real Roslyn MetadataReferences without any Unity install.
        private string EmitFixtureAssembly(string source, string assemblyName)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { tree },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var dllPath = Path.Combine(_tempRoot, assemblyName + ".dll");
            var emitResult = compilation.Emit(dllPath);
            Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics));
            return dllPath;
        }

        private DiskResolutionProvider MakeTypeProvider()
        {
            var dll = EmitFixtureAssembly(
                "namespace Demo { public class Gadget {} }\n" +
                "namespace NsA { public class Widget {} }\n" +
                "namespace NsB { public class Widget {} }\n",
                "DiskResolutionProviderTests_Fixture");

            return new DiskResolutionProvider(new[] { dll }, projectRoot: _tempRoot, managedAvailable: true);
        }

        [Fact]
        public void ResolveComponentType_QualifiedName_Resolved()
        {
            var provider = MakeTypeProvider();

            var result = provider.ResolveComponentType(new TypeRef("Demo.Gadget"), Array.Empty<string>());

            var resolved = Assert.IsType<TypeResolution.Resolved>(result);
            Assert.Equal("Demo.Gadget", resolved.FullName);
        }

        [Fact]
        public void ResolveComponentType_BareNameWithInScopeUsing_Resolved()
        {
            var provider = MakeTypeProvider();

            var result = provider.ResolveComponentType(new TypeRef("Gadget"), new[] { "Demo" });

            var resolved = Assert.IsType<TypeResolution.Resolved>(result);
            Assert.Equal("Demo.Gadget", resolved.FullName);
        }

        [Fact]
        public void ResolveComponentType_BareNameTwoInScopeUsingsBothDefine_Ambiguous()
        {
            var provider = MakeTypeProvider();

            var result = provider.ResolveComponentType(new TypeRef("Widget"), new[] { "NsA", "NsB" });

            var ambiguous = Assert.IsType<TypeResolution.Ambiguous>(result);
            Assert.Contains("NsA.Widget", ambiguous.Candidates);
            Assert.Contains("NsB.Widget", ambiguous.Candidates);
        }

        [Fact]
        public void ResolveComponentType_BareNameNoUsingDefinesIt_Unresolved()
        {
            var provider = MakeTypeProvider();

            var result = provider.ResolveComponentType(new TypeRef("Nope"), new[] { "Demo" });

            Assert.IsType<TypeResolution.Unresolved>(result);
        }

        [Fact]
        public void ResolveComponentType_BareNameOutOfScopeUsing_Unresolved()
        {
            var provider = MakeTypeProvider();

            var result = provider.ResolveComponentType(new TypeRef("Gadget"), Array.Empty<string>());

            Assert.IsType<TypeResolution.Unresolved>(result);
        }

        [Fact]
        public void ResolveAssetPath_MetaExists_ResolvedWithGuidFromMetaFile()
        {
            var materialsDir = Path.Combine(_tempRoot, "Assets", "Materials");
            Directory.CreateDirectory(materialsDir);
            File.WriteAllText(Path.Combine(materialsDir, "Red.mat"), "fake material");
            File.WriteAllText(
                Path.Combine(materialsDir, "Red.mat.meta"),
                "fileFormatVersion: 2\nguid: abc123def4567890abc123def456789\n");

            var provider = new DiskResolutionProvider(Array.Empty<string>(), _tempRoot, managedAvailable: true);

            var result = provider.ResolveAssetPath("Assets/Materials/Red.mat", null);

            var resolved = Assert.IsType<AssetResolution.Resolved>(result);
            Assert.Equal("abc123def4567890abc123def456789", resolved.Guid);
        }

        [Fact]
        public void ResolveAssetPath_MetaMissing_Unresolved()
        {
            var provider = new DiskResolutionProvider(Array.Empty<string>(), _tempRoot, managedAvailable: true);

            var result = provider.ResolveAssetPath("Assets/Materials/DoesNotExist.mat", null);

            Assert.IsType<AssetResolution.Unresolved>(result);
        }

        [Fact]
        public void ResolveAssetPath_SubAssetRequested_Deferred()
        {
            var materialsDir = Path.Combine(_tempRoot, "Assets", "Materials");
            Directory.CreateDirectory(materialsDir);
            File.WriteAllText(Path.Combine(materialsDir, "Red.mat.meta"), "guid: abc123def4567890abc123def456789\n");

            var provider = new DiskResolutionProvider(Array.Empty<string>(), _tempRoot, managedAvailable: true);

            var result = provider.ResolveAssetPath("Assets/Materials/Red.mat", "Sub");

            Assert.IsType<AssetResolution.Deferred>(result);
        }

        [Fact]
        public void ResolveBuiltin_AnyName_Deferred()
        {
            var provider = new DiskResolutionProvider(Array.Empty<string>(), _tempRoot, managedAvailable: true);

            var result = provider.ResolveBuiltin("Cube", null);

            Assert.IsType<AssetResolution.Deferred>(result);
        }

        [Fact]
        public void ResolveComponentType_ManagedUnavailable_Deferred_NotUnresolved()
        {
            var provider = new DiskResolutionProvider(Array.Empty<string>(), _tempRoot, managedAvailable: false);

            var result = provider.ResolveComponentType(new TypeRef("Anything"), Array.Empty<string>());

            Assert.IsType<TypeResolution.Deferred>(result);
        }

        [Fact]
        public void ResolveAssetPath_ManagedUnavailable_Deferred_NotUnresolved()
        {
            var provider = new DiskResolutionProvider(Array.Empty<string>(), _tempRoot, managedAvailable: false);

            var result = provider.ResolveAssetPath("Assets/Materials/Red.mat", null);

            Assert.IsType<AssetResolution.Deferred>(result);
        }

        [Fact]
        public void ResolveComponentType_GarbageInput_NeverThrows()
        {
            var provider = new DiskResolutionProvider(Array.Empty<string>(), _tempRoot, managedAvailable: true);

            var exception = Record.Exception(() =>
                provider.ResolveComponentType(new TypeRef("!!!not a real type!!!"), new[] { "Also.Not.Real" }));

            Assert.Null(exception);
        }

        [Fact]
        public void ResolveAssetPath_GarbageInput_NeverThrows()
        {
            var provider = new DiskResolutionProvider(Array.Empty<string>(), _tempRoot, managedAvailable: true);

            var exception = Record.Exception(() => provider.ResolveAssetPath("Assets/does-not/exist/at/all.mat", "?!"));

            Assert.Null(exception);
        }
    }
}
