using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;
using SceneBuilder.GateFixtures;

// b6-t1 (specs/28-typed-asset-catalogs.md): the AssetCatalogGenerator producer's Unity boundary —
// real AssetDatabase-by-type scanning + sub-asset FileId derivation, driven against fixtures
// created at runtime under Fixtures_AssetCatalog. Covers checklist #1: type-first + folder-path
// manifest structure (two same-leaf materials in different folders => no numeric suffix), the
// sub-mesh SubAsset+non-zero-FileId identity, and byte-stable idempotent regen (the observable
// stand-in for "no domain reload" — the manifest write is plain File IO outside Assets/).
public class AssetCatalogTests
{
    private const string Dir = "Assets/GateTests/Fixtures_AssetCatalog";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    [SetUp]
    public void SetUp()
    {
        _manifestPath = SceneBuilderPaths.AssetManifestPath;
        _manifestExistedBefore = File.Exists(_manifestPath);
        _manifestBytesBefore = _manifestExistedBefore ? File.ReadAllBytes(_manifestPath) : null;

        CreateFixtures();
    }

    [TearDown]
    public void TearDown()
    {
        DeleteFixtures();

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

    // Builds the fixture set the PUBLIC_SURFACE in research.md keys on:
    //   - two "Stone" materials in different folders (cross-folder same-leaf => no suffix)
    //   - a "Stone" sprite in the same folder as one of the materials (type groups are
    //     independent namespaces)
    //   - a genuine sub-mesh ("Barrel", non-zero FileId) inside a container whose MAIN object is a
    //     non-Mesh (a Material), so the container itself never yields a competing Meshes entry
    //   - a scene asset and a ScriptableObject asset
    private static void CreateFixtures()
    {
        EnsureFolder(Dir + "/Environment/Rocks");
        EnsureFolder(Dir + "/Characters/Skin");
        EnsureFolder(Dir + "/Models/Props");
        EnsureFolder(Dir + "/Scenes");
        EnsureFolder(Dir + "/Config");

        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), Dir + "/Environment/Rocks/Stone.mat");
        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), Dir + "/Characters/Skin/Stone.mat");

        var spriteAssetPath = Dir + "/Environment/Rocks/Stone.png";
        var spriteFullPath = Path.Combine(Application.dataPath, spriteAssetPath.Substring("Assets/".Length));
        var tex = new Texture2D(4, 4);
        File.WriteAllBytes(spriteFullPath, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(spriteAssetPath);
        var importer = (TextureImporter)AssetImporter.GetAtPath(spriteAssetPath);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.SaveAndReimport();

        var barrelContainerPath = Dir + "/Models/Props/Barrel.asset";
        var containerMain = new Material(Shader.Find("Standard")) { name = "BarrelContainer" };
        AssetDatabase.CreateAsset(containerMain, barrelContainerPath);
        var barrelSub = new Mesh { name = "Barrel" };
        AssetDatabase.AddObjectToAsset(barrelSub, containerMain);
        AssetDatabase.ImportAsset(barrelContainerPath);

        var scenePath = Dir + "/Scenes/MainMenu.unity";
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, scenePath);

        // A bare ScriptableObject has no backing MonoScript and is never returned by
        // AssetDatabase.FindAssets("t:ScriptableObject") — use a real compiled subclass
        // (AssetCatalogScriptableObjectFixture) so the scan can index it.
        var so = ScriptableObject.CreateInstance<AssetCatalogScriptableObjectFixture>();
        AssetDatabase.CreateAsset(so, Dir + "/Config/EnemyConfig.asset");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static void DeleteFixtures()
    {
        if (AssetDatabase.IsValidFolder(Dir))
        {
            AssetDatabase.DeleteAsset(Dir);
        }
    }

    private static AssetCatalog ReadManifest() =>
        AssetCatalog.Deserialize(File.ReadAllText(SceneBuilderPaths.AssetManifestPath));

    [Test]
    public void Regenerate_WritesTypeFirstFolderPathManifest_WithAllFixtureGroups()
    {
        // AssetCatalogGenerator is auto-triggered (OnPostprocessAllAssets), so CreateFixtures()'s own
        // imports may already have converged the manifest before this explicit call runs — do not
        // assert its return value, only that the manifest exists and holds the expected content.
        AssetCatalogGenerator.Regenerate();
        Assert.IsTrue(File.Exists(SceneBuilderPaths.AssetManifestPath), "Regenerate must write the asset manifest.");

        var catalog = ReadManifest();

        // Raw FolderSegments are the container path's directory with only the leading "Assets"
        // segment dropped (research.md SCAN) — so every entry carries the full fixture-root prefix
        // ("GateTests", "Fixtures_AssetCatalog"), not just the leaf folders under it.
        var rocksStone = catalog.Entries.FirstOrDefault(e =>
            e.Group == "Materials" && e.MemberName == "Stone" && SequenceEqual(e.FolderSegments, "GateTests", "Fixtures_AssetCatalog", "Environment", "Rocks"));
        Assert.IsNotNull(rocksStone, "No Materials entry for Environment/Rocks/Stone.mat.");

        var skinStone = catalog.Entries.FirstOrDefault(e =>
            e.Group == "Materials" && e.MemberName == "Stone" && SequenceEqual(e.FolderSegments, "GateTests", "Fixtures_AssetCatalog", "Characters", "Skin"));
        Assert.IsNotNull(skinStone, "No Materials entry for Characters/Skin/Stone.mat.");
        Assert.AreNotEqual(rocksStone.Guid, skinStone.Guid, "The two Stone materials must be distinct assets.");

        Assert.IsFalse(catalog.Entries.Any(e => e.Group == "Materials" && e.MemberName == "Stone2"),
            "Cross-folder same-leaf materials must NOT be suffixed — the folder path disambiguates them.");

        var spriteStone = catalog.Entries.FirstOrDefault(e =>
            e.Group == "Sprites" && e.MemberName == "Stone" && SequenceEqual(e.FolderSegments, "GateTests", "Fixtures_AssetCatalog", "Environment", "Rocks"));
        Assert.IsNotNull(spriteStone, "No Sprites entry for Environment/Rocks/Stone.png.");

        var barrelSub = catalog.Entries.FirstOrDefault(e =>
            e.Group == "Meshes" && e.SubAsset == "Barrel" && SequenceEqual(e.FolderSegments, "GateTests", "Fixtures_AssetCatalog", "Models", "Props"));
        Assert.IsNotNull(barrelSub, "No Meshes entry for the Barrel sub-mesh.");
        Assert.AreNotEqual(0L, barrelSub.FileId, "A sub-mesh entry must carry a non-zero FileId.");

        var mainMenuScene = catalog.Entries.FirstOrDefault(e =>
            e.Group == "Scenes" && e.MemberName == "MainMenu" && SequenceEqual(e.FolderSegments, "GateTests", "Fixtures_AssetCatalog", "Scenes"));
        Assert.IsNotNull(mainMenuScene, "No Scenes entry for Scenes/MainMenu.unity.");

        var enemyConfig = catalog.Entries.FirstOrDefault(e =>
            e.Group == "ScriptableObjects" && e.MemberName == "EnemyConfig" && SequenceEqual(e.FolderSegments, "GateTests", "Fixtures_AssetCatalog", "Config"));
        Assert.IsNotNull(enemyConfig, "No ScriptableObjects entry for Config/EnemyConfig.asset.");
    }

    [Test]
    public void Regenerate_SecondRun_IsByteStable_AndReportsNoWrite()
    {
        AssetCatalogGenerator.Regenerate();
        Assert.IsTrue(File.Exists(SceneBuilderPaths.AssetManifestPath), "First Regenerate must write the asset manifest.");
        var bytesAfterFirstRun = File.ReadAllBytes(SceneBuilderPaths.AssetManifestPath);

        var changed = AssetCatalogGenerator.Regenerate();

        Assert.IsFalse(changed, "A second Regenerate with no asset change must report no write.");
        Assert.AreEqual(bytesAfterFirstRun, File.ReadAllBytes(SceneBuilderPaths.AssetManifestPath),
            "The manifest bytes must be byte-identical across a no-op regen.");
    }

    [Test]
    public void AssetManifestPath_IsOutsideAssets()
    {
        var manifestFull = Path.GetFullPath(SceneBuilderPaths.AssetManifestPath);
        var assetsPrefix = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
        Assert.IsFalse(manifestFull.StartsWith(assetsPrefix, System.StringComparison.Ordinal),
            "The asset catalog manifest must live outside Assets/ — a write there would cost a domain reload.");
    }

    private static bool SequenceEqual(string[] actual, params string[] expected) =>
        actual.SequenceEqual(expected);
}
