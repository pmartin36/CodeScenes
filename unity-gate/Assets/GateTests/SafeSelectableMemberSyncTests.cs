using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// specs/54-selfheal-inaccessible-typed-selector.md's EditMode acceptance criterion: on a live
// prefab-instance override, a typed selector authored over a serialized property that is NOT a
// public C# member -- a native `m_`-prefixed field (CS1061) or a `[SerializeField] private` field
// (CS0122) -- heals to the string form on a diff-independent sync (the value is unchanged; only the
// selector text was never safe), the rewritten source compiles, and a second sync is byte-stable.
// A converged override authored over a genuinely safe native property (Light.intensity) must never
// be over-downgraded -- covered by TypedInstanceOverrideSyncTests.NestedTypedOverride_SurvivesNoOpSync.
public class SafeSelectableMemberSyncTests
{
    private const string Dir = "Assets/GateTests/Fixtures_SafeSelectableMemberSync";
    private const string ScenePath = "Assets/GateTests/__SafeSelectableMemberSyncTemp.unity";

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class SafeSelectableMemberSyncGateScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    // Attaches a component whose sole serialized field is `[SerializeField] private` (no public
    // spelling at all) to the Tank prefab's ROOT, mirroring TypedInstanceOverrideSyncTests'
    // AddRigidbodyToTankRoot -- the base prefab must already carry the property for a `.Override`
    // to modify it as a genuine property override rather than an added component.
    private static void AddInaccessibleFieldBehaviourToTankRoot(PrefabFacadeFixture.Handles handles)
    {
        var contents = PrefabUtility.LoadPrefabContents(handles.TankPath);
        if (contents.GetComponent<GateFixtures.InaccessibleFieldFixtureBehaviour>() == null)
        {
            contents.AddComponent<GateFixtures.InaccessibleFieldFixtureBehaviour>();
        }
        PrefabUtility.SaveAsPrefabAsset(contents, handles.TankPath);
        PrefabUtility.UnloadPrefabContents(contents);

        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(handles.TankPath, ImportAssetOptions.ForceUpdate);
    }

    [SetUp]
    public void SetUp()
    {
        var buildersDir = SceneBuilderPaths.EnsureBuildersDirectory();
        var name = "SafeSelectableMemberSyncGate_" + Guid.NewGuid().ToString("N");
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
    }

    // Nested override, native `m_`-prefixed selector (CS1061): `UnityEngine.Light` has no public
    // member named `m_Intensity` -- its real C# spelling is `intensity` -- but the raw serialized
    // name still resolves at Build/read time, so a hand-authored `l.m_Intensity` selector
    // materializes fine and is exactly the non-compiling emission spec 54 targets. The value is
    // UNCHANGED across the sync (diff-independent case): only the selector text is unsafe.
    [Test]
    public void Override_NestedNativeRawSelector_ConvergedNoOpSync_HealsToStringForm_CompilesByteStable()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source("        scene.Instance(Prefabs.Tank);"));
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        File.WriteAllText(_builderPath, Source(
            "        scene.Instance(Prefabs.Tank)\n" +
            "            .On(sel => sel.LeftTurret.Barrel, b => b.Override(x => x.Set((UnityEngine.Light l) => l.m_Intensity, 4f)));"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Rebuild authoring the native-raw-selector override reported diagnostics: "
            + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var root = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(root, "Setup: Tank instance root was not created");
        var barrel = root.transform.Find("LeftTurret/Barrel");
        Assert.IsNotNull(barrel, "Setup: LeftTurret/Barrel not found");
        Assert.AreEqual(4f, barrel.GetComponent<Light>().intensity, 0.0001f,
            "Setup: the native-raw-selector override did not materialize m_Intensity=4 at Build");

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        // No live edit since the build above -- the value is converged; only the selector heals.
        var syncResult = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(syncResult.Changed,
            "Sync reported no change -- the diff-independent self-heal did not fire for the "
            + "not-a-public-member native selector.");

        var healed = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("l.m_Intensity", healed,
            "Emitted source must never carry a typed selector naming a native, not-a-public-member "
            + "serialized field.\n" + healed);
        StringAssert.Contains("\"m_Intensity\"", healed,
            "Healed override must carry the string-key form.\n" + healed);

        // Re-sync the healed source: zero edits, no perpetual rewrite.
        var secondSync = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(secondSync.Changed,
            "Re-syncing the healed (string-form) override rewrote the builder again -- not "
            + "byte-stable.\n" + File.ReadAllText(_builderPath));
    }

    // Root override, `[SerializeField] private` selector (CS0122): the field has no public
    // spelling of any kind. Same diff-independent shape as above, but at the ROOT (flat
    // `.Override(...)`, not the scoped `.On(...)` fork) and via the managed-field ladder rather
    // than the native one -- the two production paths the fix's SafeMemberIndex must both cover.
    [Test]
    public void Override_RootPrivateFieldSelector_ConvergedNoOpSync_HealsToStringForm_CompilesByteStable()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        AddInaccessibleFieldBehaviourToTankRoot(handles);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source("        scene.Instance(Prefabs.Tank);"));
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        File.WriteAllText(_builderPath, Source(
            "        scene.Instance(Prefabs.Tank)\n" +
            "            .Override(e => e.Set((GateFixtures.InaccessibleFieldFixtureBehaviour x) => x.m_Secret, 8f));"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Rebuild authoring the private-field-selector override reported diagnostics: "
            + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var root = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(root, "Setup: Tank instance root was not created");
        var behaviour = root.GetComponent<GateFixtures.InaccessibleFieldFixtureBehaviour>();
        Assert.IsNotNull(behaviour, "Setup: Tank instance has no InaccessibleFieldFixtureBehaviour");
        Assert.AreEqual(8f, behaviour.ReadSecret, 0.0001f,
            "Setup: the private-field-selector override did not materialize m_Secret=8 at Build");

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        // No live edit since the build above -- the value is converged; only the selector heals.
        var syncResult = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(syncResult.Changed,
            "Sync reported no change -- the diff-independent self-heal did not fire for the "
            + "private-field selector.");

        var healed = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("x.m_Secret", healed,
            "Emitted source must never carry a typed selector naming a private serialized field.\n" + healed);
        StringAssert.Contains("\"m_Secret\"", healed,
            "Healed override must carry the string-key form.\n" + healed);

        // Re-sync the healed source: zero edits, no perpetual rewrite.
        var secondSync = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(secondSync.Changed,
            "Re-syncing the healed (string-form) override rewrote the builder again -- not "
            + "byte-stable.\n" + File.ReadAllText(_builderPath));
    }
}
