using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// b6-t3 (specs/28-typed-asset-catalogs.md): AssetCatalogLoader's own load/cache/null-fallback
// contract, plus a thin live-Build wiring proof that SceneBuilderBuild.Run actually threads the
// loaded catalog into BuilderParser.Parse (not just that the loader itself works). The exhaustive
// resolves-to-the-right-GUID matrix is b7-t1's job; this only proves the wire is live.
public class AssetCatalogLoaderTests
{
    private const string Dir = "Assets/GateTests/Fixtures_AssetCatalogLoader";
    private const string ScenePath = "Assets/GateTests/__AssetCatalogLoaderTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    [SetUp]
    public void SetUp()
    {
        _manifestPath = SceneBuilderPaths.AssetManifestPath;
        _manifestExistedBefore = File.Exists(_manifestPath);
        _manifestBytesBefore = _manifestExistedBefore ? File.ReadAllBytes(_manifestPath) : null;

        _dir = Path.Combine(Path.GetTempPath(), "sb_acl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "AssetCatalogLoaderWiringGateScene.cs");
        _sidecarPath = Path.Combine(_dir, "AssetCatalogLoaderWiringGateScene.sbmap.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (AssetDatabase.IsValidFolder(Dir))
        {
            AssetDatabase.DeleteAsset(Dir);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }

        if (_manifestExistedBefore)
        {
            File.WriteAllBytes(_manifestPath, _manifestBytesBefore);
        }
        else if (File.Exists(_manifestPath))
        {
            File.Delete(_manifestPath);
        }
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
        {
            return;
        }

        var parent = path.Substring(0, path.LastIndexOf('/'));
        var name = path.Substring(path.LastIndexOf('/') + 1);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }

    [Test]
    public void Load_PresentManifest_RoundTripsCatalog()
    {
        EnsureFolder(Dir + "/Env");
        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), Dir + "/Env/Red.mat");
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        AssetCatalogGenerator.Regenerate();
        var onDisk = AssetCatalog.Deserialize(File.ReadAllText(_manifestPath));

        var loaded = AssetCatalogLoader.Load();

        Assert.IsNotNull(loaded, "AssetCatalogLoader.Load() must return the on-disk manifest, not null.");
        Assert.AreEqual(onDisk.Entries.Length, loaded.Entries.Length,
            "Load() must round-trip every entry from the on-disk manifest.");
        Assert.IsTrue(loaded.Entries.Any(e => e.Group == "Materials" && e.MemberName == "Red"
                && e.FolderSegments.SequenceEqual(new[] { "GateTests", "Fixtures_AssetCatalogLoader", "Env" })),
            "Load() must round-trip the fixture Materials/Env/Red entry.");
    }

    [Test]
    public void Load_AbsentManifest_ReturnsNull()
    {
        if (File.Exists(_manifestPath))
        {
            File.Delete(_manifestPath);
        }

        var loaded = AssetCatalogLoader.Load();

        Assert.IsNull(loaded, "Load() must return null when the manifest file does not exist.");
    }

    [Test]
    public void Load_UnparseableManifest_ReturnsNull()
    {
        SceneBuilderPaths.EnsureGeneratedDirectory();
        File.WriteAllText(_manifestPath, "{ not valid json ][");

        AssetCatalog loaded = null;
        Assert.DoesNotThrow(() => loaded = AssetCatalogLoader.Load(),
            "Load() must swallow a deserialize failure, not throw.");
        Assert.IsNull(loaded, "Load() must return null for an unparseable manifest.");
    }

    // Proves SceneBuilderBuild.Run actually threads AssetCatalogLoader.Load()'s result down into
    // BuilderParser.Parse — the wiring b6-t3 adds, not just that the loader itself works (which the
    // three tests above already cover in isolation).
    [Test]
    public void TypedMemberBuilds_ProvingCatalogReachesParser()
    {
        EnsureFolder(Dir + "/Env");
        var redPath = Dir + "/Env/Red.mat";
        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), redPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        AssetCatalogGenerator.Regenerate();
        var catalog = AssetCatalog.Deserialize(File.ReadAllText(_manifestPath));
        var entry = catalog.Entries.FirstOrDefault(e => e.Group == "Materials" && e.MemberName == "Red"
            && e.FolderSegments.SequenceEqual(new[] { "GateTests", "Fixtures_AssetCatalogLoader", "Env" }));
        Assert.IsNotNull(entry, "Setup: no Materials/Env/Red entry in the regenerated manifest.");

        var chain = "Assets." + entry.Group + "." + string.Join(".", entry.FolderSegments) + "." + entry.MemberName;

        File.WriteAllText(_builderPath, $@"
using SceneBuilder.Authoring;
public class AssetCatalogLoaderWiringGateScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        var surface = scene.Add(""Surface"");
        surface.Component<UnityEngine.MeshRenderer>(c => c.Set(""m_Materials"", new[] {{ {chain} }}));
    }}
}}");

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        Assert.IsEmpty(buildResult.Diagnostics,
            "Live SceneBuilderBuild.Run reported diagnostics for a typed asset member ('" + chain + "'): "
            + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var surfaceGo = EditorSceneManager.GetActiveScene().GetRootGameObjects()
            .FirstOrDefault(go => go.name == "Surface");
        Assert.IsNotNull(surfaceGo, "Surface was not created by SceneBuilderBuild.Run");
        var mr = surfaceGo.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "Authored MeshRenderer was not materialized on Surface");
        Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Material>(redPath), mr.sharedMaterial,
            "The typed asset member did not resolve to the expected Red.mat — the catalog did not "
            + "reach BuilderParser.Parse through SceneBuilderBuild.Run.");
    }
}
