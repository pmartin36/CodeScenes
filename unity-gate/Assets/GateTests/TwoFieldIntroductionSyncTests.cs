using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// Two previously-unauthored fields set on ONE component in ONE editor action must both reach the
// builder source in a SINGLE scene->code sync.
//
// Live-editor repro this pins (invisible to every offline test, because both edits target the same
// `.Component<T>(...)` call and only the REAL sync batches them together):
//   * expression-bodied closure `c => c.Set("m_Radius", 2f)` — the second introduction threw
//     NullReferenceException out of SourcePatchApplier.Apply, so ZERO edits were written and every
//     later sync threw again on the same pair (auto-sync permanently wedged);
//   * zero-argument `.Component<T>()` — the second introduction overwrote the first's argument list,
//     so the sync reported "Synced 2 edit(s)" while only one field landed.
public class TwoFieldIntroductionSyncTests
{
    private const string ScenePath = "Assets/GateTests/__TwoFieldIntroductionTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class TwoFieldIntroductionScene : ISceneDefinition
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
        _dir = Path.Combine(Path.GetTempPath(), "sb_tfi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "TwoFieldIntroductionScene.cs");
        _sidecarPath = Path.Combine(_dir, "TwoFieldIntroductionScene.sbmap.json");
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

    // Builds "NreProbe" alone (so it is mapped), then authors the component statement given by
    // `componentStatement` onto it and rebuilds (so the component is mapped too). Returns the live
    // SphereCollider.
    private SphereCollider BuildProbeWith(string componentStatement)
    {
        File.WriteAllText(_builderPath, Source("        var probe = scene.Add(\"NreProbe\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        File.WriteAllText(_builderPath, Source(
            "        var probe = scene.Add(\"NreProbe\");\n" +
            "        " + componentStatement));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var probe = FindRoot(EditorSceneManager.GetActiveScene(), "NreProbe");
        Assert.IsNotNull(probe, "NreProbe was not created by SceneBuilderBuild.Run");
        var collider = probe.GetComponent<SphereCollider>();
        Assert.IsNotNull(collider, "Authored SphereCollider was not materialized on NreProbe");
        return collider;
    }

    // Sets m_IsTrigger AND m_Center in one go — the two-at-once edit that wedged the live sync.
    private static void SetTwoNewFields(SphereCollider collider)
    {
        collider.isTrigger = true;
        collider.center = new Vector3(1f, 2f, 3f);
    }

    private static void AssertBothFieldsLanded(string rewritten)
    {
        StringAssert.Contains(".Set(\"m_IsTrigger\", true)", rewritten,
            "The first introduced field (m_IsTrigger) is missing from the synced source.\n" + rewritten);
        StringAssert.Contains("\"m_Center\"", rewritten,
            "The second introduced field (m_Center) is missing from the synced source — an edit was dropped.\n" + rewritten);
        StringAssert.Contains("1f, 2f, 3f", rewritten,
            "The introduced m_Center did not carry the value set in the scene.\n" + rewritten);
    }

    // 1. The reported crash: an EXPRESSION-BODIED closure taking two introductions in one sync.
    [Test]
    public void SceneToCode_TwoNewFieldsOnExpressionBodiedClosure_BothLandInOnePass()
    {
        var collider = BuildProbeWith("probe.Component<UnityEngine.SphereCollider>(c => c.Set(\"m_Radius\", 2f));");
        SetTwoNewFields(collider);

        SceneBuilderSync.SyncResult result = null;
        Assert.DoesNotThrow(
            () => result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()),
            "Sync threw while introducing two fields into an expression-bodied component closure.");
        Assert.IsTrue(result.Changed, "Sync reported no change despite two newly-set component fields");
        Assert.AreEqual(2, result.PatchEdits, "Expected exactly two field introductions in the batch.");
        Assert.AreEqual(2, result.EditsApplied, "The two field introductions were not all applied.");

        var rewritten = File.ReadAllText(_builderPath);
        AssertBothFieldsLanded(rewritten);
        StringAssert.Contains(".Set(\"m_Radius\", 2f)", rewritten,
            "The pre-existing authored field was lost by the closure rewrite.\n" + rewritten);

        // Both landed in the FIRST pass, so a second sync has nothing left to harvest.
        var secondSync = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(secondSync.Changed, "A second sync with no further edit changed the source (an edit landed a pass late).");
        Assert.AreEqual(0, secondSync.PatchEdits, "A second sync with no further edit still had edits to make.");
    }

    // 2. The soft-failure twin: the ZERO-ARGUMENT `.Component<T>()` form, where the second
    //    introduction used to overwrite the first and vanish without a word.
    [Test]
    public void SceneToCode_TwoNewFieldsOnZeroArgComponentCall_BothLandInOnePass()
    {
        var collider = BuildProbeWith("probe.Component<UnityEngine.SphereCollider>();");
        SetTwoNewFields(collider);

        SceneBuilderSync.SyncResult result = null;
        Assert.DoesNotThrow(
            () => result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()),
            "Sync threw while introducing two fields into a zero-argument component call.");
        Assert.IsTrue(result.Changed, "Sync reported no change despite two newly-set component fields");
        Assert.AreEqual(2, result.PatchEdits, "Expected exactly two field introductions in the batch.");
        Assert.AreEqual(2, result.EditsApplied, "The two field introductions were not all applied.");

        var rewritten = File.ReadAllText(_builderPath);
        AssertBothFieldsLanded(rewritten);

        var secondSync = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(secondSync.Changed, "A second sync with no further edit changed the source (an edit landed a pass late).");
        Assert.AreEqual(0, secondSync.PatchEdits, "A second sync with no further edit still had edits to make.");
    }

    // 3. The Console must stay clean across the sync that carries both introductions: no exception,
    //    no error, no warning. The wedged sync announced itself as a NullReferenceException in the
    //    Console, so the Console is a first-class assertion surface here.
    [Test]
    public void SceneToCode_TwoNewFieldsOnExpressionBodiedClosure_LogsNoErrorOrWarning()
    {
        var collider = BuildProbeWith("probe.Component<UnityEngine.SphereCollider>(c => c.Set(\"m_Radius\", 2f));");
        SetTwoNewFields(collider);

        var noisy = new List<string>();
        Application.LogCallback capture = (condition, stackTrace, type) =>
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert || type == LogType.Warning)
            {
                noisy.Add($"{type}: {condition}");
            }
        };

        SceneBuilderSync.SyncResult result;
        Application.logMessageReceived += capture;
        try
        {
            result = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        }
        finally
        {
            Application.logMessageReceived -= capture;
        }

        Assert.IsEmpty(noisy,
            "The two-field-introduction sync wrote error/warning noise to the Console:\n" + string.Join("\n", noisy));
        Assert.IsEmpty(result.CompileErrors,
            "The synced source did not compile:\n" + string.Join("; ", result.CompileErrors.Select(d => d.Message)));
        Assert.IsEmpty(result.Conflicts,
            "The two-field-introduction sync reported conflicts:\n"
            + string.Join("; ", result.Conflicts.Select(c => c.Reason)));

        AssertBothFieldsLanded(File.ReadAllText(_builderPath));
    }
}
