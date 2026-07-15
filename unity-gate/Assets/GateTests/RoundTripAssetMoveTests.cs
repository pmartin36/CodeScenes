using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// M4 GUID-authoritative survival gate. Proves the spec's move/rename stability intent
// (specs/05-m4-asset-references.md §"Move/rename stability"): an asset referenced by an authored
// Asset("<path>") that is MOVED/RENAMED in the project (GUID unchanged, path stale) must STILL
// resolve on Build (code->scene) and re-derive to the new path on Sync (scene->code) — while a
// GENUINELY deleted asset (GUID maps to nothing) stays a loud, located error. Mirrors
// RoundTripAssetRefTests' fixture pattern: real Material assets under Assets/GateTests/Fixtures so
// AssetDatabase resolves path<->GUID, temp builder .cs + temp sidecar, [SetUp]/[TearDown] cleanup.
public class RoundTripAssetMoveTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripAssetMoveTemp.unity";
    private const string FixturesDir = "Assets/GateTests/Fixtures";
    private const string RedPath = FixturesDir + "/Red.mat";
    private const string MovedPath = FixturesDir + "/Crimson.mat";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class RoundTripAssetMoveScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name)
    {
        return scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);
    }

    private static Material LoadMaterial(string path) => AssetDatabase.LoadAssetAtPath<Material>(path);

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtam_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripAssetMoveScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripAssetMoveScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures");
        }

        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), RedPath);
        AssetDatabase.SaveAssets();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }

        AssetDatabase.DeleteAsset(RedPath);
        AssetDatabase.DeleteAsset(MovedPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }
    }

    // Build the object + authored Asset(RedPath) material so the renderer is mapped and the sidecar
    // Assets[] cache records the material's GUID at its current path.
    private void BuildWithRedMaterial()
    {
        File.WriteAllText(_builderPath, Source("        var surface = scene.Add(\"Surface\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + RedPath + "\") }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
    }

    // 1. MOVE survives (code->scene): after building a scene whose source authors Asset(RedPath), the
    //    asset is renamed/moved (GUID unchanged, authored path now stale). Re-running Build with the
    //    SAME stale-path source must NOT throw and must keep the renderer pointed at the SAME asset
    //    (same GUID) at its NEW location.
    [Test]
    public void CodeToScene_MovedAsset_StillResolvesSameGuid()
    {
        BuildWithRedMaterial();

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        var mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "MeshRenderer was not materialized on Surface");
        Assert.AreEqual(LoadMaterial(RedPath), mr.sharedMaterial, "Phase build did not assign Red.mat");

        var originalGuid = AssetDatabase.AssetPathToGUID(RedPath);
        Assert.IsFalse(string.IsNullOrEmpty(originalGuid), "Red.mat had no GUID before the move");

        var moveError = AssetDatabase.MoveAsset(RedPath, MovedPath);
        Assert.IsEmpty(moveError, "MoveAsset failed: " + moveError);
        AssetDatabase.Refresh();
        Assert.IsTrue(string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(RedPath)),
            "Stale RedPath still resolves after the move — test premise invalid");

        // Same (now-stale-path) source — must survive the move rather than throwing.
        Assert.DoesNotThrow(
            () => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()),
            "Build threw on a moved asset — the reference did not survive the move (GUID unchanged).");

        surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "MeshRenderer disappeared after the moved-asset rebuild");
        Assert.IsNotNull(mr.sharedMaterial, "Renderer material was cleared instead of surviving the move");
        Assert.AreEqual(MovedPath, AssetDatabase.GetAssetPath(mr.sharedMaterial),
            "Renderer material did not follow the asset to its new location");
        Assert.AreEqual(originalGuid, AssetDatabase.AssetPathToGUID(MovedPath),
            "Moved asset changed GUID — test premise invalid");
        Assert.AreEqual(LoadMaterial(MovedPath), mr.sharedMaterial,
            "Renderer no longer points at the same asset after the move");
    }

    // 2. MOVE updates code (scene->code): with the material assigned & authored (Asset(RedPath)),
    //    moving the asset then running Sync must re-derive the source Asset("...") argument to the
    //    asset's NEW path (DisplayPath re-derived from the unchanged GUID).
    [Test]
    public void SceneToCode_MovedAsset_ReDerivesNewPath()
    {
        BuildWithRedMaterial();

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        var mr = surface.GetComponent<MeshRenderer>();
        Assert.AreEqual(LoadMaterial(RedPath), mr.sharedMaterial, "Phase build did not assign Red.mat");
        StringAssert.Contains("Asset(\"" + RedPath + "\")", File.ReadAllText(_builderPath),
            "Source did not start with the authored Red.mat path");

        var moveError = AssetDatabase.MoveAsset(RedPath, MovedPath);
        Assert.IsEmpty(moveError, "MoveAsset failed: " + moveError);
        AssetDatabase.Refresh();

        var result = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite the moved asset path");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Asset(\"" + MovedPath + "\")", rewritten,
            "Builder source did not re-derive to the moved asset's new path.\n" + rewritten);
        StringAssert.DoesNotContain(RedPath, rewritten,
            "Builder source still carries the stale pre-move path.\n" + rewritten);
    }

    // 3. DELETE stays loud (regression): a genuinely deleted asset (GUID maps to nothing) must STILL
    //    raise the located missing-asset error on Build — the move fix must not silence a real delete.
    [Test]
    public void CodeToScene_DeletedAsset_StillErrors()
    {
        BuildWithRedMaterial();

        var originalGuid = AssetDatabase.AssetPathToGUID(RedPath);
        Assert.IsFalse(string.IsNullOrEmpty(originalGuid), "Red.mat had no GUID before the delete");

        // Release every live reference to the material so the delete actually removes it: clear the
        // renderer slot, open a fresh empty scene (drops the scene's reference), and force an immediate
        // unload. While the open scene pins the Material object, AssetDatabase keeps its GUID resolvable.
        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        var mr = surface.GetComponent<MeshRenderer>();
        mr.sharedMaterials = new Material[0];
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorUtility.UnloadUnusedAssetsImmediate();

        Assert.IsTrue(AssetDatabase.DeleteAsset(RedPath), "DeleteAsset(Red.mat) failed");
        AssetDatabase.Refresh();
        // Load is the deletion authority: Unity can keep a recently-deleted GUID→path entry resolvable
        // within a session, but the asset itself no longer loads. That "unloadable GUID" is exactly the
        // MISSING condition the resolver must stay loud about.
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(originalGuid)),
            "Red.mat still loads after delete — test premise invalid");

        // The sidecar Assets[] still caches the (now-dead) GUID at Red.mat, so the resolver recovers the
        // GUID — but the GUID maps to nothing, which MUST stay a loud, located error (not a silent move).
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()),
            "Build did NOT raise a missing-asset error for a genuinely deleted asset.");
        StringAssert.Contains("SceneBuilder", ex.Message, "Missing-asset error was not the located SceneBuilder error");
    }
}
