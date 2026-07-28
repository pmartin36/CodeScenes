using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// b7-t1 (specs/28-typed-asset-catalogs.md): EditMode round-trip proving typed
// `Assets.<Group>.<folders...>.<Leaf>` members Build, through the REAL AssetDatabase
// (AssetCatalogGenerator -> AssetCatalogLoader -> BuilderParser.Parse -> AssetReferenceResolver),
// to the SAME live-asset identity as the equivalent Asset(path[, sub]) string form. b1-b6 are
// already implemented and GREEN; these tests exercise the wired-together Unity boundary that no
// headless POCO test can reach. Checklist #3/#4/#5/#10. Uses AssetCatalogBuildFixtures (shared
// with b7-t2) — do not edit AssetCatalogTests' own private fixture.
public class AssetCatalogBuildTests
{
    private const string ScenePath = "Assets/GateTests/__AssetCatalogBuildTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    // Mirrors RoundTripAssetRefTests.Source / RoundTripSubAssetRefTests.Source — do not invent a
    // new harness. The typed `Assets.Group...Leaf` forms need NO using (BuilderParser parses the
    // member chain directly; the builder file is never compiled for Build).
    private static string Source(string body)
    {
        var assetRefsUsing = body.Contains("Asset(")
            ? "using static SceneBuilder.Authoring.AssetRefs;\n"
            : "";

        return $@"
using SceneBuilder.Authoring;
{assetRefsUsing}public class AssetCatalogBuildScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";
    }

