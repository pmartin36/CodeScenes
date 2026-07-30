using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Editor;

// b2-t4: code->scene EditMode round-trip for the spec's HudScene sample (specs/13-recttransform.md
// checklist items 1, 2, 6, 7). Drives the REAL SceneBuilderBuild.Run against a live editor scene
// (harness mirrored from RoundTripSpatialSyncTests.cs). On create, the plan writes m_LocalPosition
// FIRST, which RE-DERIVES anchoredPosition from the parent's rect on a RectTransform, so a field
// equal to the RectTransform default must still be written or the live value diverges from the
// authored one (QuitButton authors anchoredPos:(0,0), equal to the default). For that reason
// RectTransformDiff.EmitCreate emits Changed = ChannelMask.AllRectFields unconditionally on create
// (SceneBuilder.Core/Diff/RectTransformDiff.cs:75-86), which is what test 1 below verifies against
// the live QuitButton.anchoredPosition.
public class RoundTripRectTransformBuildTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripRectTransformBuildTemp.unity";
    private const float Tol = 1e-4f;

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    // Verbatim from specs/13-recttransform.md:191-206, parameterized ONLY on the Panel's
    // `anchoredPos:` argument and an optional extra chained call on the Panel (D8).
    private static string BuilderSource(string panelAnchoredPos = "(-10, -10)", string panelExtraChain = "")
    {
        return $@"
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using SceneBuilder.Authoring;
public class HudScene : ISceneDefinition {{
    public void Build(SceneRoot scene) {{
        var canvas = scene.Add(""Canvas"")
                          .Component<Canvas>(c => c.Set(""m_RenderMode"", 0))
                          .Component<CanvasScaler>(_ => {{ }})
                          .Component<GraphicRaycaster>(_ => {{ }});
        scene.Add(""EventSystem"").Component<EventSystem>(_ => {{ }});

        var panel = canvas.Add(""Panel"").RectTransform(
            anchoredPos: {panelAnchoredPos}, sizeDelta: (200, 120),
            anchorMin: (1, 1), anchorMax: (1, 1), pivot: (1, 1)){panelExtraChain};

        panel.Add(""QuitButton"")
             .RectTransform(anchoredPos: (0, 0), sizeDelta: (160, 40), pivot: (0.5f, 0.5f))
             .Component<Image>(_ => {{ }});
    }}
}}";
    }

    private static GameObject Find(Scene scene, string path)
    {
        var parts = path.Split('/');
        var root = scene.GetRootGameObjects().FirstOrDefault(go => go.name == parts[0]);
        var t = root == null ? null : root.transform;
        for (int i = 1; t != null && i < parts.Length; i++)
        {
            t = t.Find(parts[i]);
        }
        return t == null ? null : t.gameObject;
    }

    private static void AssertVec2(Vector2 expected, Vector2 actual, string what)
    {
        Assert.AreEqual(expected.x, actual.x, Tol, $"{what}.x: expected {expected} got {actual}");
        Assert.AreEqual(expected.y, actual.y, Tol, $"{what}.y: expected {expected} got {actual}");
    }

    private SceneBuilderBuild.BuildResult RunBuild(Scene scene, string panelAnchoredPos = "(-10, -10)", string panelExtraChain = "")
    {
        File.WriteAllText(_builderPath, BuilderSource(panelAnchoredPos, panelExtraChain));
        return SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtrtb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "HudScene.cs");
        _sidecarPath = Path.Combine(_dir, "HudScene.sbmap.json");
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

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    // Checklist 1: Author HudScene (sample above) — the Panel and QuitButton's live RectTransforms
    // must match the authored layout exactly, including a field equal to the RectTransform default
    // (QuitButton's anchoredPos:(0,0) — the F1 regression this test exists to catch).
    [Test]
    public void Build_HudScene_PanelAndButtonMatchAuthoredRectTransformLayout()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var result = RunBuild(scene);

        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for the HudScene sample: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var canvas = Find(EditorSceneManager.GetActiveScene(), "Canvas");
        Assert.IsNotNull(canvas, "Canvas was not created");
        Assert.IsInstanceOf<RectTransform>(canvas.transform, "Canvas must carry a RectTransform (Canvas's RequireComponent promotion)");

        var eventSystem = Find(EditorSceneManager.GetActiveScene(), "EventSystem");
        Assert.IsNotNull(eventSystem, "EventSystem was not created");
        Assert.IsNotInstanceOf<RectTransform>(eventSystem.transform, "EventSystem has no UI layout and must stay a plain Transform");

        var panel = (RectTransform)Find(EditorSceneManager.GetActiveScene(), "Canvas/Panel").transform;
        AssertVec2(new Vector2(-10, -10), panel.anchoredPosition, "Panel.anchoredPosition");
        AssertVec2(new Vector2(200, 120), panel.sizeDelta, "Panel.sizeDelta");
        AssertVec2(new Vector2(1, 1), panel.anchorMin, "Panel.anchorMin");
        AssertVec2(new Vector2(1, 1), panel.anchorMax, "Panel.anchorMax");
        AssertVec2(new Vector2(1, 1), panel.pivot, "Panel.pivot");

        var button = (RectTransform)Find(EditorSceneManager.GetActiveScene(), "Canvas/Panel/QuitButton").transform;
        AssertVec2(new Vector2(0, 0), button.anchoredPosition,
            "QuitButton.anchoredPosition — F1: a field equal to the RectTransform default must still be written on create, " +
            "since the prior m_LocalPosition write re-derives it from the parent's rect otherwise.");
        AssertVec2(new Vector2(160, 40), button.sizeDelta, "QuitButton.sizeDelta");
        AssertVec2(new Vector2(0.5f, 0.5f), button.anchorMin, "QuitButton.anchorMin");
        AssertVec2(new Vector2(0.5f, 0.5f), button.anchorMax, "QuitButton.anchorMax");
        AssertVec2(new Vector2(0.5f, 0.5f), button.pivot, "QuitButton.pivot");

        Assert.IsTrue(File.Exists(ScenePath), "Scene was not saved");
    }

    // Checklist 2: the sidecar has exactly one GameObject entry per UI node, correctly parented, and
    // no separate transform/RectTransform component entry (the RectTransform is the object's
    // transform, never a Components[] item — spec 13's "IdentityMap / sidecar changes: None new").
    [Test]
    public void Build_HudScene_SidecarHasOneGameObjectEntryPerUiNode_NoTransformComponentEntry()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));

        IdentityMapEntry ByName(string name) => map.Entries.SingleOrDefault(e => e.Kind == "GameObject" && e.Name == name);

        var canvasEntry = ByName("Canvas");
        var panelEntry = ByName("Panel");
        var buttonEntry = ByName("QuitButton");
        var eventSystemEntry = ByName("EventSystem");

        Assert.IsNotNull(canvasEntry, "Sidecar has no GameObject entry for Canvas.\n" + File.ReadAllText(_sidecarPath));
        Assert.IsNotNull(panelEntry, "Sidecar has no GameObject entry for Panel");
        Assert.IsNotNull(buttonEntry, "Sidecar has no GameObject entry for QuitButton");
        Assert.IsNotNull(eventSystemEntry, "Sidecar has no GameObject entry for EventSystem");

        Assert.IsFalse(string.IsNullOrEmpty(canvasEntry.GlobalObjectId), "Canvas entry has an empty GlobalObjectId");
        Assert.IsFalse(string.IsNullOrEmpty(panelEntry.GlobalObjectId), "Panel entry has an empty GlobalObjectId");
        Assert.IsFalse(string.IsNullOrEmpty(buttonEntry.GlobalObjectId), "QuitButton entry has an empty GlobalObjectId");
        Assert.IsFalse(string.IsNullOrEmpty(eventSystemEntry.GlobalObjectId), "EventSystem entry has an empty GlobalObjectId");

        Assert.AreEqual(canvasEntry.LogicalId, panelEntry.ParentLogicalId, "Panel's ParentLogicalId must be the Canvas entry's LogicalId");
        Assert.AreEqual(panelEntry.LogicalId, buttonEntry.ParentLogicalId, "QuitButton's ParentLogicalId must be the Panel entry's LogicalId");

        Assert.IsFalse(map.Entries.Any(e => e.ComponentType != null && e.ComponentType.Contains("Transform")),
            "No sidecar entry may carry a Transform/RectTransform ComponentType — the RectTransform is the object's transform, " +
            "never a separately-mapped Components[] entry.");
    }

    // Checklist 6: editing an authored value in source and re-Building moves the SAME live
    // RectTransform instance in place (no destroy+recreate).
    [Test]
    public void Rebuild_EditedAnchoredPos_MovesSameRectTransformInstanceInPlace()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var panelBefore = Find(EditorSceneManager.GetActiveScene(), "Canvas/Panel");
        Assert.IsNotNull(panelBefore, "Setup: Panel was not created by the first Build");
        var entityIdBefore = panelBefore.GetEntityId();

        var result = RunBuild(EditorSceneManager.GetActiveScene(), panelAnchoredPos: "(-40, -30)");
        Assert.IsEmpty(result.Diagnostics, "Rebuild with an edited anchoredPos reported diagnostics");

        var panelAfter = Find(EditorSceneManager.GetActiveScene(), "Canvas/Panel");
        Assert.IsNotNull(panelAfter, "Panel disappeared after the in-place rebuild");
        Assert.AreEqual(entityIdBefore, panelAfter.GetEntityId(), "Editing anchoredPos must reconcile onto the SAME object instance, not destroy+recreate");

        var rt = (RectTransform)panelAfter.transform;
        AssertVec2(new Vector2(-40, -30), rt.anchoredPosition, "Panel.anchoredPosition after edit");
        Assert.AreEqual(10f, rt.localPosition.x, Tol, "Panel.localPosition.x after anchoredPos edit");
        Assert.AreEqual(20f, rt.localPosition.y, Tol, "Panel.localPosition.y after anchoredPos edit");
    }

    // Checklist 7 (code->scene half, specs/13-recttransform.md:275-276): a rebuild with NO source
    // change is idempotent, asserted OBSERVABLY: identical scene bytes, identical sidecar bytes,
    // identical live rect values, identical instance ids.
    // NOTE: this deliberately does NOT assert BuildResult.PlanOpCount == 0. A settled rebuild of THIS
    // fixture is not zero ops for a reason that has nothing to do with RectTransform: D-1,
    // com.codescenes/Editor/SerializedFieldBridge.cs:48-62 (ReadComponent) prunes every read field
    // whose value equals a freshly-constructed instance's default, and this fixture authors
    // .Component<Canvas>(c => c.Set("m_RenderMode", 0)), where 0 IS the default, so that field is
    // pruned from every snapshot read and re-emits one SetField on every Build, forever
    // (pre-existing, pre-feature; tracked in .agent_handoffs/m-ui-recttransform/scope/bucket-b4.md as
    // D-1). The op-count assertion is owned by
    // RoundTripRectTransformDrivenTests.BuildSyncBuildSync_NoUserEdit_EmitsNoRectOps_AndNoSourceEdits,
    // which pins it on a fixture without the Image so any rect or transform churn is visible there.
    [Test]
    public void Rebuild_NoSourceChange_LeavesSceneAndSidecarByteIdentical()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var panelBefore = Find(EditorSceneManager.GetActiveScene(), "Canvas/Panel");
        var buttonBefore = Find(EditorSceneManager.GetActiveScene(), "Canvas/Panel/QuitButton");
        var canvasEntityBefore = Find(EditorSceneManager.GetActiveScene(), "Canvas").GetEntityId();
        var panelEntityBefore = panelBefore.GetEntityId();
        var buttonEntityBefore = buttonBefore.GetEntityId();
        var sceneBytesBefore = File.ReadAllBytes(ScenePath);
        var sidecarBytesBefore = File.ReadAllBytes(_sidecarPath);
        var panelRectBefore = (RectTransform)panelBefore.transform;
        var apBefore = panelRectBefore.anchoredPosition;
        var sdBefore = panelRectBefore.sizeDelta;

        var result = RunBuild(EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(result.Diagnostics, "No-op rebuild reported diagnostics");

        var panelAfter = Find(EditorSceneManager.GetActiveScene(), "Canvas/Panel");
        var buttonAfter = Find(EditorSceneManager.GetActiveScene(), "Canvas/Panel/QuitButton");

        Assert.AreEqual(canvasEntityBefore, Find(EditorSceneManager.GetActiveScene(), "Canvas").GetEntityId(), "Canvas instance id changed on a no-op rebuild");
        Assert.AreEqual(panelEntityBefore, panelAfter.GetEntityId(), "Panel instance id changed on a no-op rebuild");
        Assert.AreEqual(buttonEntityBefore, buttonAfter.GetEntityId(), "QuitButton instance id changed on a no-op rebuild");

        var panelRectAfter = (RectTransform)panelAfter.transform;
        AssertVec2(apBefore, panelRectAfter.anchoredPosition, "Panel.anchoredPosition after no-op rebuild");
        AssertVec2(sdBefore, panelRectAfter.sizeDelta, "Panel.sizeDelta after no-op rebuild");

        CollectionAssert.AreEqual(sceneBytesBefore, File.ReadAllBytes(ScenePath), "Scene file bytes changed on a no-op rebuild");
        CollectionAssert.AreEqual(sidecarBytesBefore, File.ReadAllBytes(_sidecarPath), "Sidecar file bytes changed on a no-op rebuild");
    }

    // D8 (write-side double authority): editing a UI node's scale and position.z applies to the live
    // RectTransform without disturbing its anchoredPosition, and logs no "Unhandled SetField" warning.
    [Test]
    public void Rebuild_UiNodeZAndScaleEdited_AppliesWithoutDisturbingAnchoredPosition_NoUnhandledSetField()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var messages = new System.Collections.Generic.List<string>();
        void Handler(string condition, string stackTrace, LogType type) => messages.Add(condition);
        Application.logMessageReceived += Handler;
        SceneBuilderBuild.BuildResult result;
        try
        {
            result = RunBuild(EditorSceneManager.GetActiveScene(),
                panelExtraChain: "\n            .Transform(pos: (0f, 0f, 7f), scale: (2f, 2f, 2f))");
        }
        finally
        {
            Application.logMessageReceived -= Handler;
        }

        Assert.IsEmpty(result.Diagnostics, "Rebuild with an edited scale/pos-z reported diagnostics");
        Assert.IsFalse(messages.Any(m => m.Contains("Unhandled SetField")),
            "A Transform SetField applied to a UI node must not be reported as unhandled.\nLogs:\n" + string.Join("\n", messages));

        var panel = Find(EditorSceneManager.GetActiveScene(), "Canvas/Panel");
        Assert.IsNotNull(panel, "Panel disappeared after the rebuild");
        var rt = (RectTransform)panel.transform;

        Assert.AreEqual(7f, rt.localPosition.z, Tol, "Panel.localPosition.z did not land after the edit");
        Assert.AreEqual(new Vector3(2f, 2f, 2f), rt.localScale, "Panel.localScale did not land after the edit");
        AssertVec2(new Vector2(-10, -10), rt.anchoredPosition,
            "Panel.anchoredPosition must be unchanged by the scale/pos-z edit (double authority)");
    }
}
