using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// M4 (asset references) bidirectional round-trip gate tests. Each test DRIVES a real change on one
// side — an authored builder-source edit, or a live EditMode scene edit assigning/clearing a real
// Material asset — through the programmatic Build/Sync APIs and ASSERTS propagation on the other
// side, exercising the real path->GUID->objectReferenceValue behavior in a live editor. Mirrors
// RoundTripComponentTests: temp builder .cs + temp sidecar in a system temp dir, an EmptyScene per
// test, [SetUp]/[TearDown] cleanup. Real Material assets (Red.mat / Blue.mat) are created under
// Assets/GateTests/Fixtures so AssetDatabase can resolve path<->GUID, and deleted in [TearDown].
public class RoundTripAssetRefTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripAssetRefTemp.unity";
    private const string FixturesDir = "Assets/GateTests/Fixtures";
    private const string RedPath = FixturesDir + "/Red.mat";
    private const string BluePath = FixturesDir + "/Blue.mat";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    // Wrap a Build-body fragment in a minimal ISceneDefinition the Core Roslyn parser understands.
    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class RoundTripAssetScene : ISceneDefinition
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
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtar_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripAssetScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripAssetScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures");
        }

        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), RedPath);
        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), BluePath);
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
        AssetDatabase.DeleteAsset(BluePath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }
    }

    // 1. code->scene: authoring an Asset("...Red.mat") material on a MeshRenderer materializes the real
    //    asset onto the live renderer's shared material (path -> GUID -> objectReferenceValue).
    [Test]
    public void CodeToScene_AuthoredMaterial_MaterializesOnRenderer()
    {
        // Phase 1: build the object alone so it is mapped.
        File.WriteAllText(_builderPath, Source("        var surface = scene.Add(\"Surface\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the renderer + material onto the existing object and rebuild.
        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + RedPath + "\") }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        Assert.IsNotNull(surface, "Surface was not created by SceneBuilderBuild.Run");
        var mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "Authored MeshRenderer was not materialized on Surface");
        Assert.AreEqual(LoadMaterial(RedPath), mr.sharedMaterial,
            "Authored Asset(Red.mat) did not materialize onto the renderer's shared material");
        Assert.AreEqual(RedPath, AssetDatabase.GetAssetPath(mr.sharedMaterial),
            "Materialized material does not resolve back to the Red.mat asset path");
    }

    // 2. scene->code: assigning a real material asset onto a mapped MeshRenderer in the scene writes an
    //    Asset("...Red.mat") setter back into the builder source (GUID -> re-derived DisplayPath).
    [Test]
    public void SceneToCode_AssignedMaterial_WritesAssetPath()
    {
        // Phase 1: build the object alone so it is mapped.
        File.WriteAllText(_builderPath, Source("        var surface = scene.Add(\"Surface\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author a BARE renderer onto the existing object and rebuild (maps the component,
        // no material field yet).
        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>();"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        Assert.IsNotNull(surface, "Surface was not created by SceneBuilderBuild.Run");
        var mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "MeshRenderer was not materialized on Surface");

        // Assign the real Red.mat asset in the scene.
        mr.sharedMaterial = LoadMaterial(RedPath);

        var result = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite an assigned material");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Asset(\"" + RedPath + "\")", rewritten,
            "Builder source did not gain an Asset(Red.mat) reference for the assigned material.\n" + rewritten);
    }

    // 3. swap (scene->code): assigning a DIFFERENT material asset over an existing authored one rewrites
    //    the Asset("...") argument to the new asset's path.
    [Test]
    public void SceneToCode_SwappedMaterial_UpdatesAssetPath()
    {
        // Phase 1: build the object alone so it is mapped.
        File.WriteAllText(_builderPath, Source("        var surface = scene.Add(\"Surface\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the renderer WITH Red.mat and rebuild (maps component + assigns Red).
        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + RedPath + "\") }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        var mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "MeshRenderer was not materialized on Surface");
        Assert.AreEqual(LoadMaterial(RedPath), mr.sharedMaterial, "Phase-2 build did not assign Red.mat");

        // Swap to Blue.mat in the scene.
        mr.sharedMaterial = LoadMaterial(BluePath);

        var result = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a swapped material");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Asset(\"" + BluePath + "\")", rewritten,
            "Builder source did not update to the swapped-in Blue.mat path.\n" + rewritten);
        StringAssert.DoesNotContain(RedPath, rewritten,
            "Builder source still carries the old Red.mat path after the swap.\n" + rewritten);
    }

    // 4. clear to None (scene->code): clearing a material slot to None in the scene rewrites the source
    //    argument to the None form Asset(null). Uses a two-element material array so the field stays
    //    non-default (a single null slot can equal a fresh renderer's default and be filtered out).
    [Test]
    public void SceneToCode_ClearedMaterialToNone_WritesNoneForm()
    {
        // Phase 1: build the object alone so it is mapped.
        File.WriteAllText(_builderPath, Source("        var surface = scene.Add(\"Surface\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the renderer with TWO Red.mat slots and rebuild.
        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + RedPath + "\"), Asset(\"" + RedPath + "\") }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        var mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "MeshRenderer was not materialized on Surface");
        Assert.AreEqual(2, mr.sharedMaterials.Length, "Phase-2 build did not assign two material slots");

        // Clear the FIRST slot to None (keep the array size at 2 so it stays non-default).
        var red = LoadMaterial(RedPath);
        mr.sharedMaterials = new Material[] { null, red };

        var result = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a cleared material slot");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Asset(null)", rewritten,
            "Builder source did not use the None form Asset(null) for the cleared material slot.\n" + rewritten);
    }

    // 5. author None -> scene (code->scene): authoring Asset(null) over a previously-assigned material
    //    clears the live renderer's slot (SetAssetRef with a null GUID => objectReferenceValue = null).
    [Test]
    public void CodeToScene_AuthoredNone_ClearsSlot()
    {
        // Phase 1: build the object alone so it is mapped.
        File.WriteAllText(_builderPath, Source("        var surface = scene.Add(\"Surface\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the renderer WITH Red.mat and rebuild (assigns Red).
        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + RedPath + "\") }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        var mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "MeshRenderer was not materialized on Surface");
        Assert.AreEqual(LoadMaterial(RedPath), mr.sharedMaterial, "Phase-2 build did not assign Red.mat");

        // Phase 3: author the None form and rebuild — the slot must clear.
        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(null) }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "MeshRenderer disappeared after authoring the None form");
        Assert.IsNull(mr.sharedMaterial,
            "Authoring Asset(null) did not clear the renderer's shared material slot");
    }
}
