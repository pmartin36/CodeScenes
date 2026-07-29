using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using SceneBuilder.Editor;

// specs/30-live-verify-bug-fixes.md Bug 3, task b2-t1 (3a). Live repro of "revert-to-default leaves
// a stale .Override(...)/.On(...) call": Build a typed nested instance override, then in the live
// scene PrefabUtility.RevertPropertyOverride it back to prefab default (removing it from what the
// snapshot reader sees) — a genuinely snapshot-absent model override — and Sync must drop the
// authored call and report Changed==true, with a second Sync a true no-op. Downstream of b1-t2
// (normalization) per the bucket dependency; the Core-side guard is pinned separately in
// InstanceOverrideDropGuardTests.
public class OverrideRevertToDefaultTests
{
    private const string Dir = "Assets/GateTests/Fixtures_OverrideRevertToDefault";
    private const string ScenePath = "Assets/GateTests/__OverrideRevertToDefaultTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class OverrideRevertToDefaultGateScene : ISceneDefinition
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
        _manifestPath = SceneBuilderPaths.FacadeManifestPath;
        _manifestExistedBefore = File.Exists(_manifestPath);
        _manifestBytesBefore = _manifestExistedBefore ? File.ReadAllBytes(_manifestPath) : null;

        var buildersDir = SceneBuilderPaths.EnsureBuildersDirectory();
        var name = "OverrideRevertToDefaultGate_" + Guid.NewGuid().ToString("N");
        _builderPath = Path.Combine(buildersDir, name + ".cs");
        _sidecarPath = Path.Combine(buildersDir, name + ".sbmap.json");

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_builderPath))
        {
            File.Delete(_builderPath);
        }
        if (File.Exists(_sidecarPath))
        {
            File.Delete(_sidecarPath);
        }

        PrefabFacadeFixture.Delete(Dir);

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

    // Bug 5 (façade CS0103 compile-noise) is a separate un-fixed task (b5) — Sync's compile check
    // still logs a false DOES NOT COMPILE for every façade-authored builder. Suppressed here exactly
    // like TypedInstanceOverrideSyncTests so THIS test isolates Bug 3 only.
    private static SceneBuilderSync.SyncResult SyncIgnoringFacadeCompileNoise(string builderPath, string sidecarPath, Scene scene)
    {
        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            return SceneBuilderSync.Run(builderPath, sidecarPath, scene);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = prevIgnore;
        }
    }

    [Test]
    public void OverrideRevertToDefault_SyncDropsAuthoredCall_SecondSyncNoOp()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        // Setup: bare instance, no override.
        File.WriteAllText(_builderPath, Source("        scene.Instance(Prefabs.Tank);"));
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        // Rebuild authoring the typed nested override (materializes m_Intensity=4 + sidecar record).
        File.WriteAllText(_builderPath, Source(
            "        scene.Instance(Prefabs.Tank)\n" +
            "            .On(sel => sel.LeftTurret.Barrel, b => b.Override(x => x.Set((UnityEngine.Light l) => l.intensity, 4f)));"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Rebuild authoring the typed nested override reported diagnostics: "
            + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var root = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(root, "Setup: Tank instance root was not created");
        var barrel = root.transform.Find("LeftTurret/Barrel");
        Assert.IsNotNull(barrel, "Setup: LeftTurret/Barrel not found");
        var light = barrel.GetComponent<Light>();
        Assert.AreEqual(4f, light.intensity, 0.0001f,
            "Setup: the typed nested override did not materialize m_Intensity=4 at Build");

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        // Live revert-to-default: removes the modification from the live prefab-instance override
        // list entirely (the snapshot reader will no longer see this key at all).
        var so = new SerializedObject(light);
        var prop = so.FindProperty("m_Intensity");
        Assert.IsNotNull(prop, "Setup: could not find m_Intensity SerializedProperty on the barrel Light");
        PrefabUtility.RevertPropertyOverride(prop, InteractionMode.AutomatedAction);
        so.Update();
        Assert.IsFalse(Mathf.Approximately(4f, so.FindProperty("m_Intensity").floatValue),
            "Setup: RevertPropertyOverride did not restore the prefab default — repro precondition not met");

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var syncResult = SyncIgnoringFacadeCompileNoise(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsTrue(syncResult.Changed,
            "Sync after a live PrefabUtility.RevertPropertyOverride must detect the snapshot-absent "
            + "override and rewrite the builder to drop the now-stale .On(...) call — Changed=false "
            + "means the reverted override was not detected as gone.");

        var afterSync = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain(".On(sel => sel.LeftTurret.Barrel", afterSync,
            "The reverted-to-default override's .On(...) call was not removed from source.\n" + afterSync);

        var secondSync = SyncIgnoringFacadeCompileNoise(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(secondSync.Changed,
            "A second sync immediately after the drop-sync must be a genuine no-op.");
    }
}