    private static GameObject FindRoot(UnityEngine.SceneManagement.Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        _manifestPath = SceneBuilderPaths.AssetManifestPath;
        _manifestExistedBefore = File.Exists(_manifestPath);
        _manifestBytesBefore = _manifestExistedBefore ? File.ReadAllBytes(_manifestPath) : null;

        AssetCatalogBuildFixtures.Create();

        _dir = Path.Combine(Path.GetTempPath(), "sb_acbt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "AssetCatalogBuildScene.cs");
        _sidecarPath = Path.Combine(_dir, "AssetCatalogBuildScene.sbmap.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        AssetCatalogBuildFixtures.Delete();

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

    // Checklist #3: two same-leaf materials in different folders Build to DISTINCT assets, and the
    // manifest never allocates a numeric-suffixed sibling for the cross-folder case.
    [Test]
    public void CrossFolderSameName_BuildsTwoDistinctMaterials_NoStone2Member()
    {
        File.WriteAllText(_builderPath, Source(
            "        var rock = scene.Add(\"Rock\");\n" +
            "        rock.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { " + AssetCatalogBuildFixtures.TypedRocksStone + " }));\n" +
            "        var skin = scene.Add(\"Skin\");\n" +
            "        skin.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { " + AssetCatalogBuildFixtures.TypedSkinStone + " }));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for a fully-catalogued typed reference.\n" +
            string.Join("\n", result.Diagnostics.Select(d => d.Message)));

        var rock = FindRoot(EditorSceneManager.GetActiveScene(), "Rock");
        var skin = FindRoot(EditorSceneManager.GetActiveScene(), "Skin");
        Assert.IsNotNull(rock, "Rock was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(skin, "Skin was not created by SceneBuilderBuild.Run");

        var rockMat = rock.GetComponent<MeshRenderer>().sharedMaterial;
        var skinMat = skin.GetComponent<MeshRenderer>().sharedMaterial;
        Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Material>(AssetCatalogBuildFixtures.RocksMatPath), rockMat,
            "Rock's typed member did not resolve to the Environment/Rocks/Stone.mat material.");
        Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Material>(AssetCatalogBuildFixtures.SkinMatPath), skinMat,
            "Skin's typed member did not resolve to the Characters/Skin/Stone.mat material.");
        Assert.AreNotEqual(rockMat, skinMat,
            "The two cross-folder same-leaf materials resolved to the SAME asset.");

        var catalog = AssetCatalog.Deserialize(File.ReadAllText(SceneBuilderPaths.AssetManifestPath));
        Assert.IsFalse(catalog.Entries.Any(e => e.Group == "Materials" && e.MemberName == "Stone2"),
            "Cross-folder same-leaf materials must NEVER be suffixed — the folder path disambiguates them.");
    }

    // Checklist #4: same-folder, different type-group typed members each resolve to their OWN
    // group's asset — a Materials member never resolves to the Sprites asset in the same folder
    // (and vice versa). Two separate GameObjects: MeshRenderer and SpriteRenderer both derive from
    // Renderer and Unity refuses to attach a second Renderer-derived component to one GameObject —
    // that constraint is orthogonal to what #4 is testing (group-correct resolution), so the two
    // typed members go on two GameObjects, one per component.
    [Test]
    public void SameFolderDifferentType_TypedMembers_ResolveToOwnGroupAsset()
    {
        File.WriteAllText(_builderPath, Source(
            "        var envMesh = scene.Add(\"EnvMesh\");\n" +
            "        envMesh.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { " + AssetCatalogBuildFixtures.TypedRocksStone + " }));\n" +
            "        var envSprite = scene.Add(\"EnvSprite\");\n" +
            "        envSprite.Component<UnityEngine.SpriteRenderer>(c => c.Set(\"m_Sprite\", " + AssetCatalogBuildFixtures.TypedRocksSprite + "));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for fully-catalogued same-folder typed references.\n" +
            string.Join("\n", result.Diagnostics.Select(d => d.Message)));

        var envMesh = FindRoot(EditorSceneManager.GetActiveScene(), "EnvMesh");
        var envSprite = FindRoot(EditorSceneManager.GetActiveScene(), "EnvSprite");
        Assert.IsNotNull(envMesh, "EnvMesh was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(envSprite, "EnvSprite was not created by SceneBuilderBuild.Run");

        var expectedMat = AssetDatabase.LoadAssetAtPath<Material>(AssetCatalogBuildFixtures.RocksMatPath);
        var expectedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(AssetCatalogBuildFixtures.RocksSpritePath);
        Assert.IsNotNull(expectedMat, "Test premise invalid: Rocks material did not import.");
        Assert.IsNotNull(expectedSprite, "Test premise invalid: Rocks sprite did not import.");

        Assert.AreEqual(expectedMat, envMesh.GetComponent<MeshRenderer>().sharedMaterial,
            "The Materials-group typed member did not resolve to the .mat asset.");
        Assert.AreEqual(expectedSprite, envSprite.GetComponent<SpriteRenderer>().sprite,
            "The Sprites-group typed member did not resolve to the .png sprite asset.");
    }

    // Checklist #5: a typed member for a sub-mesh Builds the SUB-object by identity, and equals
    // what the equivalent string form Asset(containerPath, "Barrel") Builds.
    [Test]
    public void SubMeshTypedMember_BuildsSubMeshByIdentity_EqualsStringForm()
    {
        File.WriteAllText(_builderPath, Source(
            "        var typedWidget = scene.Add(\"TypedWidget\");\n" +
            "        typedWidget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", " + AssetCatalogBuildFixtures.TypedBarrel + "));\n" +
            "        var stringWidget = scene.Add(\"StringWidget\");\n" +
            "        stringWidget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Asset(\"" + AssetCatalogBuildFixtures.BarrelContainerPath + "\", \"" + AssetCatalogBuildFixtures.BarrelSubAssetName + "\")));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for a fully-catalogued typed sub-mesh reference.\n" +
            string.Join("\n", result.Diagnostics.Select(d => d.Message)));

        var expectedSubMesh = AssetDatabase.LoadAllAssetsAtPath(AssetCatalogBuildFixtures.BarrelContainerPath)
            .FirstOrDefault(o => o is Mesh && o.name == AssetCatalogBuildFixtures.BarrelSubAssetName);
        Assert.IsNotNull(expectedSubMesh, "Test premise invalid: the Barrel sub-mesh was not found in the container.");

        var typedWidget = FindRoot(EditorSceneManager.GetActiveScene(), "TypedWidget");
        var stringWidget = FindRoot(EditorSceneManager.GetActiveScene(), "StringWidget");
        Assert.IsNotNull(typedWidget, "TypedWidget was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(stringWidget, "StringWidget was not created by SceneBuilderBuild.Run");

        var typedMesh = typedWidget.GetComponent<MeshFilter>().sharedMesh;
        var stringMesh = stringWidget.GetComponent<MeshFilter>().sharedMesh;
        Assert.AreEqual(expectedSubMesh, typedMesh,
            "The typed sub-mesh member did not Build the sub-object by identity.");
        Assert.AreEqual(stringMesh, typedMesh,
            "The typed sub-mesh member and the equivalent string form Asset(path,\"Barrel\") resolved to DIFFERENT objects.");
    }

    // Checklist #10: a material added AFTER the last regen is invisible to the typed member until
    // an explicit regen — Build REFUSES with a located diagnostic (scene untouched) — while the
    // string Asset(path) fallback Builds meanwhile; a subsequent Regenerate() makes the typed
    // member resolve.
    [Test]
    public void MaterialAddedAfterRegen_TypedMemberRefusesUntilRegen_StringFallbackBuilds()
    {
        var staleManifestBytes = File.ReadAllBytes(SceneBuilderPaths.AssetManifestPath);

        var granitePath = AssetCatalogBuildFixtures.Dir + "/Environment/Rocks/Granite.mat";
        var typedGranite = "Assets.Materials." + AssetCatalogBuildFixtures.RootPrefix + ".Environment.Rocks.Granite";
        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), granitePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // The import auto-regenerated the manifest to include Granite — re-stamp it back to the
        // pre-Granite bytes via plain File IO (outside Assets/, so this does NOT re-trigger
        // OnPostprocessAllAssets) to simulate a not-yet-regenerated, stale catalog.
        File.WriteAllBytes(SceneBuilderPaths.AssetManifestPath, staleManifestBytes);

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        // (a) typed member refuses: Materials is a KNOWN group (Rock/Skin Stone already catalogued)
        // but "Granite" does not resolve in the stale catalog.
        File.WriteAllText(_builderPath, Source(
            "        var granite = scene.Add(\"GraniteWidget\");\n" +
            "        granite.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { " + typedGranite + " }));"));
        var typedResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        Assert.IsNotEmpty(typedResult.Diagnostics,
            "Build silently succeeded on a typed member absent from the (stale) catalog.");
        StringAssert.Contains("Granite", string.Join("\n", typedResult.Diagnostics.Select(d => d.Message)),
            "Refusal diagnostic did not name 'Granite'.");
        Assert.IsNull(FindRoot(EditorSceneManager.GetActiveScene(), "GraniteWidget"),
            "Build mutated the scene despite refusing the unresolvable typed reference.");

        // (b) the string Asset(path) fallback still Builds meanwhile.
        File.WriteAllText(_builderPath, Source(
            "        var granite = scene.Add(\"GraniteWidget\");\n" +
            "        granite.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + granitePath + "\") }));"));
        var stringResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsEmpty(stringResult.Diagnostics,
            "Build reported diagnostics for the string Asset(path) fallback.\n" +
            string.Join("\n", stringResult.Diagnostics.Select(d => d.Message)));
        var graniteWidget = FindRoot(EditorSceneManager.GetActiveScene(), "GraniteWidget");
        Assert.IsNotNull(graniteWidget, "GraniteWidget was not created by the string-form fallback Build.");
        var graniteMat = AssetDatabase.LoadAssetAtPath<Material>(granitePath);
        Assert.AreEqual(graniteMat, graniteWidget.GetComponent<MeshRenderer>().sharedMaterial,
            "The string Asset(path) fallback did not assign the Granite material.");

        // (c) after an explicit Regenerate(), the typed member resolves too.
        AssetCatalogGenerator.Regenerate();
        File.WriteAllText(_builderPath, Source(
            "        var granite = scene.Add(\"GraniteWidget\");\n" +
            "        granite.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { " + typedGranite + " }));"));
        var typedAfterRegen = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsEmpty(typedAfterRegen.Diagnostics,
            "Build still reported diagnostics for the typed member after Regenerate().\n" +
            string.Join("\n", typedAfterRegen.Diagnostics.Select(d => d.Message)));
        graniteWidget = FindRoot(EditorSceneManager.GetActiveScene(), "GraniteWidget");
        Assert.AreEqual(graniteMat, graniteWidget.GetComponent<MeshRenderer>().sharedMaterial,
            "The typed member did not resolve to the Granite material after Regenerate().");
    }
}
