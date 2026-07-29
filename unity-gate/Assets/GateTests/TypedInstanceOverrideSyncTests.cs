using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using SceneBuilder.Editor;
using SceneBuilder.Core.Serialization;

// specs/30-live-verify-bug-fixes.md Bug 1 (CRITICAL), task b1-t2 (1b + 1c). Hand-authors the
// TYPED prefab-instance-override forms — never the string/serialized-path form the rest of the
// suite captures from a live scene edit — because that typed form is exactly what
// AuthoredPathResolver's Overrides/AddedComponents/RemovedComponents gap silently drops.
public class TypedInstanceOverrideSyncTests
{
    private const string Dir = "Assets/GateTests/Fixtures_TypedInstanceOverrideSync";
    private const string ScenePath = "Assets/GateTests/__TypedInstanceOverrideSyncTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string extraUsings, string body) => $@"
using SceneBuilder.Authoring;
{extraUsings}
public class TypedInstanceOverrideSyncGateScene : ISceneDefinition
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
        var name = "TypedInstanceOverrideSyncGate_" + Guid.NewGuid().ToString("N");
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

    // Bug 5 (façade CS0103 compile-noise) is a separate task (b5) not yet fixed — Sync's compile
    // check still logs a false DOES NOT COMPILE for every façade-authored builder. Suppressed here
    // exactly like every other pre-b5 nested/facade EditMode test (NestedRoundTripTests,
    // NestedTypedEmitTests) so THIS test isolates Bug 1 only.
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

    // Adds a live Rigidbody component to the fixture's Tank.prefab ROOT (the canonical
    // PrefabFacadeFixture ships none) so a root-level `.Override(e => e.Set((Rigidbody x) =>
    // x.mass, v))` has a real component/field to materialize onto — an unresolved-type override
    // silently no-ops rather than erroring, so without a real Rigidbody a still-buggy resolve and a
    // fixed resolve would be indistinguishable by the materialized value alone.
    private static void AddRigidbodyToTankRoot(PrefabFacadeFixture.Handles handles)
    {
        var contents = PrefabUtility.LoadPrefabContents(handles.TankPath);
        if (contents.GetComponent<Rigidbody>() == null)
        {
            contents.AddComponent<Rigidbody>();
        }
        PrefabUtility.SaveAsPrefabAsset(contents, handles.TankPath);
        PrefabUtility.UnloadPrefabContents(contents);

        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(handles.TankPath, ImportAssetOptions.ForceUpdate);
    }

    private static List<string> CaptureLogs(Action action)
    {
        var messages = new List<string>();
        void Handler(string condition, string stackTrace, LogType type) => messages.Add(condition);
        Application.logMessageReceived += Handler;
        try
        {
            action();
        }
        finally
        {
            Application.logMessageReceived -= Handler;
        }
        return messages;
    }

