#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
// System.Diagnostics.Debug would otherwise collide with UnityEngine.Debug.
using Debug = UnityEngine.Debug;

namespace SceneBuilder.Editor
{
    /// <summary>A single compile error in emitted builder source.</summary>
    public sealed class BuilderDiagnostic
    {
        /// <summary>The compiler error id, e.g. <c>CS1503</c>.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>1-based line in the emitted source.</summary>
        public int Line { get; set; }

        /// <summary>The compiler's message.</summary>
        public string Message { get; set; } = string.Empty;

        /// <inheritdoc/>
        public override string ToString() => $"line {Line}: {Id}: {Message}";
    }

    /// <summary>
    /// Compiles emitted builder source with Roslyn and reports errors. This is the backstop for
    /// "the generated C# does not compile" — a class of bug that has shipped four times
    /// (<c>1.53</c> vs <c>1.53f</c>, bare <c>ObjectReference</c> tokens, <c>Asset(...)</c> without its
    /// <c>using</c>, and a CS0841 statement-ordering bug).
    /// </summary>
    /// <remarks>
    /// The builder used to live under <c>Assets/</c>, where Unity's own compiler caught these on import.
    /// Moving it to <c>&lt;ProjectRoot&gt;/SceneBuilders/</c> (so writes stop triggering domain reloads)
    /// removes that backstop, so the check moves in-process and runs after every sync that writes source.
    /// </remarks>
    public static class BuilderCompileCheck
    {
        private const string AssemblyName = "SceneBuilderEmittedCheck";

        // Both caches are pure functions of the loaded assembly set. Unity tears the AppDomain down on
        // every assembly reload, which drops these statics with it — so a reload IS the invalidation and
        // the cache can never outlive the assemblies it was built from. No manual busting needed.
        private static MetadataReference[]? _references;
        private static CSharpCompilation? _template;

        /// <summary>
        /// Wall-clock ms spent building the reference set — the dominant one-time cost, paid once per
        /// domain. Zero until it is built. Sync performance is a first-class constraint, so the real
        /// cost is measured rather than assumed.
        /// </summary>
        public static double ReferenceBuildMilliseconds { get; private set; }

        /// <summary>
        /// Wall-clock ms of the FIRST <see cref="Check"/> in this domain, which pays for binding every
        /// referenced assembly to symbols. Later checks reuse that work. Zero until one has run.
        /// </summary>
        public static double FirstCheckMilliseconds { get; private set; }

        /// <summary>
        /// Every loaded assembly with a real file location: the Authoring/Editor assemblies, UnityEngine,
        /// and mscorlib/netstandard in one shot — the same surface the user's builder compiles against.
        /// Built once per domain; reading metadata for ~200 assemblies is far too slow to redo per sync.
        /// </summary>
        public static MetadataReference[] References()
        {
            if (_references != null)
            {
                return _references;
            }

            var stopwatch = Stopwatch.StartNew();
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
            stopwatch.Stop();
            ReferenceBuildMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return _references;
        }

        // A tree-less compilation carrying just the reference set. Compilations derived from it via
        // AddSyntaxTrees inherit its ReferenceManager, so the referenced assemblies are bound to symbols
        // ONCE (on the first check) and every later check reuses that work. Creating a fresh
        // CSharpCompilation per sync would re-bind ~200 assemblies each time; this is what keeps the
        // per-sync cost proportional to the builder file rather than to the project's assembly count.
        private static CSharpCompilation Template()
        {
            return _template ??= CSharpCompilation.Create(
                AssemblyName,
                Array.Empty<SyntaxTree>(),
                References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        /// <summary>
        /// Compiles <paramref name="source"/> and returns its error diagnostics (empty when it compiles).
        /// Pure — reports, never throws or logs.
        /// </summary>
        public static IReadOnlyList<BuilderDiagnostic> Check(string source)
        {
            var first = FirstCheckMilliseconds == 0d;
            var stopwatch = first ? Stopwatch.StartNew() : null;

            var compilation = Template().AddSyntaxTrees(CSharpSyntaxTree.ParseText(source));

            var diagnostics = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => new BuilderDiagnostic
                {
                    Id = d.Id,
                    Line = d.Location.GetLineSpan().StartLinePosition.Line + 1,
                    Message = d.GetMessage(),
                })
                .ToArray();

            if (stopwatch != null)
            {
                stopwatch.Stop();
                FirstCheckMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            }

            return diagnostics;
        }

        /// <summary>
        /// Renders <paramref name="diagnostics"/> into a report naming the exact defect in the emission,
        /// including the offending source.
        /// </summary>
        public static string Format(IReadOnlyList<BuilderDiagnostic> diagnostics, string context, string source)
        {
            var message = new StringBuilder();
            message.AppendLine($"[SceneBuilder] {context}: emitted builder source DOES NOT COMPILE ({diagnostics.Count} error(s)).");
            message.AppendLine("This is a bug in SceneBuilder's emission, not in your scene edit.");
            foreach (var d in diagnostics)
            {
                message.AppendLine($"  {d}");
            }

            message.AppendLine("---- emitted source ----");
            message.AppendLine(source);
            return message.ToString();
        }

        /// <summary>
        /// Compiles <paramref name="source"/> and reports any errors to the Console via
        /// <see cref="Debug.LogError(object)"/>. Returns the errors (empty when it compiles) so callers
        /// can surface them too. Never throws: a broken check must not break the sync that called it.
        /// </summary>
        public static BuilderDiagnostic[] CheckAndReport(string source, string context)
        {
            IReadOnlyList<BuilderDiagnostic> errors;
            try
            {
                errors = Check(source);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneBuilder] Compile check failed to run on {context}:\n{e}");
                return Array.Empty<BuilderDiagnostic>();
            }

            if (errors.Count == 0)
            {
                return Array.Empty<BuilderDiagnostic>();
            }

            Debug.LogError(Format(errors, context, source));
            return errors.ToArray();
        }
    }
}
