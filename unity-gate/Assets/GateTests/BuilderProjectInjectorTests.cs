using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using SceneBuilder.Editor;

// Gate for BuilderProjectInjector — the IDE-support half of the relocation.
//
// The relocation moved the builder to <ProjectRoot>/SceneBuilders/ so Unity would stop importing and
// domain-reloading for it. The cost was IDE support: Rider/VS no longer saw the file. This injector
// buys that back by putting the file in Unity's GENERATED csproj (an IDE-only artifact) without ever
// putting it in the asset database.
//
// What these tests CAN and CANNOT prove, stated plainly:
//   CAN  — the pure injection logic (what XML lands where, relative paths, idempotence, non-donors),
//          the CompilationPipeline-backed donor check against a REAL editor assembly graph, and that
//          Unity's own hook-discovery reflection finds and can invoke our hook.
//   CANNOT — that an IDE actually lights up. No IDE package (com.unity.ide.rider/visualstudio) is
//          installed in this gate project, so csproj generation never runs here and the hook is never
//          called by Unity itself. Confirming IntelliSense in Rider is a MANUAL step.
public class BuilderProjectInjectorTests
{
    // Shaped like Unity's real generated output: SDK-style, one ItemGroup of Compile items.
    private const string DonorCsproj =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<Project ToolsVersion=\"4.0\" DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
        "  <PropertyGroup>\n" +
        "    <AssemblyName>Assembly-CSharp</AssemblyName>\n" +
        "  </PropertyGroup>\n" +
        "  <ItemGroup>\n" +
        "    <Compile Include=\"Assets/Scripts/Player.cs\" />\n" +
        "  </ItemGroup>\n" +
        "</Project>\n";

    private const string ProjectDir = "/home/dev/MyGame";

    private static string BuilderAt(params string[] parts) =>
        Path.Combine(new[] { ProjectDir, "SceneBuilders" }.Concat(parts).ToArray());

    // The include Unity would write for a builder directly under SceneBuilders/, in Unity's own
    // convention: native separators (its generator normalizes to Path.DirectorySeparatorChar).
    private static string ExpectedInclude(string fileName) =>
        "SceneBuilders" + Path.DirectorySeparatorChar + fileName;

