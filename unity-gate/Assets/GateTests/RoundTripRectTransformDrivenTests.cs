using System;
using System.Collections.Generic;
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

// specs/13-recttransform.md checklist item 9, "no churn": the seam proof that RectTransform support
// composes end to end against REAL driving components in a live editor, exercised through a real
// Canvas/CanvasScaler resolution change and a real HorizontalLayoutGroup, never a hand-set
// DrivenChannels fixture (that belongs to RectTransformReadDrivenTests.cs).
//
// This file is the durable, in-repo version of the seam proof. Every PREMISE assertion below exists
// so a future regression that silently stops driving a value (and would otherwise make the case pass
// vacuously) fails loudly instead.
//
// This file also hosts the live-editor pin for the bare-`.RectTransform()` forward-promotion fix — a
// mapped plain-Transform object whose source gains a bare `.RectTransform()` (every field at
// RectTransformFields.Default*) must still promote in place, reusing FixtureC and the harness above
// rather than a new file.
//
// This file also hosts the live-editor pin for the foreign-driven-base-transform hold — a real
// CanvasScaler-driven scale factor change must not make an unchanged builder emit a growing plan
// every rebuild.
public class RoundTripRectTransformDrivenTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripRectTransformDrivenTemp.unity";
    private const float Tol = 1e-4f;

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    // Fixture A: the spec's HudScene (specs/13-recttransform.md:191-206) MINUS QuitButton's
    // `.Component<Image>(_ => { })`. Without the Image, the very first Sync after Build is already a
    // fixed point (WITH the Image, Unity's RequireComponent CanvasRenderer harvest adds noise that
    // is orthogonal to this task's zero-op cycle).
    private const string FixtureA = @"
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using SceneBuilder.Authoring;
public class HudScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var canvas = scene.Add(""Canvas"")
                          .Component<Canvas>(c => {
                              c.Set(""m_RenderMode"", 0);
                              c.Set(""m_PixelPerfect"", false);
                          })
                          .Component<CanvasScaler>(_ => { })
                          .Component<GraphicRaycaster>(_ => { });
        scene.Add(""EventSystem"").Component<EventSystem>(_ => { });

        var panel = canvas.Add(""Panel"").RectTransform(
            anchoredPos: (-10, -10), sizeDelta: (200, 120),
            anchorMin: (1, 1), anchorMax: (1, 1), pivot: (1, 1));

        panel.Add(""QuitButton"")
             .RectTransform(anchoredPos: (0, 0), sizeDelta: (160, 40), pivot: (0.5f, 0.5f));
    }
}";

    // Fixture B: a real HorizontalLayoutGroup with its control fields AUTHORED (setting them on the
    // live component instead would be an M3 field edit the settle Sync would harvest, destroying the
    // byte-identity premise every case below relies on).
    private const string FixtureB = @"
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using SceneBuilder.Authoring;
public class HudScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var canvas = scene.Add(""Canvas"")
                          .Component<Canvas>(c => c.Set(""m_RenderMode"", 0))
                          .Component<CanvasScaler>(_ => { })
                          .Component<GraphicRaycaster>(_ => { });
        scene.Add(""EventSystem"").Component<EventSystem>(_ => { });

        var row = canvas.Add(""Row"")
                        .RectTransform(anchoredPos: (0, 0), sizeDelta: (400, 80))
                        .Component<HorizontalLayoutGroup>(g => {
                            g.Set(""m_ChildControlWidth"", true);
                            g.Set(""m_ChildControlHeight"", true);
                        });
        row.Add(""ItemA"").RectTransform(sizeDelta: (100, 50));
        row.Add(""ItemB"").RectTransform(sizeDelta: (100, 50));
    }
}";

    // Fixture C: a Canvas with a plain (non-UI) child. Never auto-promoted by Unity, so nothing to
    // harvest at settle time; used only to prove the source-driven promotion path (C4).
    private const string FixtureC = @"
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using SceneBuilder.Authoring;
public class HudScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var canvas = scene.Add(""Canvas"")
                          .Component<Canvas>(c => c.Set(""m_RenderMode"", 0))
                          .Component<CanvasScaler>(_ => { })
                          .Component<GraphicRaycaster>(_ => { });
        scene.Add(""EventSystem"").Component<EventSystem>(_ => { });

        var icon = canvas.Add(""Icon"");
    }
}";

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

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private static List<(string Message, LogType Type)> CaptureLogs(Action action)
    {
        var messages = new List<(string, LogType)>();
        void Handler(string condition, string stackTrace, LogType type) => messages.Add((condition, type));
        Application.logMessageReceived += Handler;
        try { action(); }
        finally { Application.logMessageReceived -= Handler; }
        return messages;
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtrtd_" + Guid.NewGuid().ToString("N"));
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

    // Folds "a Sync with no further edit must be a no-op" into one place, reused both as the
    // settle-after-Build step and as the post-assertion confirmation every case ends with.
    private void AssertSyncIsNoOp(Scene scene, string what)
    {
        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, scene);
        Assert.IsFalse(result.Changed, $"NOT CONVERGED: a Sync after {what}, with no further edit, reported Changed=true.");
        Assert.AreEqual(0, result.PatchEdits, $"NOT CONVERGED: a Sync after {what} produced non-zero PatchEdits.");
        Assert.AreEqual(0, result.EditsApplied, $"NOT CONVERGED: a Sync after {what} applied non-zero edits.");
    }

    // Builds the given source into a fresh scene, makes the Canvas genuinely driven (a Build-created
    // Canvas is NOT driven in batchmode until toggled), runs an optional per-fixture reflow step, then
    // asserts the FIRST Sync after Build is already a fixed point (measured true for every fixture in
    // this file — a non-zero value here is a genuine defect).
    private Scene BuildAndSettle(string source, Action<Scene> beforeSettle = null)
    {
        File.WriteAllText(_builderPath, source);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for the fixture: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var canvasGo = Find(scene, "Canvas");
        Assert.IsNotNull(canvasGo, "Setup: Canvas was not created");
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.enabled = false;
        canvas.enabled = true;
        Canvas.ForceUpdateCanvases();
        var canvasRect = (RectTransform)canvasGo.transform;
        Assert.IsNotNull(canvasRect.drivenByObject,
            "PREMISE: a Build-created Canvas must be driven after the " +
            "enabled-toggle + ForceUpdateCanvases recipe, or every byte-identity assertion below is " +
            "unearned.");

        beforeSettle?.Invoke(scene);

        AssertSyncIsNoOp(scene, "the first Sync after Build");

        return scene;
    }

    [Test]
    public void DrivenCanvasResolutionChange_LeavesBuilderByteIdentical_NoRectOrTransformPatch()
    {
        var scene = BuildAndSettle(FixtureA);
        var baseline = File.ReadAllText(_builderPath);

        var canvasGo = Find(scene, "Canvas");
        var canvasRect = (RectTransform)canvasGo.transform;
        Assert.IsNotNull(canvasRect.drivenByObject, "PREMISE: the Canvas must still be driven going into the resolution change.");

        var sdBefore = canvasRect.sizeDelta;
        var lsBefore = canvasRect.localScale;

        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.scaleFactor = 2f;
        Canvas.ForceUpdateCanvases();

        var sdAfter = canvasRect.sizeDelta;
        var lsAfter = canvasRect.localScale;
        var lpAfter = canvasRect.localPosition;

        Assert.AreNotEqual(sdBefore, sdAfter,
            "PREMISE: the resolution/scale change must actually move the Canvas's driven sizeDelta, or this case is vacuous.");
        Assert.AreNotEqual(lsBefore, lsAfter,
            "PREMISE: the resolution/scale change must actually move the Canvas's driven localScale.");
        Assert.AreNotEqual(Vector3.one, lsAfter, "PREMISE: the driven localScale must differ from the unscaled default.");
        Assert.AreNotEqual(Vector3.zero, lpAfter, "PREMISE: the driven localPosition must differ from zero.");

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, scene);
        Assert.IsFalse(result.Changed, "A driven-only Canvas resolution change must not be reported as a scene change.");
        Assert.AreEqual(0, result.PatchEdits, "A driven-only Canvas resolution change must never produce a patch edit.");
        Assert.AreEqual(0, result.EditsApplied, "A driven-only Canvas resolution change must never apply a source edit.");

        var rewritten = File.ReadAllText(_builderPath);
        Assert.AreEqual(baseline, rewritten, "The builder file must stay byte-identical after a driven-only Canvas change.");
        Assert.AreEqual(2, CountOccurrences(rewritten, ".RectTransform("),
            "Only Panel and QuitButton may carry .RectTransform(...) — the Canvas must gain none.\n" + rewritten);
        StringAssert.DoesNotContain(".Transform(", rewritten,
            "Neither fixture node may gain a .Transform(pos:/scale:) call from a fully-driven Canvas change (D2).\n" + rewritten);

        AssertSyncIsNoOp(scene, "the driven Canvas resolution change");
    }

    [Test]
    public void LayoutGroupChildAddedThenRemoved_WritesNoLayoutDrivenValue_AndRestoresSource()
    {
        RectTransform rowRect = null;
        var scene = BuildAndSettle(FixtureB, s =>
        {
            rowRect = (RectTransform)Find(s, "Canvas/Row").transform;
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rowRect);
        });
        var baseline = File.ReadAllText(_builderPath);

        var itemA = (RectTransform)Find(scene, "Canvas/Row/ItemA").transform;
        Assert.IsNotNull(itemA.drivenByObject, "PREMISE: ItemA must be driven by the HorizontalLayoutGroup.");
        Assert.AreNotEqual(new Vector2(100f, 50f), itemA.sizeDelta,
            "PREMISE: the live, layout-driven sizeDelta must differ from ItemA's authored literal, or byte-identity below is vacuous.");
        Assert.AreNotEqual(Vector2.zero, itemA.anchoredPosition,
            "PREMISE: the live, layout-driven anchoredPosition must differ from the unauthored default.");

        var apBeforeAdd = itemA.anchoredPosition;
        var sdBeforeAdd = itemA.sizeDelta;

        var itemC = new GameObject("ItemC", typeof(RectTransform));
        itemC.transform.SetParent(rowRect, false);
        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(rowRect);

        Assert.AreNotEqual(apBeforeAdd, itemA.anchoredPosition, "PREMISE: adding ItemC must actually reflow ItemA's anchoredPosition.");
        Assert.AreNotEqual(sdBeforeAdd, itemA.sizeDelta, "PREMISE: adding ItemC must actually reflow ItemA's sizeDelta.");

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, scene);
        Assert.IsTrue(result.Changed, "Adding a real child under the Row must be reported as a scene change.");
        Assert.AreEqual(1, result.AddedEntries, "Exactly one new sidecar entry must be added for ItemC.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("row.Add(\"ItemC\").RectTransform();", rewritten,
            "ItemC must append a BARE .RectTransform() call — no layout-driven value may be written.\n" + rewritten);

        var baselineLines = baseline.Replace("\r\n", "\n").Split('\n');
        var rewrittenLines = rewritten.Replace("\r\n", "\n").Split('\n');
        foreach (var line in baselineLines)
        {
            Assert.Contains(line, rewrittenLines,
                $"Pre-existing line missing after the append (must be a pure insertion): {line}\n---\n{rewritten}");
        }
        Assert.AreEqual(baselineLines.Length + 1, rewrittenLines.Length, "Exactly one line must be added by the append.\n" + rewritten);
        StringAssert.DoesNotContain("66.6", rewritten, "No leaked layout-reflow number may be written to source.\n" + rewritten);
        StringAssert.DoesNotContain("133.3", rewritten, "No leaked layout-reflow number may be written to source.\n" + rewritten);

        AssertSyncIsNoOp(scene, "the ItemC append");

        UnityEngine.Object.DestroyImmediate(itemC);
        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(rowRect);

        var removeResult = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, scene);
        Assert.AreEqual(1, removeResult.RemovedEntries, "Exactly one sidecar entry must be removed for ItemC.");
        var restored = File.ReadAllText(_builderPath);
        Assert.AreEqual(baseline, restored, "Removing ItemC must restore the source byte-for-byte.");

        AssertSyncIsNoOp(scene, "the ItemC removal");
    }

    [Test]
    public void BuildSyncBuildSync_NoUserEdit_EmitsNoRectOps_AndNoSourceEdits()
    {
        var scene = BuildAndSettle(FixtureA);
        var sourceBaseline = File.ReadAllText(_builderPath);
        var sidecarBaseline = File.ReadAllText(_sidecarPath);

        StringAssert.Contains("m_RenderMode", sourceBaseline,
            "PREMISE: the fixture must still author a native enum field whose value DIFFERS from its " +
            "type's constructed default (m_RenderMode: authored 0, default 2), or this case stops " +
            "covering the read/write-asymmetry archetype it exists for.");
        StringAssert.Contains("m_PixelPerfect", sourceBaseline,
            "PREMISE: the fixture must still author a component field AT its type's constructed " +
            "default value (m_PixelPerfect: authored false, default false), or this case stops " +
            "covering the type-default-template archetype it exists for.");

        var panel = (RectTransform)Find(scene, "Canvas/Panel").transform;
        var button = (RectTransform)Find(scene, "Canvas/Panel/QuitButton").transform;
        var panelApBefore = panel.anchoredPosition;
        var panelSdBefore = panel.sizeDelta;
        var panelAnchorMinBefore = panel.anchorMin;
        var panelAnchorMaxBefore = panel.anchorMax;
        var panelPivotBefore = panel.pivot;
        var buttonApBefore = button.anchoredPosition;
        var buttonSdBefore = button.sizeDelta;
        var buttonAnchorMinBefore = button.anchorMin;
        var buttonAnchorMaxBefore = button.anchorMax;
        var buttonPivotBefore = button.pivot;

        var rebuild1 = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(rebuild1.Diagnostics, "Rebuild with no source change reported diagnostics");
        Assert.AreEqual(0, rebuild1.PlanOpCount,
            "A settled rebuild must be a fixed point: zero plan ops, including the native enum field " +
            "authored away from its type's default value (m_RenderMode) and the field authored AT its " +
            "type's default value (m_PixelPerfect); any rect/transform churn, or a regression of the " +
            "native-enum read or the default-field template, pushes this above zero.");

        AssertSyncIsNoOp(scene, "the first rebuild with no source change");
        Assert.AreEqual(sourceBaseline, File.ReadAllText(_builderPath), "Source must stay byte-identical across the cycle.");
        Assert.AreEqual(sidecarBaseline, File.ReadAllText(_sidecarPath), "Sidecar must stay byte-identical across the cycle.");

        var rebuild2 = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(rebuild2.Diagnostics, "Second rebuild with no source change reported diagnostics");
        Assert.AreEqual(0, rebuild2.PlanOpCount, "The rebuild's op count must be stable at zero, not growing, across repeated cycles.");

        AssertSyncIsNoOp(scene, "the second rebuild with no source change");

        var panelAfter = (RectTransform)Find(scene, "Canvas/Panel").transform;
        var buttonAfter = (RectTransform)Find(scene, "Canvas/Panel/QuitButton").transform;
        AssertVec2(panelApBefore, panelAfter.anchoredPosition, "Panel.anchoredPosition across the cycle");
        AssertVec2(panelSdBefore, panelAfter.sizeDelta, "Panel.sizeDelta across the cycle");
        AssertVec2(panelAnchorMinBefore, panelAfter.anchorMin, "Panel.anchorMin across the cycle");
        AssertVec2(panelAnchorMaxBefore, panelAfter.anchorMax, "Panel.anchorMax across the cycle");
        AssertVec2(panelPivotBefore, panelAfter.pivot, "Panel.pivot across the cycle");
        AssertVec2(buttonApBefore, buttonAfter.anchoredPosition, "QuitButton.anchoredPosition across the cycle");
        AssertVec2(buttonSdBefore, buttonAfter.sizeDelta, "QuitButton.sizeDelta across the cycle");
        AssertVec2(buttonAnchorMinBefore, buttonAfter.anchorMin, "QuitButton.anchorMin across the cycle");
        AssertVec2(buttonAnchorMaxBefore, buttonAfter.anchorMax, "QuitButton.anchorMax across the cycle");
        AssertVec2(buttonPivotBefore, buttonAfter.pivot, "QuitButton.pivot across the cycle");

        var canvasRectAfter = (RectTransform)Find(scene, "Canvas").transform;
        Assert.IsNotNull(canvasRectAfter.drivenByObject, "The Canvas must still be driven at the end of the cycle.");
    }

    [Test]
    public void MappedPlainTransformPromotedToUi_LogsLocatedNote_KeepsSameObject()
    {
        var scene = BuildAndSettle(FixtureC);

        var iconGo = Find(scene, "Canvas/Icon");
        Assert.IsNotNull(iconGo, "Setup: Icon was not created");
        Assert.IsNotInstanceOf<RectTransform>(iconGo.transform, "PREMISE: Icon must start as a plain Transform.");

        var entityIdBefore = iconGo.GetEntityId();
        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(iconGo).ToString();

        var sourceWithRect = File.ReadAllText(_builderPath).Replace(
            "var icon = canvas.Add(\"Icon\");",
            "var icon = canvas.Add(\"Icon\").RectTransform(anchoredPos: (5, 6), sizeDelta: (64, 64));");
        Assert.AreNotEqual(File.ReadAllText(_builderPath), sourceWithRect, "Setup: the Icon replacement did not match the fixture source.");
        File.WriteAllText(_builderPath, sourceWithRect);

        SceneBuilderBuild.BuildResult result = null;
        var messages = CaptureLogs(() =>
        {
            result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        });

        Assert.IsEmpty(result.Diagnostics, "Promoting a plain Transform to RectTransform via source reported diagnostics");
        Assert.IsTrue(messages.Any(m => m.Message.Contains("NOTE on 'icon'") && m.Message.Contains("promoted in place")),
            "A located NOTE naming the LogicalId 'icon' and 'promoted in place' must be logged.\nLogs:\n" +
            string.Join("\n", messages.Select(m => m.Message)));
        Assert.IsFalse(messages.Any(m => m.Message.Contains("Unhandled SetField")),
            "The promotion must not log an 'Unhandled SetField' warning.\nLogs:\n" + string.Join("\n", messages.Select(m => m.Message)));
        Assert.IsFalse(messages.Any(m => m.Type == LogType.Error),
            "The promotion must not log any error.\nLogs:\n" + string.Join("\n", messages.Select(m => m.Message)));

        var iconAfter = Find(scene, "Canvas/Icon");
        Assert.IsNotNull(iconAfter, "Icon disappeared after the in-place promotion");
        Assert.AreEqual(entityIdBefore, iconAfter.GetEntityId(), "Icon's EntityId must be unchanged by an in-place promotion (same object, no wipe).");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(iconAfter).ToString(), "Icon's GlobalObjectId must be unchanged by an in-place promotion.");
        Assert.IsInstanceOf<RectTransform>(iconAfter.transform, "Icon must be a RectTransform after the promotion.");

        var iconRect = (RectTransform)iconAfter.transform;
        AssertVec2(new Vector2(5, 6), iconRect.anchoredPosition, "Icon.anchoredPosition after promotion");
        AssertVec2(new Vector2(64, 64), iconRect.sizeDelta, "Icon.sizeDelta after promotion");

        AssertSyncIsNoOp(scene, "the promotion");
    }

    [Test]
    public void MappedPlainTransformPromotedByBareRectTransform_KeepsSameObject_LandsDefaultTable()
    {
        // A BARE `.RectTransform()` call — every field at RectTransformFields.Default* — must still
        // promote a mapped plain-Transform object in place. A value-gated diff would compare the
        // plain snapshot's implicit defaults equal to the model's defaults and emit nothing, silently
        // dropping the authored Kind change forever.
        var scene = BuildAndSettle(FixtureC);

        var iconGo = Find(scene, "Canvas/Icon");
        Assert.IsNotNull(iconGo, "Setup: Icon was not created");
        Assert.IsNotInstanceOf<RectTransform>(iconGo.transform, "PREMISE: Icon must start as a plain Transform.");

        var entityIdBefore = iconGo.GetEntityId();
        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(iconGo).ToString();

        var baselineResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(baselineResult.Diagnostics, "Baseline rebuild reported diagnostics");
        var baselineOpCount = baselineResult.PlanOpCount;

        var sourceWithRect = File.ReadAllText(_builderPath).Replace(
            "var icon = canvas.Add(\"Icon\");",
            "var icon = canvas.Add(\"Icon\").RectTransform();");
        Assert.AreNotEqual(File.ReadAllText(_builderPath), sourceWithRect, "Setup: the bare RectTransform() replacement did not match the fixture source.");
        File.WriteAllText(_builderPath, sourceWithRect);

        SceneBuilderBuild.BuildResult result = null;
        var messages = CaptureLogs(() =>
        {
            result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        });

        Assert.IsEmpty(result.Diagnostics, "Promoting a plain Transform via a bare RectTransform() reported diagnostics");
        Assert.Greater(result.PlanOpCount, baselineOpCount,
            "The bare RectTransform() edit must emit rect ops against the plan — the Kind change, not a value difference, is what the diff keys on.");
        Assert.IsTrue(messages.Any(m => m.Message.Contains("NOTE on 'icon'") && m.Message.Contains("promoted in place")),
            "A located NOTE naming the LogicalId 'icon' and 'promoted in place' must be logged.\nLogs:\n" +
            string.Join("\n", messages.Select(m => m.Message)));
        Assert.IsFalse(messages.Any(m => m.Message.Contains("Unhandled SetField")),
            "The promotion must not log an 'Unhandled SetField' warning.\nLogs:\n" + string.Join("\n", messages.Select(m => m.Message)));
        Assert.IsFalse(messages.Any(m => m.Type == LogType.Error),
            "The promotion must not log any error.\nLogs:\n" + string.Join("\n", messages.Select(m => m.Message)));

        var iconAfter = Find(scene, "Canvas/Icon");
        Assert.IsNotNull(iconAfter, "Icon disappeared after the in-place promotion");
        Assert.AreEqual(entityIdBefore, iconAfter.GetEntityId(), "Icon's EntityId must be unchanged by an in-place promotion (same object, no wipe).");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(iconAfter).ToString(), "Icon's GlobalObjectId must be unchanged by an in-place promotion.");
        Assert.IsInstanceOf<RectTransform>(iconAfter.transform, "Icon must be a RectTransform after the promotion.");

        var iconRect = (RectTransform)iconAfter.transform;
        AssertVec2(new Vector2(RectTransformFields.DefaultAnchoredPosition.X, RectTransformFields.DefaultAnchoredPosition.Y), iconRect.anchoredPosition, "Icon.anchoredPosition after bare promotion");
        AssertVec2(new Vector2(RectTransformFields.DefaultSizeDelta.X, RectTransformFields.DefaultSizeDelta.Y), iconRect.sizeDelta, "Icon.sizeDelta after bare promotion");
        AssertVec2(new Vector2(RectTransformFields.DefaultAnchorMin.X, RectTransformFields.DefaultAnchorMin.Y), iconRect.anchorMin, "Icon.anchorMin after bare promotion");
        AssertVec2(new Vector2(RectTransformFields.DefaultAnchorMax.X, RectTransformFields.DefaultAnchorMax.Y), iconRect.anchorMax, "Icon.anchorMax after bare promotion");
        AssertVec2(new Vector2(RectTransformFields.DefaultPivot.X, RectTransformFields.DefaultPivot.Y), iconRect.pivot, "Icon.pivot after bare promotion");

        var convergedResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.AreEqual(baselineOpCount, convergedResult.PlanOpCount,
            "A further Build must not keep re-writing the five fields every cycle (converged fixed point).");

        AssertSyncIsNoOp(scene, "the bare-argument promotion");
    }

    [Test]
    public void DrivenCanvasScaleFactorChange_RebuildEmitsNoExtraOps_KeepsDrivenScale()
    {
        // A CanvasScaler-driven scale factor is a driver the plugin does not own and cannot
        // re-baseline (spec 23's re-baseline rationale holds for FitSize/AlignTo, which
        // PlanExecutor.cs:393-405 explicitly re-baselines on write; it does not hold for a
        // Canvas/CanvasScaler). A Position/Scale axis driven by something the model does not author
        // is held to the live value because nothing re-baselines a driver the plugin does not own, so
        // a scale-factor change adds no plan op against an unchanged builder — satisfying checklist
        // item 7's "re-Build with no source change is a no-op".
        var scene = BuildAndSettle(FixtureA);
        var sourceBaseline = File.ReadAllText(_builderPath);

        var baseline = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(baseline.Diagnostics, "Baseline rebuild reported diagnostics");
        var baselineOpCount = baseline.PlanOpCount;

        var canvasGo = Find(scene, "Canvas");
        var canvasRect = (RectTransform)canvasGo.transform;
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.scaleFactor = 2f;
        Canvas.ForceUpdateCanvases();

        Assert.IsNotNull(canvasRect.drivenByObject, "PREMISE: the Canvas must still be driven going into the scale-factor change.");
        Assert.AreNotEqual(Vector3.one, canvasRect.localScale,
            "PREMISE: the scale-factor change must actually move the Canvas's driven localScale off (1,1,1), or this case is vacuous.");

        SceneBuilderBuild.BuildResult result = null;
        var messages = CaptureLogs(() =>
        {
            result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        });

        Assert.IsEmpty(result.Diagnostics, "Rebuild after a driven scale-factor change reported diagnostics");
        Assert.AreEqual(baselineOpCount, result.PlanOpCount,
            "A rebuild after a CanvasScaler-driven scale-factor change must not grow the plan.");
        Assert.IsFalse(messages.Any(m => m.Type == LogType.Error),
            "The rebuild must not log any error.\nLogs:\n" + string.Join("\n", messages.Select(m => m.Message)));
        Assert.IsFalse(messages.Any(m => m.Message.Contains("Unhandled SetField")),
            "The rebuild must not log an 'Unhandled SetField' warning.\nLogs:\n" + string.Join("\n", messages.Select(m => m.Message)));

        Assert.AreNotEqual(Vector3.one, canvasRect.localScale,
            "The Canvas's driven scale must not be clobbered back to (1,1,1) by the rebuild.");

        var secondResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.AreEqual(baselineOpCount, secondResult.PlanOpCount, "The op count must be stable, not merely small, across repeated rebuilds.");

        var panel = (RectTransform)Find(scene, "Canvas/Panel").transform;
        var button = (RectTransform)Find(scene, "Canvas/Panel/QuitButton").transform;
        AssertVec2(new Vector2(-10, -10), panel.anchoredPosition, "Panel.anchoredPosition after the Canvas scale-factor change");
        AssertVec2(new Vector2(200, 120), panel.sizeDelta, "Panel.sizeDelta after the Canvas scale-factor change");
        AssertVec2(new Vector2(0, 0), button.anchoredPosition, "QuitButton.anchoredPosition after the Canvas scale-factor change");
        AssertVec2(new Vector2(160, 40), button.sizeDelta, "QuitButton.sizeDelta after the Canvas scale-factor change");

        Assert.AreEqual(sourceBaseline, File.ReadAllText(_builderPath),
            "The builder file must stay byte-identical after a driven-only Canvas scale-factor change.");

        AssertSyncIsNoOp(scene, "the Canvas scale-factor change");
    }
}
