using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// Spec 32 C6: a field a user cannot set in the Inspector is not author intent and must not reach
// builder source. SpriteRenderer.m_WasSpriteAssigned has no Inspector control at all; m_Size is
// recomputed from the sprite's bounds whenever a sprite is assigned. The exclusion is TYPE-QUALIFIED
// (declaring type + property path root segment), never a bare property name — BoxCollider,
// BoxCollider2D and Scrollbar all declare their own, genuinely authorable, m_Size.
public class InspectorVisibilityRuleTests
{
    private const string ScenePath = "Assets/GateTests/__InspectorVisibilityRuleTemp.unity";
    private const string FixturesDir = "Assets/GateTests/Fixtures_InspectorVisibilityRule";
    private const string PrefabPath = FixturesDir + "/InspectorVisibilityRulePrefab.prefab";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;
    private Sprite _sprite;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class InspectorVisibilityRuleScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_ivr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "InspectorVisibilityRuleScene.cs");
        _sidecarPath = Path.Combine(_dir, "InspectorVisibilityRuleScene.sbmap.json");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // A real, independent Sprite asset oracle — no project asset creation needed (avoids
        // racing any other suite's asset fixtures over a shared folder).
        _sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        Assert.IsNotNull(_sprite, "Could not resolve the built-in UISprite Sprite oracle - test premise invalid");
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

        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }
    }

    // ---- Deliverable 1: the live read omits both fields, and only those two -------------------

    [Test]
    public void ReadComponent_SpriteRendererWithSpriteAssigned_OmitsWasSpriteAssignedAndSize()
    {
        var go = new GameObject("Sprite");
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _sprite;
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var data = SerializedFieldBridge.ReadComponent(sr, resolveSceneRef: null);

        Assert.IsFalse(data.Fields.TryGetValue("m_WasSpriteAssigned", out _),
            "m_WasSpriteAssigned has no Inspector control - Unity sets it itself on sprite " +
            "assignment, so it is not author intent and must never reach builder source.");
        Assert.IsFalse(data.Fields.TryGetValue("m_Size", out _),
            "SpriteRenderer.m_Size is recomputed from the sprite's bounds whenever a sprite is " +
            "assigned, so it is not author intent and must never reach builder source.");
    }

    [Test]
    public void ReadComponent_SpriteRendererWithSpriteAssigned_StillCapturesSpriteColorAndSorting()
    {
        var go = new GameObject("Sprite");
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _sprite;
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var data = SerializedFieldBridge.ReadComponent(sr, resolveSceneRef: null);

        Assert.IsTrue(data.Fields.ContainsKey("m_Sprite"), "m_Sprite must still be reported.");
        Assert.IsTrue(data.Fields.ContainsKey("m_Color"), "m_Color must still be reported.");
        Assert.IsTrue(data.Fields.ContainsKey("m_SortingOrder"), "m_SortingOrder must still be reported.");
    }

    // ---- Deliverable 1: end to end, through a real sync, and a fixed point --------------------

    [Test]
    public void SceneToCode_SpriteAssignedInScene_SourceCarriesOnlyTheSpriteAndIsAFixedPoint()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Sprite\").Component<UnityEngine.SpriteRenderer>();"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var sr = FindRoot(EditorSceneManager.GetActiveScene(), "Sprite").GetComponent<SpriteRenderer>();
        sr.sprite = _sprite;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(
            _builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change after a sprite was assigned in the scene.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("\"m_Sprite\"", rewritten,
            "The assigned sprite never reached the builder source.\n" + rewritten);
        StringAssert.DoesNotContain("\"m_Size\"", rewritten,
            "m_Size must never reach builder source.\n" + rewritten);
        StringAssert.DoesNotContain("\"m_WasSpriteAssigned\"", rewritten,
            "m_WasSpriteAssigned must never reach builder source.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(
            _builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "A second sync with no further edit must be a fixed point.");

        var rebuild = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, rebuild.PlanOpCount, "Rebuilding from the converged source must apply zero ops.");
    }

    // ---- Deliverable 1: the exclusion loses nothing code->scene ------------------------------

    [Test]
    public void Build_SpriteRendererAuthoringOnlyTheSprite_UnitySetsSizeAndWasSpriteAssignedItself()
    {
        File.WriteAllText(_builderPath, Source(
            "        var sprite = scene.Add(\"Sprite\");\n" +
            "        sprite.Component<UnityEngine.SpriteRenderer>(c => c.Set(\"m_Sprite\", Builtin(\"UISprite\", \"Sprite\")));"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var sr = FindRoot(EditorSceneManager.GetActiveScene(), "Sprite").GetComponent<SpriteRenderer>();
        Assert.IsNotNull(sr, "Authored SpriteRenderer was not materialized on Sprite");
        Assert.AreEqual(_sprite, sr.sprite, "Build did not assign the authored sprite - test premise invalid");

        var so = new SerializedObject(sr);
        var size = so.FindProperty("m_Size").vector2Value;
        var wasAssigned = so.FindProperty("m_WasSpriteAssigned").boolValue;

        Assert.IsTrue(wasAssigned,
            "Unity must set m_WasSpriteAssigned itself when the sprite is assigned - the exclusion loses nothing.");
        Assert.AreEqual(new Vector2(_sprite.bounds.size.x, _sprite.bounds.size.y), size,
            "Unity must derive m_Size from the sprite's bounds itself - the exclusion loses nothing.");
    }

    // ---- Deliverable 2/3 (the rule is type-qualified, not a bare name) -----------------------

    [Test]
    public void ReadComponent_OtherTypesDeclaringMSize_StillCaptureIt()
    {
        var box = new GameObject("Box").AddComponent<BoxCollider>();
        var box2D = new GameObject("Box2D").AddComponent<BoxCollider2D>();
        var scrollbar = new GameObject("Scrollbar").AddComponent<UnityEngine.UI.Scrollbar>();
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        Assert.IsTrue(SerializedFieldBridge.ReadComponent(box, resolveSceneRef: null).Fields.ContainsKey("m_Size"),
            "BoxCollider.m_Size is a first-class Inspector control (the collider's size) and must " +
            "still be captured. A bare-name exclusion would silently stop it round-tripping.");
        Assert.IsTrue(SerializedFieldBridge.ReadComponent(box2D, resolveSceneRef: null).Fields.ContainsKey("m_Size"),
            "BoxCollider2D.m_Size is a first-class Inspector control and must still be captured.");
        Assert.IsTrue(SerializedFieldBridge.ReadComponent(scrollbar, resolveSceneRef: null).Fields.ContainsKey("m_Size"),
            "Scrollbar.m_Size is a first-class Inspector control and must still be captured.");
    }

    // ---- Deliverable 4 (documented, current, unproven-dual-emission case) --------------------

    [Test]
    public void ReadComponent_SortingLayerPair_BothStillCaptured()
    {
        var go = new GameObject("Sprite");
        var sr = go.AddComponent<SpriteRenderer>();
        var canvasGo = new GameObject("Canvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var spriteData = SerializedFieldBridge.ReadComponent(sr, resolveSceneRef: null);
        Assert.IsTrue(spriteData.Fields.ContainsKey("m_SortingLayer"),
            "SpriteRenderer serializes m_SortingLayer (the short name) alongside m_SortingLayerID; both must round-trip.");
        Assert.IsTrue(spriteData.Fields.ContainsKey("m_SortingLayerID"),
            "SpriteRenderer's m_SortingLayerID must round-trip.");

        var canvasData = SerializedFieldBridge.ReadComponent(canvas, resolveSceneRef: null);
        Assert.IsTrue(canvasData.Fields.ContainsKey("m_SortingLayerID"),
            "Canvas's m_SortingLayerID must round-trip.");
        Assert.IsFalse(canvasData.Fields.ContainsKey("m_SortingLayer"),
            "Canvas serializes only m_SortingLayerID, never m_SortingLayer - unlike SpriteRenderer.");
    }

    // ---- The second exclusion channel: prefab instance property modifications ----------------

    [Test]
    public void SnapshotRead_PrefabInstanceSpriteAssignedOnInstance_OverridesCarryOnlyTheSprite()
    {
        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_InspectorVisibilityRule");
        }

        var source = new GameObject("InspectorVisibilityRule_Source");
        source.AddComponent<SpriteRenderer>();
        PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
        UnityEngine.Object.DestroyImmediate(source);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        var root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
        root.name = "Instance";
        var sr = root.GetComponent<SpriteRenderer>();
        sr.sprite = _sprite;
        PrefabUtility.RecordPrefabInstancePropertyModifications(sr);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = SceneSnapshotReader.Read(scene, resolveSceneRef: null).Roots.First(r => r.Name == "Instance");

        var leaked = node.Overrides
            .Select(o => o.PropertyPath.Split('.')[0])
            .Where(root0 => root0 == "m_Size" || root0 == "m_WasSpriteAssigned")
            .ToArray();
        Assert.IsEmpty(leaked,
            "A prefab instance's property-modification read must apply the same exclusion as the " +
            "live component read. Leaked: " + string.Join(", ", leaked));

        Assert.IsTrue(node.Overrides.Any(o => o.PropertyPath == "m_Sprite"),
            "The genuinely authored m_Sprite override must still be present.");

        AssetDatabase.DeleteAsset(PrefabPath);
    }

    // ---- The exclusion rule lives in exactly one place ----------------------------------------

    private static readonly string[] ExclusionLiterals =
    {
        "m_WasSpriteAssigned", "m_LightmapIndex", "m_ObjectHideFlags", "m_StaticBatchInfo",
    };

    [Test]
    public void Package_SerializedFieldExclusionsAreDeclaredInOneFile()
    {
        var root = SceneBuilderPaths.PackageRootPath;
        Assert.IsNotNull(root, "SceneBuilderPaths.PackageRootPath resolved to null - cannot scan the " +
            "package for exclusion-list literals (this must FAIL, never vacuously pass).");

        var hits = new HashSet<string>();
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (ExclusionLiterals.Any(text.Contains))
            {
                hits.Add(Path.GetFileName(file));
            }
        }

        CollectionAssert.AreEquivalent(new[] { "SerializedFieldExclusions.cs" }, hits,
            "The 'what is not author intent' exclusion literals must be declared in exactly one file " +
            "(SerializedFieldExclusions.cs), not restated or duplicated elsewhere. Found in: " +
            string.Join(", ", hits));
    }
}
