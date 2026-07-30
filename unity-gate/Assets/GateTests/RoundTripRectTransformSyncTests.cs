using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Editor;

// m-ui-recttransform b3-t5 (specs/13-recttransform.md): scene->code EditMode round-trip. Drives the
// REAL Sync path (EmittedCodeCompiles.SyncAndAssertCompiles, per that file's mandatory-compile
// contract) against live RectTransforms. Covers Unity confirmation checklist items 3 (drag), 4
// (resize), 5 (anchor/pivot change), 8 (created UI child appends a RectTransform Add(...)).
//
// Fixture note: the fixture is the spec's HudScene VERBATIM
// (specs/13-recttransform.md:191-206), including QuitButton's chained `.Component<Image>(_ => {
// })`. Unity's `RequireComponent` adds CanvasRenderer to a live Image, so the FIRST Sync after
// Build harvests it as a separate `quitButton.Component<UnityEngine.CanvasRenderer>();` statement —
// the settled fixed point is reached only on the SECOND Sync. `BuildAndSettle()` performs and
// asserts that harvest so every test below starts from a genuine no-further-edits baseline.
public class RoundTripRectTransformSyncTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripRectTransformSyncTemp.unity";
    private const float Tol = 1e-4f;

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    // Verbatim from specs/13-recttransform.md:191-206. The Panel's call is split across two lines
    // exactly as in the spec so the anchoredPos/sizeDelta patch and the anchorMin/anchorMax/pivot
    // patch land on DIFFERENT lines, letting a per-line diff localise each checklist item
    // independently. QuitButton's `.RectTransform(...)` is kept on its own line for the same reason.
    private const string BuilderSource = @"
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

        var panel = canvas.Add(""Panel"").RectTransform(
            anchoredPos: (-10, -10), sizeDelta: (200, 120),
            anchorMin: (1, 1), anchorMax: (1, 1), pivot: (1, 1));

        panel.Add(""QuitButton"")
             .RectTransform(anchoredPos: (0, 0), sizeDelta: (160, 40), pivot: (0.5f, 0.5f))
             .Component<Image>(_ => { });
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

    // Returns the (1-based line number, before-text, after-text) triples for every line that
    // differs between two texts, split on '\n'. Used to assert per-line diffs BY CONTENT rather
    // than by a hand-counted index.
    private static List<(int Line, string Before, string After)> ChangedLines(string before, string after)
    {
        var b = before.Replace("\r\n", "\n").Split('\n');
        var a = after.Replace("\r\n", "\n").Split('\n');
        var changed = new List<(int, string, string)>();
        var max = Math.Max(b.Length, a.Length);
        for (int i = 0; i < max; i++)
        {
            var bl = i < b.Length ? b[i] : "<missing>";
            var al = i < a.Length ? a[i] : "<missing>";
            if (bl != al)
            {
                changed.Add((i + 1, bl, al));
            }
        }
        return changed;
    }

    private static string DiffMessage(List<(int Line, string Before, string After)> diff) =>
        string.Join("\n", diff.Select(d => $"  line {d.Line}:\n    - {d.Before}\n    + {d.After}"));

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtrts_" + Guid.NewGuid().ToString("N"));
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

    // Builds the fixture into a fresh scene, makes the Canvas genuinely driven (a Build-created
    // Canvas is NOT driven in batchmode until toggled), Syncs ONCE to harvest Unity's
    // RequireComponent CanvasRenderer onto QuitButton, and returns the scene from that SETTLED
    // baseline — a second Sync from here must be a clean fixed point.
    private Scene BuildAndSettle()
    {
        File.WriteAllText(_builderPath, BuilderSource);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for the HudScene fixture: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var canvasGo = Find(scene, "Canvas");
        Assert.IsNotNull(canvasGo, "Setup: Canvas was not created");
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.enabled = false;
        canvas.enabled = true;
        Canvas.ForceUpdateCanvases();
        var canvasRect = (RectTransform)canvasGo.transform;
        Assert.IsNotNull(canvasRect.drivenByObject,
            "PREMISE: a Build-created Canvas must be driven after the " +
            "enabled-toggle + ForceUpdateCanvases recipe, or the baseline-no-op assumption below is " +
            "unearned and every 'only this line changed' assertion in this file is meaningless.");

        // Sync #1: harvests Unity's RequireComponent CanvasRenderer onto QuitButton — the ONLY
        // edit a freshly-Built HudScene should ever produce.
        var first = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, scene);
        Assert.AreEqual(1, first.PatchEdits,
            "PREMISE: the first Sync after Build must produce exactly the RequireComponent " +
            "CanvasRenderer harvest and nothing else.");
        StringAssert.Contains("quitButton.Component<UnityEngine.CanvasRenderer>();", File.ReadAllText(_builderPath),
            "Unity's RequireComponent must have harvested CanvasRenderer onto QuitButton after Sync #1.");

        // Sync #2: from here on, nothing changed — this is the genuine fixed point every
        // per-test baseline below is captured AFTER.
        AssertSecondSyncIsNoOp(scene, "the post-harvest baseline");

        return scene;
    }

    // Folds the second-Sync-is-a-no-op triple repeated by every test below into one place.
    private void AssertSecondSyncIsNoOp(Scene scene, string what)
    {
        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, scene);
        Assert.IsFalse(second.Changed, $"NOT CONVERGED: a Sync after {what}, with no further edit, reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits, $"NOT CONVERGED: a Sync after {what} produced non-zero PatchEdits.");
        Assert.AreEqual(0, second.EditsApplied, $"NOT CONVERGED: a Sync after {what} applied non-zero edits.");
    }

    [Test]
    public void BuildThenSettle_SecondSyncIsFixedPoint_AndCanvasIsDriven()
    {
        var scene = BuildAndSettle();

        // BuildAndSettle already proved the fixed point once; this is an INDEPENDENT confirmation
        // that the settled baseline stays a no-op on yet another Sync.
        AssertSecondSyncIsNoOp(scene, "an already-settled baseline");
    }

    [Test]
    public void SceneToCode_DraggedPanel_PatchesOnlyAnchoredPosArgument_FSuffixed()
    {
        var scene = BuildAndSettle();
        var baseline = File.ReadAllText(_builderPath);
        var baselineSidecar = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var panelEntryBefore = baselineSidecar.Entries.Single(e => e.Kind == "GameObject" && e.Name == "Panel");

        var panel = (RectTransform)Find(scene, "Canvas/Panel").transform;
        panel.anchoredPosition = new Vector2(-40f, -30f);

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, scene);
        Assert.IsTrue(result.Changed, "Moving the Panel's anchoredPosition must be reported as a scene change.");

        var rewritten = File.ReadAllText(_builderPath);
        var diff = ChangedLines(baseline, rewritten);
        Assert.AreEqual(1, diff.Count,
            "Dragging the Panel must patch exactly one line (its anchoredPos/sizeDelta line).\n" + DiffMessage(diff));
        StringAssert.Contains("anchoredPos: (-40f, -30f), sizeDelta: (200, 120),", diff[0].After,
            "The patched line must rewrite ONLY anchoredPos (f-suffixed) and leave the authored, " +
            "non-f-suffixed sizeDelta literal byte-identical.\n" + DiffMessage(diff));
        StringAssert.DoesNotContain(".Transform(pos:", rewritten,
            "A RectTransform node's anchoredPos edit must never emit a .Transform(pos:) call (D1).\n" + rewritten);

        var sidecarAfter = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var panelEntriesAfter = sidecarAfter.Entries.Where(e => e.Kind == "GameObject" && e.Name == "Panel").ToArray();
        Assert.AreEqual(1, panelEntriesAfter.Length, "Exactly one Panel sidecar entry must survive the patch (no duplicate).");
        Assert.AreEqual(panelEntryBefore.GlobalObjectId, panelEntriesAfter[0].GlobalObjectId,
            "The Panel's GlobalObjectId must be unchanged by an in-place argument patch.");
        Assert.AreEqual(baselineSidecar.Entries.Length, sidecarAfter.Entries.Length,
            "A pure argument patch must not add or remove any sidecar entries.");

        AssertSecondSyncIsNoOp(scene, "the dragged-Panel patch");
    }

    [Test]
    public void SceneToCode_ResizedButton_PatchesOnlySizeDeltaArgument()
    {
        var scene = BuildAndSettle();
        var baseline = File.ReadAllText(_builderPath);

        var button = (RectTransform)Find(scene, "Canvas/Panel/QuitButton").transform;
        button.sizeDelta = new Vector2(240f, 64f);

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, scene);
        Assert.IsTrue(result.Changed, "Resizing the QuitButton must be reported as a scene change.");

        var rewritten = File.ReadAllText(_builderPath);
        var diff = ChangedLines(baseline, rewritten);
        Assert.AreEqual(1, diff.Count,
            "Resizing the QuitButton must patch exactly one line (its RectTransform call).\n" + DiffMessage(diff));
        StringAssert.Contains("anchoredPos: (0, 0), sizeDelta: (240f, 64f), pivot: (0.5f, 0.5f)", diff[0].After,
            "Only sizeDelta may change; the QuitButton's anchoredPos and pivot arguments must survive byte-identical.\n" + DiffMessage(diff));
        StringAssert.Contains("anchoredPos: (-10, -10), sizeDelta: (200, 120),", rewritten,
            "The Panel's own sizeDelta argument must be untouched by an edit to a different node.\n" + rewritten);

        AssertSecondSyncIsNoOp(scene, "the resized-Button patch");
    }

    [Test]
    public void SceneToCode_AnchorPresetAndPivotChanged_PatchesAnchorAndPivotArgumentsOnly()
    {
        var scene = BuildAndSettle();
        var baseline = File.ReadAllText(_builderPath);

        var panel = (RectTransform)Find(scene, "Canvas/Panel").transform;
        var apBefore = panel.anchoredPosition;
        var sdBefore = panel.sizeDelta;

        panel.anchorMin = Vector2.zero;
        panel.anchorMax = Vector2.zero;
        panel.pivot = Vector2.zero;

        // Setting anchorMin/anchorMax/pivot through script properties
        // moves the DERIVED localPosition, never the STORED anchoredPosition/sizeDelta. Assert what
        // actually happened on the live object before asserting the source text — do not emulate the
        // Inspector's rect-preserving SetAnchorSmart.
        AssertVec2(apBefore, panel.anchoredPosition, "Panel.anchoredPosition must be unchanged by an anchor/pivot-only edit");
        AssertVec2(sdBefore, panel.sizeDelta, "Panel.sizeDelta must be unchanged by an anchor/pivot-only edit");

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, scene);
        Assert.IsTrue(result.Changed, "Changing the Panel's anchors and pivot must be reported as a scene change.");

        var rewritten = File.ReadAllText(_builderPath);
        var diff = ChangedLines(baseline, rewritten);
        Assert.AreEqual(1, diff.Count,
            "Changing anchors+pivot must patch exactly one line (the anchorMin/anchorMax/pivot line).\n" + DiffMessage(diff));
        StringAssert.Contains("anchorMin: (0f, 0f), anchorMax: (0f, 0f), pivot: (0f, 0f));", diff[0].After,
            DiffMessage(diff));
        StringAssert.Contains("anchoredPos: (-10, -10), sizeDelta: (200, 120),", rewritten,
            "The Panel's anchoredPos/sizeDelta line must be untouched by an anchor/pivot-only edit.\n" + rewritten);

        AssertSecondSyncIsNoOp(scene, "the anchor/pivot patch");
    }

    [Test]
    public void SceneToCode_CreatedUiChildUnderCanvas_AppendsAddWithRectTransform_SecondSyncNoOp()
    {
        var scene = BuildAndSettle();
        var baseline = File.ReadAllText(_builderPath);
        var sidecarBefore = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var canvasEntry = sidecarBefore.Entries.Single(e => e.Kind == "GameObject" && e.Name == "Canvas");

        var canvasGo = Find(scene, "Canvas");
        var badge = new GameObject("Badge", typeof(RectTransform));
        badge.transform.SetParent(canvasGo.transform, false);
        var badgeRect = (RectTransform)badge.transform;
        badgeRect.sizeDelta = new Vector2(64f, 24f);
        badgeRect.anchoredPosition = new Vector2(12f, -8f);

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, scene);
        Assert.IsTrue(result.Changed, "A newly created UI child must be reported as a scene change.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("canvas.Add(\"Badge\").RectTransform(anchoredPos: (12f, -8f), sizeDelta: (64f, 24f));", rewritten,
            "The created Badge must append a .RectTransform(...) Add() call under the canvas handle, " +
            "with anchorMin/anchorMax/pivot OMITTED because they equal the RectTransform default (0.5, 0.5).\n" + rewritten);

        // Pure insertion: every pre-existing baseline line must still be present verbatim.
        var baselineLines = baseline.Replace("\r\n", "\n").Split('\n');
        var rewrittenLines = rewritten.Replace("\r\n", "\n").Split('\n');
        foreach (var line in baselineLines)
        {
            Assert.Contains(line, rewrittenLines,
                $"Pre-existing line missing after the append (must be a pure insertion): {line}\n---\n{rewritten}");
        }

        var sidecarAfter = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        Assert.AreEqual(sidecarBefore.Entries.Length + 1, sidecarAfter.Entries.Length,
            "Exactly one new sidecar entry must be added for the created Badge.");
        var badgeEntry = sidecarAfter.Entries.SingleOrDefault(e => e.Kind == "GameObject" && e.Name == "Badge");
        Assert.IsNotNull(badgeEntry, "Sidecar has no new GameObject entry for Badge.\n" + File.ReadAllText(_sidecarPath));
        Assert.IsFalse(string.IsNullOrEmpty(badgeEntry.GlobalObjectId), "Badge's sidecar entry has an empty GlobalObjectId");
        Assert.AreEqual(canvasEntry.LogicalId, badgeEntry.ParentLogicalId, "Badge's ParentLogicalId must be the Canvas entry's LogicalId");

        AssertSecondSyncIsNoOp(scene, "the created-UI-child append");
    }
}
