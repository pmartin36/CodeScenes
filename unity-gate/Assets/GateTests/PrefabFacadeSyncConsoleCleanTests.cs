using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using SceneBuilder.Editor;

// live-verify-bug-fixes b5-t2 (specs/30-live-verify-bug-fixes.md, consolidated regression-test list
// item 5b): with the b5-t1 façade compile-check stub landed, a real scene->code Sync of a typed
// `.Instance(Prefabs.X)` builder must emit no false CS0103 "'Prefabs' does not exist" / "DOES NOT
// COMPILE" Console noise. Asserted two ways: (1) the observable contract — SyncResult.CompileErrors is
// empty; (2) the Console itself — via LogAssert.Expect for the sync's own known-benign Log messages
// followed by LogAssert.NoUnexpectedReceived(), WITHOUT LogAssert.ignoreFailingMessages, so any
// unexpected message (in particular a compile-check Debug.LogError) fails the test.
public class PrefabFacadeSyncConsoleCleanTests
{
    private const string Dir = "Assets/GateTests/Fixtures_PrefabFacadeSyncConsoleClean";
    private const string ScenePath = "Assets/GateTests/__PrefabFacadeSyncConsoleCleanTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class PrefabFacadeSyncConsoleCleanGateScene : ISceneDefinition
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
        var name = "PrefabFacadeSyncConsoleClean_" + Guid.NewGuid().ToString("N");
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

    // Builds a bare typed `.Instance(Prefabs.Tank)`, live-adds a child under an instance sub-object so
    // Sync actually rewrites the source (and therefore runs BuilderCompileCheck on the freshly emitted
    // `Prefabs.Tank`-referencing text) — the exact path Bug 5 false-positived on before the b5-t1 stub.
    [Test]
    public void Sync_TypedFacadeInstance_LogsNoCompileErrorOrCS0103()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source("        scene.Instance(Prefabs.Tank);"));

        // Once LogAssert.Expect is used anywhere in this test, Unity's test framework strictly checks
        // EVERY log message received for the rest of the test (not only ones after the Expect call) —
        // so the setup Build's own known-benign info message must be expected too, or it fails as
        // "unhandled" at LogAssert.NoUnexpectedReceived() below.
        LogAssert.Expect(LogType.Log, new Regex(@"^\[SceneBuilder\] Built in place: .*", RegexOptions.Singleline));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var tank = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(tank, "Setup: Tank instance root was not created");
        var leftTurret = tank.transform.Find("LeftTurret");
        Assert.IsNotNull(leftTurret, "Setup: LeftTurret sub-object not found");

        var muzzleFlash = new GameObject("MuzzleFlash");
        muzzleFlash.transform.SetParent(leftTurret, false);
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        // The sync below WILL write (a real scene edit) and therefore logs exactly its own known-benign
        // "Synced N edit(s)" info message. Expect ONLY that, so ANYTHING else received — most of all a
        // compile-check Debug.LogError — is left unmatched and fails LogAssert.NoUnexpectedReceived()
        // below. (A sidecar-content-changed "Sidecar updated" log is NOT guaranteed here — Changed can
        // be true from the source edit alone — so it is deliberately not expected.)
        LogAssert.Expect(LogType.Log, new Regex(@"^\[SceneBuilder\] Synced \d+ edit\(s\) back into .*", RegexOptions.Singleline));

        var syncResult = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        LogAssert.NoUnexpectedReceived();

        Assert.IsTrue(syncResult.Changed, "Setup: the live-added child must produce a real sync write.");
        Assert.IsEmpty(syncResult.CompileErrors,
            "A typed-facade Sync must emit compilable source — zero compile errors.\n"
            + string.Join("; ", syncResult.CompileErrors.Select(d => d.Message)));
        Assert.IsFalse(
            syncResult.CompileErrors.Any(d => d.Message.Contains("CS0103") || d.Message.Contains("DOES NOT COMPILE")),
            "Bug 5 regression: a typed `Prefabs.*` Sync must never report a false CS0103 / DOES NOT COMPILE.\n"
            + string.Join("; ", syncResult.CompileErrors.Select(d => d.Message)));

        var synced = File.ReadAllText(_builderPath);
        StringAssert.Contains("Instance(Prefabs.Tank)", synced,
            "The typed instance must remain in the synced source.\n" + synced);
    }
}
