using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// The added-child twin of RoundTripObjectRefPrefabInstanceTests: a child GameObject live-added
// under a prefab instance's sub-object, carrying a component with a scene reference to another
// live scene object, must Sync to a `.AddChild(...)` whose component closure renders the reference
// as a real handle expression -- never a raw GlobalObjectId, never a thrown exception -- and the
// emitted source must compile and converge to a fixed point on a second sync. Mirrors
// InstanceAddReconcileTests.Sync_SceneAddedChildUnderInstance_EmitsScopedAddChild_NotTopLevelSceneAdd,
// adding the DoorOpener.target cross-object reference onto the added child's own component.
public class AddedChildReferenceRoundTripTests
{
    private const string Dir = "Assets/GateTests/Fixtures_AddedChildReferenceRoundTrip";
    private const string ScenePath = "Assets/GateTests/__AddedChildReferenceRoundTripTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class AddedChildReferenceRoundTripGateScene : ISceneDefinition
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
        var name = "AddedChildReferenceRoundTripGate_" + Guid.NewGuid().ToString("N");
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

    [Test]
    public void Sync_AddedChildComponentReferencesLiveSceneObject_RendersHandle_CompilesAndConverges()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        // Door is declared before the Tank instance: the reference is folded in place into the
        // anchor statement it is authored on, so the referenced handle must already be declared
        // earlier or the rendered source forward-references it and fails to compile.
        File.WriteAllText(_builderPath, Source(
            "        var door = scene.Add(\"Door\");\n" +
            "        scene.Instance(Prefabs.Tank);"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var tank = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        var door = FindRoot(EditorSceneManager.GetActiveScene(), "Door");
        Assert.IsNotNull(tank, "Setup: Tank instance root was not created");
        Assert.IsNotNull(door, "Setup: Door was not created");
        var leftTurret = tank.transform.Find("LeftTurret");
        Assert.IsNotNull(leftTurret, "Setup: LeftTurret sub-object not found");

        // The real scene-side action: a plain child added under the instance's sub-object, carrying
        // a component whose field references the other live scene object. Unity records the whole
        // subtree (including this component's live value) as one AddedGameObjects override on Tank.
        var muzzleFlash = new GameObject("MuzzleFlash");
        muzzleFlash.transform.SetParent(leftTurret, false);
        var opener = muzzleFlash.AddComponent<DoorOpener>();
        opener.target = door;
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var added = PrefabUtility.GetAddedGameObjects(tank)
            ?? new System.Collections.Generic.List<UnityEditor.SceneManagement.AddedGameObject>();
        Assert.IsTrue(added.Any(a => a.instanceGameObject == muzzleFlash),
            "Setup: the live-added MuzzleFlash was not recorded as an AddedGameObjects override on the "
            + "Tank instance -- the emit path would have nothing to read back.");

        var syncResult = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(syncResult.Changed,
            "Sync did not write -- a scene-added child under the instance must be captured back into source.");

        var synced = File.ReadAllText(_builderPath);

        StringAssert.Contains(".AddChild(sel => sel.LeftTurret, \"MuzzleFlash\"", synced,
            "Sync did not emit the scoped `.AddChild(...)` for the scene-added child.\n" + synced);
        StringAssert.Contains("c.Set(\"target\", door)", synced,
            "Sync did not render the added child's cross-object reference as a real handle expression.\n" + synced);

        // Fixed point: the added child (and its reference) now matches the authored source, so a
        // second Sync must report no change.
        var secondSync = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(secondSync.Changed, "A settled added-child reference must sync as a fixed point.");
    }
}
