using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SceneBuilder.Core.Tests
{
    // Binds emitted builder source against the REAL com.codescenes/Runtime authoring surface, so a
    // test can assert applied source actually COMPILES rather than merely parses. A syntax-only
    // check (CSharpSyntaxTree.ParseText + GetDiagnostics) cannot see a type mismatch between an
    // emitted handle token and the parameter it is assigned to -- only a full compilation can.
    internal static class AuthoringBindHarness
    {
        private static string RepoRoot([CallerFilePath] string here = "")
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !File.Exists(Path.Combine(dir, "SceneBuilder.sln")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            if (dir == null)
            {
                throw new InvalidOperationException(
                    $"AuthoringBindHarness.RepoRoot: no SceneBuilder.sln found walking up from '{here}'");
            }

            return dir;
        }

        // Every com.codescenes/Runtime/*.cs source EXCEPT the runtime MonoBehaviours, which need the
        // UnityEngine assembly this harness does not reference. Excluded by CONTENT ("using
        // UnityEngine"), never a hardcoded filename pair, so a new authoring file is included
        // automatically and a new MonoBehaviour is excluded automatically.
        internal static string[] AuthoringSources()
        {
            var runtimeDir = Path.Combine(RepoRoot(), "com.codescenes", "Runtime");
            return Directory.EnumerateFiles(runtimeDir, "*.cs")
                .Select(File.ReadAllText)
                .Where(text => !text.Contains("using UnityEngine"))
                .ToArray();
        }

        // The Game.DoorOpener fixture type + its supporting UnityEngine/Prefabs stubs, shared by
        // every test that binds an emitted instance-target or override expression against a
        // component field. One copy so a fixture type change lands in one place.
        internal const string DoorOpenerStubs = @"
namespace UnityEngine { public class GameObject { } public class Material { } }
namespace Game
{
    public class DoorOpener
    {
        public UnityEngine.GameObject target;
        public float speed;
        public UnityEngine.Material mat;
    }
}
public static class Prefabs { public static TankRef Tank => null; }
public class TankRef : SceneBuilder.Authoring.PrefabRef { }
";

        // Binds [builderSource] + [typeStubs] + the real authoring sources and returns every C#
        // error diagnostic's message text.
        internal static IReadOnlyList<string> BindErrors(string builderSource, string typeStubs)
        {
            var trees = new List<SyntaxTree>
            {
                CSharpSyntaxTree.ParseText(builderSource),
                CSharpSyntaxTree.ParseText(typeStubs),
            };
            foreach (var source in AuthoringSources())
            {
                trees.Add(CSharpSyntaxTree.ParseText(source));
            }

            var corlibDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Func<>).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(corlibDir, "System.Runtime.dll")),
            };

            var compilation = CSharpCompilation.Create(
                "AuthoringBindHarnessAssembly",
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToArray();
        }
    }
}
