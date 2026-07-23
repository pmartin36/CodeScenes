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

// m-nested-props b7-t2 (specs/24-nested-prefab-overrides.md checklist #1): a genuine no-op scene->code
// sync of an already-converged, Sync-authored TYPED nested override must be a true no-op. Without
// NestedOverrideBootstrap stamping the desired target's SubKey from the live sub-object, the parsed
// desired target stays (0,0) forever while the live snapshot carries the real SubKey; ReconcilerInstances'
// (Target,PropertyPath)-keyed diff sees them as non-equal keys and spuriously drops the authored `.On`
// block (model-only (0,0) key) and re-appends an equivalent one (snapshot-only real key) — a rewrite
// with no genuine source-of-truth change. Against the canonical Tank/Turret fixture (b6-t1).
public class NestedTypedEmitTests
{
    private const string Dir = "Assets/GateTests/Fixtures_NestedTypedEmit";
    private const string ScenePath = "Assets/GateTests/__NestedTypedEmitTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class NestedTypedEmitGateScene : ISceneDefinition
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
        var name = "NestedTypedEmitGate_" + Guid.NewGuid().ToString("N");
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
    public void Sync_NoOpAfterFirstAuthorSync_NoChurn_NoGuidInSource()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source("        scene.Instance(Prefabs.Tank);"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var root = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(root, "Setup: Tank instance root was not created");
        var barrel = root.transform.Find("LeftTurret/Barrel");
        var light = barrel.GetComponent<Light>();
        light.intensity = 4f;
        PrefabUtility.RecordPrefabInstancePropertyModifications(light);
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var firstSync = SyncIgnoringFacadeCompileNoise(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(firstSync.Changed, "Setup: first sync did not capture the live nested override");

        var afterFirstSync = File.ReadAllText(_builderPath);
        StringAssert.Contains("sel => sel.", afterFirstSync,
            "Typed selector form ('sel => sel.') was not emitted — checklist #1 requires a typed, not "
            + "string, nested selector.\n" + afterFirstSync);
        Assert.IsFalse(
            System.Text.RegularExpressions.Regex.IsMatch(afterFirstSync, "[0-9a-fA-F]{32}"),
            "Emitted source must never carry a raw GUID/fileID.\n" + afterFirstSync);

        // No further scene edits — a second sync of the SAME converged state must be a true no-op.
        var secondSync = SyncIgnoringFacadeCompileNoise(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsFalse(secondSync.Changed,
            "A no-op sync (no live edits since the previous sync captured this exact nested override) "
            + "must not rewrite the builder — a churn here means the desired target's un-stamped SubKey "
            + "mismatched the live snapshot's real SubKey and the reconciler dropped+re-appended the "
            + "authored .On block.");

        var afterSecondSync = File.ReadAllText(_builderPath);
        Assert.AreEqual(afterFirstSync, afterSecondSync,
            "No-op sync changed builder source bytes.\n" + afterSecondSync);
    }
}
