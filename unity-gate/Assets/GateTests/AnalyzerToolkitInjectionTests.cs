using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Editor;

// Gate for the analyzer toolkit's Unity-boundary confirmations (b5-t1/t2/t3): the project-catalog
// generator's real File IO against a live TagManager, the injector's real <Analyzer>/<AdditionalFiles>
// wiring, and that the shipped Analyzers~/ dlls are never imported into the compilation graph.
public class AnalyzerToolkitInjectionTests
{
    private const string PlayerTag = "Player";
    private const int GroundLayerIndex = 6;
    private const string GroundLayerName = "Ground";

    private static SerializedObject OpenTagManager() =>
        new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);

    [Test]
    public void Regenerate_WithNamedTagAndLayer_WritesManifestContainingBoth()
    {
        var so = OpenTagManager();
        var tagsProp = so.FindProperty("tags");
        var tagExistedBefore = Enumerable.Range(0, tagsProp.arraySize)
            .Any(i => tagsProp.GetArrayElementAtIndex(i).stringValue == PlayerTag);
        var layerProp = so.FindProperty("layers").GetArrayElementAtIndex(GroundLayerIndex);
        var originalLayerName = layerProp.stringValue;

        var manifestPath = SceneBuilderPaths.ProjectCatalogManifestPath;
        var manifestExistedBefore = File.Exists(manifestPath);
        var manifestBytesBefore = manifestExistedBefore ? File.ReadAllBytes(manifestPath) : null;

        try
        {
            if (!tagExistedBefore)
            {
                tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
                tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = PlayerTag;
            }

            layerProp.stringValue = GroundLayerName;
            so.ApplyModifiedPropertiesWithoutUndo();

            var changed = ProjectCatalogGenerator.Regenerate();

            Assert.IsTrue(File.Exists(manifestPath), "Regenerate must write the manifest file.");

            var manifest = CanonicalJson.Deserialize<ProjectCatalogManifest>(File.ReadAllText(manifestPath));
            Assert.Contains(PlayerTag, manifest.Tags, "The live TagManager's named tag must be captured.");
            Assert.IsTrue(
                manifest.Layers.Any(l => l.Index == GroundLayerIndex && l.Name == GroundLayerName),
                "The live TagManager's named layer must be captured with its real slot index.");
        }
        finally
        {
            var so2 = OpenTagManager();
            if (!tagExistedBefore)
            {
                var tags2 = so2.FindProperty("tags");
                for (var i = tags2.arraySize - 1; i >= 0; i--)
                {
                    if (tags2.GetArrayElementAtIndex(i).stringValue == PlayerTag)
                    {
                        tags2.DeleteArrayElementAtIndex(i);
                    }
                }
            }

            so2.FindProperty("layers").GetArrayElementAtIndex(GroundLayerIndex).stringValue = originalLayerName;
            so2.ApplyModifiedPropertiesWithoutUndo();

            if (manifestExistedBefore)
            {
                File.WriteAllBytes(manifestPath, manifestBytesBefore);
            }
            else if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }
        }
    }

    [Test]
    public void Regenerate_CalledAgainWithNoChange_RewritesNothing()
    {
        var manifestPath = SceneBuilderPaths.ProjectCatalogManifestPath;
        var manifestExistedBefore = File.Exists(manifestPath);
        var manifestBytesBefore = manifestExistedBefore ? File.ReadAllBytes(manifestPath) : null;

        try
        {
            ProjectCatalogGenerator.Regenerate();
            var bytesAfterFirstRun = File.ReadAllBytes(manifestPath);

            var changed = ProjectCatalogGenerator.Regenerate();

            Assert.IsFalse(changed, "A second Regenerate with no TagManager change must report no write.");
            Assert.AreEqual(bytesAfterFirstRun, File.ReadAllBytes(manifestPath),
                "The manifest bytes must be byte-identical across a no-op regen.");
        }
        finally
        {
            if (manifestExistedBefore)
            {
                File.WriteAllBytes(manifestPath, manifestBytesBefore);
            }
            else if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }
        }
    }

    // Shaped like Unity's real generated output, matching BuilderProjectInjectorTests' fixture.
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

    private static string BuilderAt(string fileName) => Path.Combine(ProjectDir, "SceneBuilders", fileName);

    private static XNamespace Msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";

    private static string[] AnalyzerIncludes(string csproj) =>
        XDocument.Parse(csproj).Descendants(Msbuild + "Analyzer")
            .Select(e => e.Attribute("Include")?.Value).Where(v => v != null).ToArray();

    private static string[] AdditionalFilesIncludes(string csproj) =>
        XDocument.Parse(csproj).Descendants(Msbuild + "AdditionalFiles")
            .Select(e => e.Attribute("Include")?.Value).Where(v => v != null).ToArray();

    [Test]
    public void Inject_WithAnalyzersDirectory_AddsAnalyzerAndAdditionalFilesItems()
    {
        var analyzersDir = SceneBuilderPaths.AnalyzersDirectory;
        Assert.IsNotNull(analyzersDir, "The package must resolve in the gate project.");

        var result = BuilderProjectInjector.Inject(
            DonorCsproj, "Assembly-CSharp", ProjectDir, new[] { BuilderAt("DemoScene.cs") }, analyzersDir);

        var analyzers = AnalyzerIncludes(result);
        Assert.IsTrue(
            analyzers.Any(a => a.EndsWith(SceneBuilderPaths.AnalyzerAssemblyFileName, StringComparison.Ordinal)),
            "The diagnostics analyzer dll must be injected as an <Analyzer> item.");
        Assert.IsTrue(
            analyzers.Any(a => a.EndsWith(SceneBuilderPaths.GrammarAssemblyFileName, StringComparison.Ordinal)),
            "The grammar dependency dll must be injected as an <Analyzer> item.");

        var additionalFiles = AdditionalFilesIncludes(result);
        Assert.IsTrue(
            additionalFiles.Any(a => a.EndsWith(SceneBuilderPaths.ProjectCatalogFileName, StringComparison.Ordinal)),
            "The project catalog manifest must be injected as an <AdditionalFiles> item.");
    }

    [Test]
    public void Inject_WithAnalyzersDirectory_IsIdempotentAndLeavesNonDonorUnchanged()
    {
        var analyzersDir = SceneBuilderPaths.AnalyzersDirectory;

        var once = BuilderProjectInjector.Inject(
            DonorCsproj, "Assembly-CSharp", ProjectDir, new[] { BuilderAt("DemoScene.cs") }, analyzersDir);
        var twice = BuilderProjectInjector.Inject(
            once, "Assembly-CSharp", ProjectDir, new[] { BuilderAt("DemoScene.cs") }, analyzersDir);

        Assert.IsTrue(ReferenceEquals(once, twice),
            "Re-injecting must return the SAME instance when every item is already present.");
        Assert.AreEqual(2, AnalyzerIncludes(twice).Length, "Must never double-inject the two analyzer dlls.");
        Assert.AreEqual(2, AdditionalFilesIncludes(twice).Length,
            "Must never double-inject the catalog file or the facade manifest file (two AdditionalFiles items total).");

        Assert.AreEqual(
            DonorCsproj,
            BuilderProjectInjector.Inject(
                DonorCsproj, "GateTests", ProjectDir, new[] { BuilderAt("DemoScene.cs") }, analyzersDir),
            "A non-donor project must come back byte-identical even with an analyzers directory supplied.");
    }

    [Test]
    public void AnalyzersFolder_IsNotImportedAsAManagedPlugin()
    {
        bool NameMatches(string name) =>
            string.Equals(name, "CodeScenes.Analyzers", StringComparison.Ordinal)
            || string.Equals(name, "SceneBuilder.Grammar", StringComparison.Ordinal);

        Assert.IsFalse(
            CompilationPipeline.GetAssemblies(AssembliesType.Editor).Any(a => NameMatches(a.name)),
            "The Analyzers~ dlls must never appear in the editor compilation graph.");
        Assert.IsFalse(
            CompilationPipeline.GetAssemblies(AssembliesType.Player).Any(a => NameMatches(a.name)),
            "The Analyzers~ dlls must never appear in the player compilation graph.");

        var analyzersDir = SceneBuilderPaths.AnalyzersDirectory;
        Assert.IsNotNull(analyzersDir, "The package must resolve in the gate project.");

        var analyzerDll = Path.Combine(analyzersDir, SceneBuilderPaths.AnalyzerAssemblyFileName);
        var grammarDll = Path.Combine(analyzersDir, SceneBuilderPaths.GrammarAssemblyFileName);
        Assert.IsTrue(File.Exists(analyzerDll), "The analyzer dll must be staged on disk.");
        Assert.IsTrue(File.Exists(grammarDll), "The grammar dll must be staged on disk.");

        Assert.IsFalse(File.Exists(analyzerDll + ".meta"), "No .meta must exist for the analyzer dll — it must never be imported.");
        Assert.IsFalse(File.Exists(grammarDll + ".meta"), "No .meta must exist for the grammar dll — it must never be imported.");
        Assert.IsFalse(File.Exists(analyzersDir + ".meta"), "No .meta must exist for the Analyzers~ folder itself.");
    }
}
