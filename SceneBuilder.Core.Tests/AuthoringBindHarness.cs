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

        // Full paths of every com.codescenes/Runtime/*.cs source -- the one Runtime directory walk,
        // shared by every reader of the authoring surface.
        internal static string[] RuntimeSourceFiles()
        {
            var runtimeDir = Path.Combine(RepoRoot(), "com.codescenes", "Runtime");
            return Directory.EnumerateFiles(runtimeDir, "*.cs").ToArray();
        }

        // Every com.codescenes/Runtime/*.cs source EXCEPT the runtime MonoBehaviours, which need the
        // UnityEngine assembly this harness does not reference. Excluded by CONTENT ("using
        // UnityEngine"), never a hardcoded filename pair, so a new authoring file is included
        // automatically and a new MonoBehaviour is excluded automatically.
        internal static string[] AuthoringSources()
        {
            return RuntimeSourceFiles()
                .Select(File.ReadAllText)
                .Where(text => !text.Contains("using UnityEngine"))
                .ToArray();
        }

        // The Game.DoorOpener fixture type + its supporting Prefabs stub, shared by every test that
        // binds an emitted instance-target or override expression against a component field. One
        // copy so a fixture type change lands in one place. GameObject/Material come from the
        // always-prepended UnityEngineStubs below, not declared here (a second declaration would be
        // CS0101).
        internal const string DoorOpenerStubs = @"
namespace Game
{
    public class DoorOpener
    {
        public UnityEngine.GameObject target;
        public UnityEngine.GameObject[] targets;
        public float speed;
        public UnityEngine.Material mat;
    }
}
public static class Prefabs { public static TankRef Tank => null; }
public class TankRef : SceneBuilder.Authoring.PrefabRef { }
";

        // The `.Ref<T>()`/`.OnClick(...)` compile-proof stub: a global-namespace target type
        // carrying a public zero-argument `Open()` (the wireable method) so both the positive and
        // negative binding proofs have something to compile against. `UnityEngine.UI.Button` comes
        // from the always-prepended UnityEngineStubs below. `Close()`/`SetLevel(int)`/
        // `SetSpeed(float)`/`SetLabel(string)`/`SetValueAlt(float)`/`SetMaterial(Material)` are the
        // wireable targets for the emitted-source bind proofs of the arg-literal, dynamic
        // method-group, and object/asset-argument render forms.
        internal const string UnityEventStubs = @"
public class DoorOpener
{
    public void Open() { }
    public void Close() { }
    public void SetLevel(int level) { }
    public void SetSpeed(float speed) { }
    public void SetLabel(string label) { }
    public void SetValueAlt(float value) { }
    public void SetMaterial(UnityEngine.Material material) { }
}
";

        // The shared minimal UnityEngine surface every com.codescenes/Runtime authoring member
        // might reference (ComponentHandle<T>.OnClick/OnEvent, SceneObjectHandle.As<T>, ...),
        // prepended to every BindErrors compilation so no caller has to redeclare it -- the whole
        // Runtime directory is always compiled in (AuthoringSources), regardless of which member a
        // given test exercises, so every type its signatures mention must resolve.
        private const string UnityEngineStubs = @"
namespace UnityEngine
{
    public class Object { }
    public class Component : Object { }
    public class GameObject : Object { public void SetActive(bool value) { } }
    public class MonoBehaviour : Component { }
    public class Material : Object { }
    public class Sprite : Object { }
}
namespace UnityEngine.UI { public class Button : UnityEngine.MonoBehaviour { public UnityEngine.Events.UnityEvent<float> onValueChanged; } }
namespace UnityEngine.Events
{
    public class UnityEvent { }
    public class UnityEvent<T0> { }
    public class UnityEvent<T0, T1> { }
}
";

        // UnityEventCallState is the one piece of UnityEngineStubs a caller sometimes redeclares
        // itself (a richer local enum for a specific compile proof) -- added only when the
        // caller's own typeStubs doesn't already mention it, so neither caller collides with this
        // shared default (CS0101).
        private const string UnityEventCallStateStub = @"
namespace UnityEngine.Events { public enum UnityEventCallState { Off, EditorAndRuntime, RuntimeOnly } }
";

        // Binds [builderSource] + [typeStubs] + the real authoring sources and returns every C#
        // error diagnostic's message text.
        internal static IReadOnlyList<string> BindErrors(string builderSource, string typeStubs)
        {
            var trees = new List<SyntaxTree>
            {
                CSharpSyntaxTree.ParseText(builderSource),
                CSharpSyntaxTree.ParseText(typeStubs),
                CSharpSyntaxTree.ParseText(UnityEngineStubs),
            };

            if (!typeStubs.Contains("UnityEventCallState"))
            {
                trees.Add(CSharpSyntaxTree.ParseText(UnityEventCallStateStub));
            }

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
