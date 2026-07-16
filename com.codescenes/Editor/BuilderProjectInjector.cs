#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using Debug = UnityEngine.Debug;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Gives the relocated builder (<c>&lt;ProjectRoot&gt;/SceneBuilders/*.cs</c>) full IDE support —
    /// IntelliSense, type-checking, go-to-definition — WITHOUT putting it back into Unity's asset
    /// pipeline, by injecting it into Unity's own generated <c>Assembly-CSharp.csproj</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this is safe: Unity's <c>.csproj</c>/<c>.sln</c> are IDE-only artifacts. Unity compiles from
    /// its internal asset-database assembly graph and never invokes MSBuild on them — the IDE packages
    /// (<c>com.unity.ide.rider</c>, <c>com.unity.ide.visualstudio</c>) only READ
    /// <c>CompilationPipeline.GetAssemblies()</c> and WRITE csproj text; nothing parses a csproj back
    /// into compilation. So a file can be IN the csproj but NOT in the asset database: the IDE sees it,
    /// Unity does not import it, does not compile it, and does not domain-reload for it. That is exactly
    /// what <see cref="SceneBuilderPaths"/> bought us and what this class must not give back.
    /// </para>
    /// <para>
    /// Unity regenerates csprojs from scratch and clobbers any edit, so this hook re-injects on every
    /// generation. That is the intended design, not a workaround — the csproj is disposable output.
    /// </para>
    /// <para>
    /// Watch item (NOT designed against): Unity has stated an intent to eventually compile FROM csproj,
    /// which would make injected files compiled again (reinstating the domain reload). The same
    /// statement says those csprojs would be read-only, so injection would be refused rather than
    /// silently compiled. Announced 2022, blocked 2024, shipped nowhere. If it ever ships, this class is
    /// where it surfaces.
    /// </para>
    /// </remarks>
    public class BuilderProjectInjector : AssetPostprocessor
    {
        /// <summary>
        /// The csproj we inject into: Unity's predefined assembly for loose scripts under
        /// <c>Assets/</c>.
        /// </summary>
        /// <remarks>
        /// This is the donor because it is precisely the assembly the builder USED to be compiled into:
        /// before the relocation it lived at <c>Assets/SceneBuilder/DemoScene.cs</c>, a loose script with
        /// no asmdef in its folder, i.e. Assembly-CSharp. Injecting here restores that exact compile
        /// context for the IDE and nothing more. It has what the builder needs in scope:
        /// <list type="bullet">
        /// <item><c>SceneBuilder.Authoring</c> (<c>ISceneDefinition</c>, <c>SceneRoot</c>,
        /// <c>AssetRefs</c>) — its asmdef is <c>autoReferenced: true</c>, and Unity auto-references such
        /// asmdefs from the predefined assemblies. Verified at runtime, not assumed:
        /// see <see cref="ReferencesAuthoring"/>.</item>
        /// <item><c>UnityEngine</c> — always referenced; the builder names component types fully
        /// qualified (e.g. <c>UnityEngine.Rigidbody</c>).</item>
        /// </list>
        /// Exact-matching this name also excludes the <c>.Player</c> variant
        /// (<c>Assembly-CSharp.Player.csproj</c>), which Unity generates when the PlayerAssemblies
        /// project-generation flag is set and which must not receive editor-authoring source.
        /// </remarks>
        public const string DonorProjectName = "Assembly-CSharp";

        private const string AuthoringAssemblyName = "SceneBuilder.Authoring";

        // One warning per domain: Unity regenerates csprojs constantly, and a per-generation log would
        // be console spam rather than a diagnostic.
        private static bool _warnedAboutDonor;

        /// <summary>
        /// Unity's hook, invoked by the IDE packages (reflectively, hence non-public is fine) once per
        /// generated csproj. Returns the content to write.
        /// </summary>
        /// <remarks>
        /// Only the Rider and Visual Studio packages call this. The VSCode package does not, so IDE
        /// recovery via this hook is NOT universal.
        /// </remarks>
        private static string OnGeneratedCSProject(string path, string content)
        {
            try
            {
                var injected = Inject(
                    content,
                    Path.GetFileNameWithoutExtension(path),
                    SceneBuilderPaths.ProjectRoot,
                    BuilderFiles());

                // Reference equality: Inject returns the SAME instance when it changed nothing, so this
                // warns only when source was actually added to a project that cannot resolve its types.
                if (!ReferenceEquals(injected, content) && !_warnedAboutDonor && !ReferencesAuthoring(DonorProjectName))
                {
                    _warnedAboutDonor = true;
                    Debug.LogWarning(
                        $"[SceneBuilder] Injected the builder into {DonorProjectName}.csproj, but that assembly does " +
                        $"not reference {AuthoringAssemblyName} — the IDE will show unresolved-symbol errors in the " +
                        $"builder. Check that {AuthoringAssemblyName}.asmdef still has \"autoReferenced\": true.");
                }

                return injected;
            }
            catch (Exception e)
            {
                // A broken injector must never break the user's project generation: worst case they get
                // the un-injected csproj they would have had anyway.
                Debug.LogError($"[SceneBuilder] Failed to inject the builder into '{path}':\n{e}");
                return content;
            }
        }

        /// <summary>
        /// Every builder source under <c>&lt;ProjectRoot&gt;/SceneBuilders/</c>, in a stable order.
        /// Empty when the folder does not exist yet.
        /// </summary>
        public static IReadOnlyList<string> BuilderFiles()
        {
            var directory = SceneBuilderPaths.BuildersDirectory;
            if (!Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            var files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }

        /// <summary>
        /// True when <paramref name="assemblyName"/> exists in the editor compilation graph AND
        /// references <c>SceneBuilder.Authoring</c>.
        /// </summary>
        /// <remarks>
        /// Reads <see cref="CompilationPipeline"/> — the same source the IDE packages derive csprojs
        /// from — and never parses a csproj. A csproj is disposable output, never a source of truth
        /// about the assembly graph.
        /// </remarks>
        public static bool ReferencesAuthoring(string assemblyName)
        {
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
            {
                if (string.Equals(assembly.name, assemblyName, StringComparison.Ordinal))
                {
                    return assembly.assemblyReferences.Any(
                        r => string.Equals(r.name, AuthoringAssemblyName, StringComparison.Ordinal));
                }
            }

            return false;
        }

        /// <summary>
        /// The pure, testable core: returns <paramref name="csprojContent"/> with a
        /// <c>&lt;Compile /&gt;</c> item for each of <paramref name="builderFiles"/>, or the SAME
        /// instance when nothing should change — i.e. when this is not the donor project, when there are
        /// no builders, or when every builder is already present (idempotence).
        /// </summary>
        /// <param name="csprojContent">The generated csproj text.</param>
        /// <param name="csprojProjectName">
        /// The csproj's file name without extension. Unity writes each project to
        /// <c>&lt;ProjectDirectory&gt;/&lt;name&gt;.csproj</c>, so this IS the project name — derived from
        /// the path, never by parsing the XML.
        /// </param>
        /// <param name="projectDirectory">The folder the csproj lives in (the Unity project root).</param>
        /// <param name="builderFiles">Absolute paths of the builder sources to inject.</param>
        public static string Inject(
            string csprojContent,
            string csprojProjectName,
            string projectDirectory,
            IReadOnlyList<string> builderFiles)
        {
            if (!string.Equals(csprojProjectName, DonorProjectName, StringComparison.Ordinal))
            {
                return csprojContent;
            }

            if (builderFiles == null || builderFiles.Count == 0 || string.IsNullOrEmpty(csprojContent))
            {
                return csprojContent;
            }

            var closingTag = csprojContent.LastIndexOf("</Project>", StringComparison.Ordinal);
            if (closingTag < 0)
            {
                return csprojContent;
            }

            var items = new StringBuilder();
            foreach (var builderFile in builderFiles)
            {
                var include = SecurityElement.Escape(RelativePath(projectDirectory, builderFile));

                // Idempotence: Unity regenerates from scratch so this normally never trips, but a hook
                // must never double-inject if it sees its own output (e.g. another postprocessor, or a
                // future generator that preserves content).
                if (csprojContent.IndexOf($"Include=\"{include}\"", StringComparison.Ordinal) >= 0)
                {
                    continue;
                }

                items.Append("    <Compile Include=\"").Append(include).Append('"');

                // <Link> controls where the IDE shows the file in the solution tree. It is only needed
                // when the file sits OUTSIDE the csproj's folder, where MSBuild would otherwise display
                // a "..\..\" path. It normally does NOT: Unity generates csprojs into the project root
                // and the builders folder is a child of it, so the include is already a clean
                // "SceneBuilders/Foo.cs" that displays correctly on its own.
                if (include.StartsWith("..", StringComparison.Ordinal))
                {
                    var link = SecurityElement.Escape(
                        SceneBuilderPaths.BuildersFolderName + Path.DirectorySeparatorChar + Path.GetFileName(builderFile));
                    items.Append(" Link=\"").Append(link).Append('"');
                }

                items.AppendLine(" />");
            }

            if (items.Length == 0)
            {
                return csprojContent;
            }

            // A fresh <ItemGroup> appended just before </Project>. MSBuild allows any number of them, so
            // this needs no knowledge of the generated file's structure beyond where it ends — which
            // keeps us out of the business of parsing Unity's output.
            var itemGroup = new StringBuilder()
                .AppendLine("  <ItemGroup>")
                .Append(items)
                .AppendLine("  </ItemGroup>")
                .ToString();

            return csprojContent.Insert(closingTag, itemGroup);
        }

        /// <summary>
        /// <paramref name="file"/> relative to <paramref name="directory"/>, using the platform's
        /// separator — matching how Unity's own generator normalizes the paths it writes.
        /// </summary>
        private static string RelativePath(string directory, string file)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(file));
            return relative.Replace(
                Path.DirectorySeparatorChar == '\\' ? '/' : '\\',
                Path.DirectorySeparatorChar);
        }
    }
}
