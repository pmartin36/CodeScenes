using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// The component-field reader's VISIBILITY blind spot.
//
// Live repro (2026-07-30): a Canvas authored with `c.Set("m_SortingOrder", 3)` was set to 7, 9, 21
// and 41 in the editor and the builder source stayed at 3 every time ("Scene already matches code").
// Code->scene worked. The cause is not transport or identity: CollectFields walked the component
// with SerializedProperty.NextVisible, which yields exactly the properties a DEFAULT inspector would
// draw. Anything a custom editor draws by hand (Canvas m_SortingOrder / m_SortingLayerID /
// m_TargetDisplay, Rigidbody m_Constraints, Renderer sorting) carries the native hidden flag, so the
// reader never saw it and every scene->code diff for it came back empty.
//
// The class, not the field: the reader must enumerate SERIALIZED state (Next), not INSPECTOR-VISIBLE
// state (NextVisible), and filter by an explicit name list instead of by Unity's inspector-drawing
// flag. These tests pin both halves — hidden authored state must round-trip, and the bookkeeping /
// bake-derived properties that NextVisible used to hide for free must stay out.
public class HiddenSerializedFieldSyncTests
{
    private const string ScenePath = "Assets/GateTests/__HiddenFieldTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class HiddenFieldScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_hidden_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "HiddenFieldScene.cs");
        _sidecarPath = Path.Combine(_dir, "HiddenFieldScene.sbmap.json");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
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
    }

    private static void SaveActiveScene() =>
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

    // ---- Unit: the reader must see hidden-but-authored serialized state --------------------

    [Test]
    public void ReadComponent_CanvasSortingOrder_IsCaptured()
    {
        var go = new GameObject("Canvas");
        var canvas = go.AddComponent<Canvas>();
        canvas.sortingOrder = 7; // hidden from a default inspector; CanvasEditor draws it by hand
        SaveActiveScene();

        var data = SerializedFieldBridge.ReadComponent(canvas);

        Assert.IsTrue(data.Fields.TryGetValue("m_SortingOrder", out var node),
            "Canvas.m_SortingOrder was edited in the scene but the reader did not capture it — the " +
            "exact live bug: every scene->code sync reported 'Scene already matches code'.");
        Assert.AreEqual(ValueNode.Primitive.Int(7), node);
    }

    [Test]
    public void ReadComponent_RigidbodyConstraints_IsCaptured()
    {
        var go = new GameObject("Body");
        var rb = go.AddComponent<Rigidbody>();
        rb.constraints = RigidbodyConstraints.FreezePositionY; // hidden; drawn by RigidbodyEditor
        SaveActiveScene();

        var data = SerializedFieldBridge.ReadComponent(rb);

        Assert.IsTrue(data.Fields.TryGetValue("m_Constraints", out var node),
            "Rigidbody.m_Constraints is the same class of bug as Canvas.m_SortingOrder — a real " +
            "authored field hidden from the default inspector. Fixing only Canvas is not a fix.");
        Assert.AreEqual(
            new ValueNode.Enum("UnityEngine.RigidbodyConstraints", new[] { "FreezePositionY" }, IsFlags: false),
            node,
            "m_Constraints is an Integer-typed native property whose value is a [Flags] enum; it must " +
            "read as a typed member NAME, not a raw int.");
    }

    [Test]
    public void ReadComponent_DisabledComponent_CapturesEnabledFlag()
    {
        var go = new GameObject("Canvas");
        var canvas = go.AddComponent<Canvas>();
        canvas.enabled = false; // m_Enabled is hidden: the inspector draws it in the component header
        SaveActiveScene();

        var data = SerializedFieldBridge.ReadComponent(canvas);

        Assert.IsTrue(data.Fields.TryGetValue("m_Enabled", out var node),
            "Disabling a component in the scene must reach the builder source — m_Enabled is hidden " +
            "from the property walk because the inspector draws it in the component header.");
        Assert.AreEqual(ValueNode.Primitive.Bool(false), node);
    }

    // ---- Unit: what visibility used to filter for free must stay filtered -------------------

    [Test]
    public void ReadComponent_ManagedComponent_ExcludesSerializationBookkeeping()
    {
        var go = new GameObject("Canvas");
        go.AddComponent<Canvas>();
        var scaler = go.AddComponent<CanvasScaler>();
        SaveActiveScene();

        var data = SerializedFieldBridge.ReadComponent(scaler);

        foreach (var bookkeeping in new[]
                 {
                     "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance",
                     "m_PrefabAsset", "m_GameObject", "m_Script", "m_EditorHideFlags",
                     "m_EditorClassIdentifier", "m_Name",
                 })
        {
            Assert.IsFalse(data.Fields.TryGetValue(bookkeeping, out _),
                $"'{bookkeeping}' is serialization/editor bookkeeping and must never be surfaced as a " +
                "field. Enumerating all serialized properties instead of only the visible ones must " +
                "not let it leak into the builder source.");
        }
    }

    [Test]
    public void ReadComponent_Renderer_ExcludesBakeDerivedState()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var renderer = go.GetComponent<MeshRenderer>();
        var so = new SerializedObject(renderer);
        so.FindProperty("m_LightmapIndex").intValue = 3; // as a lightmap bake would leave it
        so.ApplyModifiedPropertiesWithoutUndo();
        SaveActiveScene();

        var data = SerializedFieldBridge.ReadComponent(renderer);

        foreach (var derived in new[]
                 {
                     "m_LightmapIndex", "m_LightmapIndexDynamic", "m_LightmapTilingOffset",
                     "m_LightmapTilingOffsetDynamic", "m_StaticBatchInfo", "m_StaticBatchRoot",
                     "m_SelectedEditorRenderState",
                 })
        {
            Assert.IsFalse(data.Fields.TryGetValue(derived, out _),
                $"'{derived}' is regenerated by a lightmap bake / static batching, not authored. " +
                "Writing it into the builder source would push stale bake output back onto the scene " +
                "on the next code->scene rebuild.");
        }
    }

    [Test]
    public void ReadComponent_DisabledSpatialComponent_OmitsEnabledFlag()
    {
        var go = new GameObject("Crate");
        var sizer = go.AddComponent<SceneBuilder.Authoring.FitSize>();
        sizer.enabled = false;
        SaveActiveScene();

        var data = SerializedFieldBridge.ReadComponent(sizer);

        Assert.IsFalse(data.Fields.TryGetValue("m_Enabled", out _),
            "FitSize is authored as `.FitSize(height: 2f)` — a closed keyword grammar with no " +
            "argument for m_Enabled. Surfacing it makes the writer emit `.FitSize(m_Enabled: false)`, " +
            "which the parser then rejects as an unknown argument on the next read.");
    }

    // ---- End to end: the live repro, through the real sync ---------------------------------

    [Test]
    public void SceneToCode_CanvasSortingOrderEdit_ReachesBuilderSource()
    {
        File.WriteAllText(_builderPath, Source(
            "        var ui = scene.Add(\"Ui\");\n" +
            "        ui.Component<UnityEngine.Canvas>(c => c.Set(\"m_SortingOrder\", 3));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var ui = EditorSceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == "Ui");
        Assert.IsNotNull(ui, "Ui was not created by SceneBuilderBuild.Run");
        var canvas = ui.GetComponent<Canvas>();
        Assert.IsNotNull(canvas, "Authored Canvas was not materialized on Ui");
        Assert.AreEqual(3, canvas.sortingOrder, "code->scene did not apply the authored m_SortingOrder");

        canvas.sortingOrder = 7; // the live edit that used to vanish

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(
            _builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsTrue(result.Changed,
            "Sync reported 'Scene already matches code' after m_SortingOrder was changed 3 -> 7.");
        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("\"m_SortingOrder\", 7", rewritten,
            "The edited m_SortingOrder never reached the builder source.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(
            _builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed,
            "A second sync with no further edit still changed the source — the hidden field does not " +
            "settle (it would re-sync on every keystroke).");
    }

    [Test]
    public void SceneToCode_NewlyIntroducedHiddenField_ReachesBuilderSource()
    {
        File.WriteAllText(_builderPath, Source(
            "        var ui = scene.Add(\"Ui\");\n" +
            "        ui.Component<UnityEngine.Canvas>(c => c.Set(\"m_RenderMode\", 0));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var ui = EditorSceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == "Ui");
        var canvas = ui.GetComponent<Canvas>();
        canvas.sortingOrder = 41; // never authored — must be INTRODUCED into the source

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(
            _builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsTrue(result.Changed, "Sync saw no change after a hidden field was set for the first time.");
        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("\"m_SortingOrder\", 41", rewritten,
            "A never-authored hidden field must be introduced into the builder source.\n" + rewritten);
        StringAssert.Contains("\"m_RenderMode\", 0", rewritten,
            "The pre-existing authored field was lost by the rewrite.\n" + rewritten);
    }
}
