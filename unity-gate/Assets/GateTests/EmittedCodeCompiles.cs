using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// The COMPILABILITY gate for emitted builder source.
//
// The round-trip tests hand builder source to Build/Sync as strings, and Roslyn PARSES text — it
// never COMPILES it. So the suite proved sync semantics and never proved the one property the
// user's real builder demands: the file lives in Assets/ and Unity compiles it. Three shipped bugs
// escaped through that hole (`1.53` vs `1.53f`; bare ObjectReference/LayerMask tokens; a bare
// `Asset(...)` call with no `using static SceneBuilder.Authoring.AssetRefs;`) — all of the same
// class: "the generated C# does not compile."
//
// SyncAndAssertCompiles is the seam that closes the class: scene->code tests call it INSTEAD of
// SceneBuilderSync.Run, so the compile assertion is inherited by default and a future test cannot
// silently skip it by forgetting to opt in.
public static class EmittedCodeCompiles
{
    private static MetadataReference[] _references;

    // Every loaded assembly with a real file location: the Authoring/Editor assemblies, UnityEngine,
    // and mscorlib/netstandard in one shot — the same surface the user's builder compiles against.
    private static MetadataReference[] References()
    {
        if (_references != null)
        {
            return _references;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            string location;
            try
            {
                location = assembly.Location;
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(location) || !File.Exists(location) || !seen.Add(location))
            {
                continue;
            }

            try
            {
                refs.Add(MetadataReference.CreateFromFile(location));
            }
            catch
            {
                // Not a readable managed assembly — nothing the builder could reference anyway.
            }
        }

        _references = refs.ToArray();
        return _references;
    }

    /// <summary>
    /// Asserts <paramref name="source"/> COMPILES with zero errors. Reports every error diagnostic
    /// plus the offending source, so a failure names the exact defect in the emission.
    /// </summary>
    public static void AssertCompiles(string source, string context)
    {
        var compilation = CSharpCompilation.Create(
            "SceneBuilderEmittedCheck",
            new[] { CSharpSyntaxTree.ParseText(source) },
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count == 0)
        {
            return;
        }

        var message = new StringBuilder();
        message.AppendLine($"{context}: emitted builder source DOES NOT COMPILE ({errors.Count} error(s)).");
        message.AppendLine("The builder file lives in Assets/ and IS compiled by Unity — code that only parses is a shipped bug.");
        foreach (var error in errors)
        {
            var line = error.Location.GetLineSpan().StartLinePosition.Line + 1;
            message.AppendLine($"  line {line}: {error.Id}: {error.GetMessage()}");
        }

        message.AppendLine("---- emitted source ----");
        message.AppendLine(source);

        Assert.Fail(message.ToString());
    }

    /// <summary>
    /// Runs the real scene-&gt;code sync and asserts the builder source it wrote COMPILES. Use this
    /// in place of <see cref="SceneBuilderSync.Run"/> everywhere in the gate.
    /// </summary>
    public static SceneBuilderSync.SyncResult SyncAndAssertCompiles(string builderPath, string sidecarPath, Scene scene)
    {
        var result = SceneBuilderSync.Run(builderPath, sidecarPath, scene);
        AssertCompiles(
            File.ReadAllText(builderPath),
            $"After SceneBuilderSync.Run on {Path.GetFileName(builderPath)}");
        return result;
    }
}
