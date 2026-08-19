using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// The auto-sync 3-way conflict merge (SceneBuilderSync.RunConflictAware / KeyOfSourceEdit /
// DiffDesiredFields / KeysOfChangeOp) attributes the five RectTransform authoring arguments as
// field-level keys. These cases drive the REAL merge over a live RectTransform (AutoConflictTests'
// two-phase build + ExecuteBothChanged) and lock:
//   - a rect field is attributable on both sides, so a code-only rect edit is never reverted by an
//     unattributable scene edit;
//   - the base transform and the rect fields are diffed separately, so a rect-only code edit never
//     claims transform.pos/rot/scale.
public class AutoConflictRectTransformTests
{
    private const string BuilderName = "AutoConflictRectScene";
    private const string ScenePath = "Assets/GateTests/__AutoConflictRectTemp.unity";
    private const float Tol = 1e-4f;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class AutoConflictRectScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    private static void AssertVec2(Vector2 expected, Vector2 actual, string what)
    {
        Assert.AreEqual(expected.x, actual.x, Tol, $"{what}.x: expected {expected} got {actual}");
        Assert.AreEqual(expected.y, actual.y, Tol, $"{what}.y: expected {expected} got {actual}");
    }

    [SetUp]
    public void SetUp()
    {
        LicenseGate.ResetToDefault();
        SceneBuilderPaths.EnsureBuildersDirectory();
        _builderPath = SceneBuilderPaths.Builder(BuilderName);
        _sidecarPath = SceneBuilderPaths.Sidecar(BuilderName);

        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        LicenseEnforcement.Register();
        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();

        if (File.Exists(_builderPath)) File.Delete(_builderPath);
        if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    /// <summary>
    /// A minimal fixture: a root Panel authored ONLY with <c>.RectTransform(...)</c> (no Canvas, no
    /// <c>Component&lt;Image&gt;</c>, so no RequireComponent harvest churn) plus a second root Beta
    /// carrying a Rigidbody, two-phase per AutoConflictTests' BuildTwoMassObjects so Beta's mass
    /// field targets an already-durable, mapped component.
    /// </summary>
    private Scene BuildPanelAndBeta()
    {
        const string panelStatement =
            "        scene.Add(\"Panel\").RectTransform(\n" +
            "            anchoredPos: (0f, 0f), sizeDelta: (200f, 120f),\n" +
            "            anchorMin: (0.5f, 0.5f), anchorMax: (0.5f, 0.5f), pivot: (0.5f, 0.5f));\n";

        File.WriteAllText(_builderPath, Source(
            panelStatement +
            "        scene.Add(\"Beta\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        File.WriteAllText(_builderPath, Source(
            panelStatement +
            "        var beta = scene.Add(\"Beta\");\n" +
            "        beta.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 1f));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        SceneBuilderRouter.ResetForTests();

        return EditorSceneManager.GetActiveScene();
    }

    // A scene-side edit to an unrelated object must not revert a code-only rect edit: the rect
    // PatchArgument is attributable, so the merge sees code as the sole authority for that field.
    [Test]
    public void Conflict_RectFieldChangedInCodeOnly_SurvivesCycle_AndMaterializesToLiveRect()
    {
        var scene = BuildPanelAndBeta();
        var panel = FindRoot(scene, "Panel");
        var beta = FindRoot(scene, "Beta");
        Assert.IsNotNull(panel, "Panel was not created.");
        Assert.IsNotNull(beta, "Beta was not created.");

        SceneBuilderAutoSync.CaptureBaseline(scene);

        // Scene-side change: Beta's mass, live (unrelated object/field).
        beta.GetComponent<Rigidbody>().mass = 9f;

        // Code-side change: Panel's anchoredPos, external edit — Beta's statement untouched.
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Panel\").RectTransform(\n" +
            "            anchoredPos: (25f, 40f), sizeDelta: (200f, 120f),\n" +
            "            anchorMin: (0.5f, 0.5f), anchorMax: (0.5f, 0.5f), pivot: (0.5f, 0.5f));\n" +
            "        var beta = scene.Add(\"Beta\");\n" +
            "        beta.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 1f));"));

        SceneBuilderAutoSync.ExecuteBothChanged(
            new[] { beta.GetEntityId() },
            new[] { _builderPath });

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("// CONFLICT:", rewritten,
            "A rect-only code edit on Panel and a mass-only scene edit on Beta are non-overlapping " +
            "and must never raise a conflict marker.\n" + rewritten);
        StringAssert.Contains("25f", rewritten,
            "Panel's code-changed anchoredPos.x must survive the cycle (it is the sole authority for " +
            "a field the scene never touched), not be reverted to the live scene's stale value.\n" + rewritten);
        StringAssert.Contains("40f", rewritten,
            "Panel's code-changed anchoredPos.y must survive the cycle.\n" + rewritten);

        var panelAfter = FindRoot(EditorSceneManager.GetActiveScene(), "Panel");
        AssertVec2(new Vector2(25f, 40f), panelAfter.GetComponent<RectTransform>().anchoredPosition,
            "Panel.anchoredPosition after the surviving code edit materializes");
    }

    // Same-field-same-object overlap resolves scene-wins, with the prior CODE value preserved as a
    // SourceExpr.Vec2Literal in an inline `// CONFLICT:` marker plus one located Console error —
    // the code value stays recoverable.
    [Test]
    public void Conflict_RectSameFieldBothSides_SceneWins_PreservesCodeVec2InMarker_LocatesError()
    {
        var scene = BuildPanelAndBeta();
        var panel = FindRoot(scene, "Panel");
        Assert.IsNotNull(panel, "Panel was not created.");

        SceneBuilderAutoSync.CaptureBaseline(scene);

        // Scene-side change: Panel's anchoredPosition, live.
        panel.GetComponent<RectTransform>().anchoredPosition = new Vector2(60f, 70f);

        // Code-side change to the SAME field of the SAME object: Panel's anchoredPos, external edit.
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Panel\").RectTransform(\n" +
            "            anchoredPos: (25f, 40f), sizeDelta: (200f, 120f),\n" +
            "            anchorMin: (0.5f, 0.5f), anchorMax: (0.5f, 0.5f), pivot: (0.5f, 0.5f));\n" +
            "        var beta = scene.Add(\"Beta\");\n" +
            "        beta.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 1f));"));

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Panel.*anchoredPos|anchoredPos.*Panel"));