    private static string[] CompileIncludes(string csproj)
    {
        XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
        return XDocument.Parse(csproj)
            .Descendants(ns + "Compile")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => v != null)
            .ToArray();
    }

    [Test]
    public void Inject_AddsACompileItemForTheBuilder_ToTheDonorProject()
    {
        var result = BuilderProjectInjector.Inject(
            DonorCsproj, "Assembly-CSharp", ProjectDir, new[] { BuilderAt("DemoScene.cs") });

        Assert.Contains(
            ExpectedInclude("DemoScene.cs"),
            CompileIncludes(result),
            "The builder must appear as a <Compile Include> in the donor csproj — that is what gives the IDE IntelliSense.");

        // The pre-existing content must survive untouched.
        Assert.Contains("Assets/Scripts/Player.cs", CompileIncludes(result),
            "Injection must not disturb the items Unity generated.");
    }

    [Test]
    public void Inject_WritesThePathRelativeToTheProjectDirectory()
    {
        var result = BuilderProjectInjector.Inject(
            DonorCsproj, "Assembly-CSharp", ProjectDir, new[] { BuilderAt("DemoScene.cs") });

        var include = CompileIncludes(result).Single(i => i.EndsWith("DemoScene.cs", StringComparison.Ordinal));

        Assert.AreEqual(ExpectedInclude("DemoScene.cs"), include,
            "Unity generates csprojs INTO the project root and SceneBuilders/ is a child of it, so the " +
            "include must be a clean relative path — no absolute path, no '..' escape.");
        Assert.IsFalse(Path.IsPathRooted(include), "The include must be relative, not absolute.");

        // It must round-trip back to the real file the IDE has to open.
        Assert.AreEqual(
            Path.GetFullPath(BuilderAt("DemoScene.cs")),
            Path.GetFullPath(Path.Combine(ProjectDir, include)),
            "Resolving the include against the project dir must yield the actual builder file.");
    }

    [Test]
    public void Inject_IsIdempotent()
    {
        var once = BuilderProjectInjector.Inject(
            DonorCsproj, "Assembly-CSharp", ProjectDir, new[] { BuilderAt("DemoScene.cs") });
        var twice = BuilderProjectInjector.Inject(
            once, "Assembly-CSharp", ProjectDir, new[] { BuilderAt("DemoScene.cs") });

        Assert.AreEqual(once, twice, "Re-injecting into already-injected content must change nothing.");
        Assert.AreEqual(
            1,
            CompileIncludes(twice).Count(i => i.EndsWith("DemoScene.cs", StringComparison.Ordinal)),
            "The builder must never be double-included — a duplicate Compile item is an MSBuild error.");
    }

    [Test]
    public void Inject_LeavesNonDonorProjectsUnchanged()
    {
        foreach (var nonDonor in new[] { "GateTests", "SceneBuilder.Editor", "Assembly-CSharp-Editor" })
        {
            Assert.AreEqual(
                DonorCsproj,
                BuilderProjectInjector.Inject(DonorCsproj, nonDonor, ProjectDir, new[] { BuilderAt("DemoScene.cs") }),
                $"'{nonDonor}' is not the donor — its csproj must be returned byte-identical.");
        }
    }

    [Test]
    public void Inject_LeavesThePlayerVariantUnchanged()
    {
        // Unity generates Assembly-CSharp.Player.csproj when the PlayerAssemblies generation flag is
        // set. Editor-authoring source must not be injected into it.
        Assert.AreEqual(
            DonorCsproj,
            BuilderProjectInjector.Inject(
                DonorCsproj, "Assembly-CSharp.Player", ProjectDir, new[] { BuilderAt("DemoScene.cs") }),
            "The .Player variant must never receive the builder.");
    }

    [Test]
    public void Inject_LeavesContentUnchanged_WhenThereAreNoBuilders()
    {
        Assert.AreEqual(
            DonorCsproj,
            BuilderProjectInjector.Inject(DonorCsproj, "Assembly-CSharp", ProjectDir, Array.Empty<string>()),
            "With no builders there is nothing to inject and the csproj must be untouched.");
    }

    [Test]
    public void Inject_AddsEveryBuilder()
    {
        var result = BuilderProjectInjector.Inject(
            DonorCsproj,
            "Assembly-CSharp",
            ProjectDir,
            new[] { BuilderAt("DemoScene.cs"), BuilderAt("Level2.cs") });

        var includes = CompileIncludes(result);
        Assert.Contains(ExpectedInclude("DemoScene.cs"), includes);
        Assert.Contains(ExpectedInclude("Level2.cs"), includes);
    }

    [Test]
    public void Inject_ProducesWellFormedXml_WithTheItemInsideTheProjectElement()
    {
        var result = BuilderProjectInjector.Inject(
            DonorCsproj, "Assembly-CSharp", ProjectDir, new[] { BuilderAt("DemoScene.cs") });

        // Parse rather than string-match: a malformed csproj breaks the IDE for the WHOLE project, which
        // is a far worse failure than the missing IntelliSense we set out to fix.
        var doc = XDocument.Parse(result);
        XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";

        var compile = doc.Descendants(ns + "Compile")
            .Single(e => e.Attribute("Include")!.Value.EndsWith("DemoScene.cs", StringComparison.Ordinal));

        Assert.AreEqual(ns + "ItemGroup", compile.Parent!.Name,
            "A Compile item must live in an ItemGroup.");
        Assert.AreEqual(ns + "Project", compile.Parent.Parent!.Name,
            "That ItemGroup must be a direct child of <Project>.");
        Assert.IsTrue(result.TrimEnd().EndsWith("</Project>", StringComparison.Ordinal),
            "Nothing may be appended after </Project>.");
    }

    [Test]
    public void Inject_EscapesXmlSpecialCharactersInPaths()
    {
        var result = BuilderProjectInjector.Inject(
            DonorCsproj, "Assembly-CSharp", ProjectDir, new[] { BuilderAt("Rock & Roll.cs") });

        // Must still parse, and the raw '&' must have been escaped rather than written literally.
        Assert.Contains(ExpectedInclude("Rock &amp; Roll.cs").Replace("&amp;", "&"), CompileIncludes(result),
            "The decoded include must be the real file name.");
        Assert.IsTrue(result.Contains("&amp;"), "'&' must be XML-escaped in the emitted attribute.");
    }

    [Test]
    public void Inject_AddsLinkMetadata_OnlyWhenTheBuilderEscapesTheProjectDirectory()
    {
        // Normal case: SceneBuilders/ is a child of the project root, the include is already clean, so
        // <Link> would be redundant noise.
        var inside = BuilderProjectInjector.Inject(
            DonorCsproj, "Assembly-CSharp", ProjectDir, new[] { BuilderAt("DemoScene.cs") });
        Assert.IsFalse(inside.Contains("Link="),
            "No Link is needed for a path already under the csproj's own folder.");

        // Escape case: a '..' include displays as a confusing tree node without Link.
        var outside = BuilderProjectInjector.Inject(
            DonorCsproj, "Assembly-CSharp", ProjectDir, new[] { "/home/dev/Shared/SceneBuilders/DemoScene.cs" });

        XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
        var compile = XDocument.Parse(outside).Descendants(ns + "Compile")
            .Single(e => e.Attribute("Include")!.Value.EndsWith("DemoScene.cs", StringComparison.Ordinal));

        Assert.IsTrue(compile.Attribute("Include")!.Value.StartsWith("..", StringComparison.Ordinal),
            "Precondition: this builder is outside the project dir.");
        Assert.AreEqual(ExpectedInclude("DemoScene.cs"), compile.Attribute("Link")?.Value,
            "Link must make it display under SceneBuilders/ instead of as a '..' path.");
    }

    // ---- The Unity boundary: real assembly graph, real hook discovery ----

    [Test]
    public void ReferencesAuthoring_ReadsTheRealEditorAssemblyGraph()
    {
        // Ground truth from this project's asmdefs, via CompilationPipeline — never by parsing a csproj.
        // GateTests.asmdef lists SceneBuilder.Authoring in "references"; GateFixtures.asmdef has none.
        Assert.IsTrue(BuilderProjectInjector.ReferencesAuthoring("GateTests"),
            "GateTests references SceneBuilder.Authoring in its asmdef.");
        Assert.IsFalse(BuilderProjectInjector.ReferencesAuthoring("GateFixtures"),
            "GateFixtures has no references and must not report Authoring.");
        Assert.IsFalse(BuilderProjectInjector.ReferencesAuthoring("NoSuchAssembly"),
            "An assembly that does not exist cannot reference Authoring.");
    }

    [Test]
    public void DonorIsTheAssemblyUnityCompilesLooseAssetsScriptsInto()
    {
        Assert.AreEqual("Assembly-CSharp", BuilderProjectInjector.DonorProjectName,
            "The builder lived at Assets/SceneBuilder/DemoScene.cs (a loose script, no asmdef) before the " +
            "relocation, i.e. in Assembly-CSharp. That is the compile context we restore for the IDE.");
    }

    [Serializable]
    private class AsmdefProbe
    {
#pragma warning disable CS0649 // assigned by JsonUtility
        public bool autoReferenced;
#pragma warning restore CS0649
    }

    [Test]
    public void AuthoringAsmdefIsAutoReferenced_SoTheDonorCanSeeTheBuildersTypes()
    {
        // The load-bearing claim behind choosing Assembly-CSharp: Unity auto-references autoReferenced
        // asmdefs from the predefined assemblies. Assert it against the SHIPPED asmdef, located through
        // the compilation pipeline — this gate project has no loose scripts and therefore no
        // Assembly-CSharp to inspect directly.
        var asmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName("SceneBuilder.Authoring");
        Assert.IsNotNull(asmdefPath, "SceneBuilder.Authoring must exist in the editor compilation graph.");

        var asmdef = AssetDatabase.LoadAssetAtPath<TextAsset>(asmdefPath);
        Assert.IsNotNull(asmdef, $"Expected to load the Authoring asmdef at {asmdefPath}");

        Assert.IsTrue(JsonUtility.FromJson<AsmdefProbe>(asmdef.text).autoReferenced,
            "If Authoring stops being autoReferenced, Assembly-CSharp can no longer see ISceneDefinition " +
            "and the donor choice is invalid.");
    }

    [Test]
    public void UnityHookDiscovery_FindsAndInvokesOurHook()
    {
        // Mirror EXACTLY what com.unity.ide.rider does: collect AssetPostprocessor-derived types via
        // TypeCache, then reflect for a static OnGeneratedCSProject(string, string) returning string.
        // No IDE package is installed here, so this is the closest the gate can get to the real call.
        var type = TypeCache.GetTypesDerivedFrom<AssetPostprocessor>()
            .SingleOrDefault(t => t == typeof(BuilderProjectInjector));
        Assert.IsNotNull(type,
            "Unity only calls OnGeneratedCSProject on AssetPostprocessor subclasses it finds via TypeCache.");

        var method = type.GetMethod(
            "OnGeneratedCSProject",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "The hook must be a STATIC method named OnGeneratedCSProject.");
        Assert.AreEqual(typeof(string), method.ReturnType, "Rider only uses the return value if it is a string.");
        CollectionAssert.AreEqual(
            new[] { typeof(string), typeof(string) },
            method.GetParameters().Select(p => p.ParameterType).ToArray(),
            "The hook is invoked with exactly (path, content).");

        // Drive it end-to-end with a real builder on disk, exactly as Unity would.
        var buildersDir = SceneBuilderPaths.BuildersDirectory;
        var existedBefore = Directory.Exists(buildersDir);
        var builder = SceneBuilderPaths.Builder("HookProbeScene");
        try
        {
            SceneBuilderPaths.EnsureBuildersDirectory();
            File.WriteAllText(builder, "public class HookProbeScene { }\n");

            var donorPath = Path.Combine(SceneBuilderPaths.ProjectRoot, "Assembly-CSharp.csproj");
            var result = (string)method.Invoke(null, new object[] { donorPath, DonorCsproj });

            Assert.Contains(ExpectedInclude("HookProbeScene.cs"), CompileIncludes(result),
                "Invoked as Unity invokes it, the hook must inject the builder found on disk.");

            // And a non-donor csproj must come back untouched through the same entry point.
            var otherPath = Path.Combine(SceneBuilderPaths.ProjectRoot, "GateTests.csproj");
            Assert.AreEqual(DonorCsproj, (string)method.Invoke(null, new object[] { otherPath, DonorCsproj }),
                "The hook must derive the project name from the csproj PATH and skip non-donors.");
        }
        finally
        {
            if (File.Exists(builder))
            {
                File.Delete(builder);
            }

            if (!existedBefore && Directory.Exists(buildersDir))
            {
                Directory.Delete(buildersDir, true);
            }
        }
    }

    [Test]
    public void BuilderFiles_ReturnsBuilderSourcesFromTheBuildersDirectory()
    {
        var buildersDir = SceneBuilderPaths.BuildersDirectory;
        var existedBefore = Directory.Exists(buildersDir);
        var builder = SceneBuilderPaths.Builder("ProbeScene");
        var sidecar = SceneBuilderPaths.Sidecar("ProbeScene");
        try
        {
            SceneBuilderPaths.EnsureBuildersDirectory();
            File.WriteAllText(builder, "public class ProbeScene { }\n");
            File.WriteAllText(sidecar, "{}\n");

            var files = BuilderProjectInjector.BuilderFiles();

            Assert.Contains(Path.GetFullPath(builder), files.Select(Path.GetFullPath).ToArray(),
                "The builder .cs must be discovered.");
            Assert.IsFalse(files.Any(f => f.EndsWith(".sbmap.json", StringComparison.Ordinal)),
                "The identity sidecar is not source and must never be injected as a Compile item.");
        }
        finally
        {
            if (File.Exists(builder))
            {
                File.Delete(builder);
            }

            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }

            if (!existedBefore && Directory.Exists(buildersDir))
            {
                Directory.Delete(buildersDir, true);
            }
        }
    }
}