    // 1b: hand-author the typed NESTED selector form, Build, then Sync with NO scene edits.
    // AuthoredPathResolver never descends PrefabInstanceNode.Overrides, so the parsed target still
    // carries `member:intensity` + verbatim "UnityEngine.Light" while the live snapshot/sidecar
    // carry the serialized `m_Intensity` + FullName key — ReconcilerInstances' (Target,PropertyPath)
    // diff never matches, treats the model override as gone, and drops+rewrites `.On(...)` to a bare
    // `scene.Instance(Prefabs.Tank);` on a no-op sync.
    //
    // Two Build.Run calls (setup bare instance, then rebuild with the override authored) — v1
    // instance creation (Differ.EmitCreateInstance) emits no override ops at all on a brand-new
    // instance (see NestedSidecarTests), so a single one-shot Build authoring the override together
    // with the instance would never exercise the override-apply path this test targets.
    [Test]
    public void NestedTypedOverride_SurvivesNoOpSync_OnBlockByteReserved_SidecarIntact()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source(string.Empty, "        scene.Instance(Prefabs.Tank);"));
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        File.WriteAllText(_builderPath, Source(string.Empty,
            "        scene.Instance(Prefabs.Tank)\n" +
            "            .On(sel => sel.LeftTurret.Barrel, b => b.Override(x => x.Set((UnityEngine.Light l) => l.intensity, 4f)));"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Rebuild authoring the hand-authored typed nested override reported diagnostics: "
            + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var root = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(root, "Setup: Tank instance root was not created");
        var barrel = root.transform.Find("LeftTurret/Barrel");
        Assert.IsNotNull(barrel, "Setup: LeftTurret/Barrel not found");
        Assert.AreEqual(4f, barrel.GetComponent<Light>().intensity, 0.0001f,
            "Setup: the typed nested override did not materialize m_Intensity=4 at Build");

        var beforeSync = File.ReadAllText(_builderPath);
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        // No live edits since the build above — a Sync of this exact state must be a genuine no-op.
        var syncResult = SyncIgnoringFacadeCompileNoise(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsFalse(syncResult.Changed,
            "A no-op sync of a just-Built, un-edited typed nested override must not rewrite the "
            + "builder — Changed=true means AuthoredPathResolver's un-normalized "
            + "(member:intensity, \"UnityEngine.Light\") key mismatched the live snapshot's "
            + "(m_Intensity, FullName) key and the reconciler dropped+re-appended the .On block.");

        var afterSync = File.ReadAllText(_builderPath);
        Assert.AreEqual(beforeSync, afterSync,
            "No-op sync changed builder source bytes.\n" + afterSync);
        StringAssert.Contains(".On(sel => sel.LeftTurret.Barrel", afterSync,
            "The hand-authored typed .On(...) block was rewritten away.\n" + afterSync);
        StringAssert.DoesNotContain("scene.Instance(Prefabs.Tank);\n", afterSync,
            "The .On(...) override was dropped, leaving a bare Instance(Prefabs.Tank) call.\n" + afterSync);

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var entry = map.Entries.Single(e => e.Kind == "PrefabInstance");
        var ov = entry.Overrides?.SingleOrDefault(o => o.Kind == "Property" && o.PropertyPath == "m_Intensity");
        Assert.IsNotNull(ov,
            "Sidecar InstanceOverrideRecord for the nested override was nulled by the no-op sync.\n"
            + File.ReadAllText(_sidecarPath));
    }

    // 1c: hand-author the M10 root-level typed override with an UNQUALIFIED component type under
    // `using UnityEngine;`. Materializing a SetInstanceOverride resolves the target's ComponentType
    // via the no-usings ComponentTypeResolver.Resolve(string) overload, which cannot resolve the
    // short "Rigidbody" token, logs "Could not resolve component type 'Rigidbody'.", and silently
    // no-ops the override while Build still reports success.
    //
    // Two Build.Run calls (setup bare instance, then rebuild with the override authored) — see
    // NestedTypedOverride_SurvivesNoOpSync_OnBlockByteReserved_SidecarIntact for why a one-shot
    // Build authoring the override on a brand-new instance would never exercise this path.
    [Test]
    public void ShortTypeRootOverride_ResolvesAtBuild_NoUnresolvedWarning_MaterializesAsOverride()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        AddRigidbodyToTankRoot(handles);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source("using UnityEngine;", "        scene.Instance(Prefabs.Tank);"));
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        File.WriteAllText(_builderPath, Source("using UnityEngine;",
            "        scene.Instance(Prefabs.Tank)\n" +
            "            .Override(e => e.Set((Rigidbody x) => x.mass, 7f));"));

        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.BuildResult buildResult = null;
        var logs = CaptureLogs(() =>
        {
            buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        });

        Assert.IsEmpty(buildResult.Diagnostics,
            "Rebuild authoring the hand-authored short-type root override reported diagnostics: "
            + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        Assert.IsFalse(logs.Any(m => m.Contains("Could not resolve component type 'Rigidbody'")),
            "Build logged an unresolved-component-type warning for the unqualified 'Rigidbody' "
            + "token — the short type must resolve via the usings-aware pre-diff normalization, not "
            + "silently drop the override.\nLogs:\n" + string.Join("\n", logs));

        var root = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(root, "Setup: Tank instance root was not created");
        var rigidbody = root.GetComponent<Rigidbody>();
        Assert.IsNotNull(rigidbody, "Setup: Tank instance has no Rigidbody component");
        Assert.AreEqual(7f, rigidbody.mass, 0.0001f,
            "The short-type ('Rigidbody') root override did not materialize m_Mass=7 — it was "
            + "silently dropped because the unqualified type token failed to resolve.");

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var entry = map.Entries.Single(e => e.Kind == "PrefabInstance");
        var ov = entry.Overrides?.SingleOrDefault(o => o.Kind == "Property" && o.PropertyPath == "m_Mass");
        Assert.IsNotNull(ov,
            "Sidecar has no persisted Kind=Property m_Mass override — the short-type override did "
            + "not persist as a genuine instance override.\n" + File.ReadAllText(_sidecarPath));
        Assert.AreEqual("UnityEngine.Rigidbody", ov.ComponentType,
            "Persisted override record's ComponentType was not resolved to the FullName 'UnityEngine.Rigidbody'");
    }
}