        SceneBuilderAutoSync.ExecuteBothChanged(
            new[] { panel.GetEntityId() },
            new[] { _builderPath });

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("// CONFLICT:", rewritten,
            "A true same-field-same-object rect overlap must leave a `// CONFLICT:` marker in the source.\n" + rewritten);
        StringAssert.Contains("25f", rewritten,
            "The prior CODE anchoredPos.x must be preserved (recoverable) in the marker, never silently discarded.\n" + rewritten);
        StringAssert.Contains("40f", rewritten,
            "The prior CODE anchoredPos.y must be preserved (recoverable) in the marker.\n" + rewritten);
        StringAssert.Contains("60f", rewritten,
            "The conflicting argument's live value must be the SCENE value (scene-wins tie-break).\n" + rewritten);
        StringAssert.Contains("70f", rewritten,
            "The conflicting argument's live value must be the SCENE value (scene-wins tie-break).\n" + rewritten);

        var panelAfter = FindRoot(EditorSceneManager.GetActiveScene(), "Panel");
        AssertVec2(new Vector2(60f, 70f), panelAfter.GetComponent<RectTransform>().anchoredPosition,
            "Scene-wins: the live rect must keep the scene value on the conflicting field, not revert to the code value.");
    }

    // A rect-only code edit and a base-transform-only scene edit on the SAME node touch different
    // field keys: SetRectTransform claims only its changed rect fields, so no overlap and no marker.
    [Test]
    public void Conflict_RectOnlyCodeEdit_DoesNotClaimBaseTransformKeys_NoSpuriousConflict()
    {
        var scene = BuildPanelAndBeta();
        var panel = FindRoot(scene, "Panel");
        Assert.IsNotNull(panel, "Panel was not created.");

        SceneBuilderAutoSync.CaptureBaseline(scene);

        // Scene-side change: Panel's rotation, live — a DIFFERENT field on the SAME object.
        panel.transform.localEulerAngles = new Vector3(0f, 0f, 15f);

        // Code-side change: Panel's sizeDelta ONLY, external edit — no `.Transform(rot:...)` call.
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Panel\").RectTransform(\n" +
            "            anchoredPos: (0f, 0f), sizeDelta: (280f, 140f),\n" +
            "            anchorMin: (0.5f, 0.5f), anchorMax: (0.5f, 0.5f), pivot: (0.5f, 0.5f));\n" +
            "        var beta = scene.Add(\"Beta\");\n" +
            "        beta.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 1f));"));

        SceneBuilderAutoSync.ExecuteBothChanged(
            new[] { panel.GetEntityId() },
            new[] { _builderPath });

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("// CONFLICT:", rewritten,
            "A rect-only code edit (sizeDelta) and a base-transform-only scene edit (rotation) on the " +
            "SAME node touch DIFFERENT fields and must never be claimed as an overlap.\n" + rewritten);
        StringAssert.Contains("280f", rewritten,
            "Panel's code-changed sizeDelta.x must survive (non-overlapping, code is sole authority).\n" + rewritten);
        StringAssert.Contains("140f", rewritten,
            "Panel's code-changed sizeDelta.y must survive.\n" + rewritten);
        StringAssert.Contains("15f", rewritten,
            "Panel's scene-changed rotation must be patched into the source (non-overlapping, scene is " +
            "sole authority for the field it touched).\n" + rewritten);
        // No LogAssert.Expect: an unexpected Console error here fails the test — the point of this
        // case is that NO conflict, and therefore no located error, is raised for non-overlapping fields.

        var panelAfter = FindRoot(EditorSceneManager.GetActiveScene(), "Panel");
        AssertVec2(new Vector2(280f, 140f), panelAfter.GetComponent<RectTransform>().sizeDelta,
            "Panel.sizeDelta after the surviving code edit materializes");
    }
}
