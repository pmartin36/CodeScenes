using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// The EMIT (scene->code) direction of an added child UNDER a prefab instance
// (specs/24-nested-prefab-overrides.md checklist #5/#6, adapter boundary). Every other EditMode
// AddChild test covers AUTHORING->EXECUTION (`.AddChild(...)` in source -> live child created); this
// closes the reverse gap at the SceneBuilderSync boundary — a child that exists live under an
// instance sub-object (an AddedGameObject override) must Sync to a `.AddChild(...)` SCOPED to that
// instance, NEVER a top-level `scene.Add(...)` that would re-create the child as a plain scene
// object. Mirrors the Core expectation PrefabInstanceReconcileTests.AddedChildGameObject_RoundTrips
// (`.AddChild("Turret", "MuzzleFlash")` on the handled instance, no root re-append) at the real
// adapter emit path. Against the canonical Tank/Turret fixture.
public class InstanceAddReconcileTests
{
    private const string Dir = "Assets/GateTests/Fixtures_InstanceAddReconcile";
    private const string ScenePath = "Assets/GateTests/__InstanceAddReconcileTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class InstanceAddReconcileGateScene : ISceneDefinition
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
        var name = "InstanceAddReconcileGate_" + Guid.NewGuid().ToString("N");
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

    // Builds a bare typed `.Instance(Prefabs.Tank)`, then live-adds a plain GameObject "MuzzleFlash"
    // under the instance's LeftTurret sub-object — the real user action's data effect: an
    // AddedGameObjects override on the instance (asserted here so a setup that fails to register the
    // override reddens loudly, never a trivially-green test). Sync must read that override back and
    // EMIT a `.AddChild(...)` scoped to the instance.
    [Test]
    public void Sync_SceneAddedChildUnderInstance_EmitsScopedAddChild_NotTopLevelSceneAdd()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source("        scene.Instance(Prefabs.Tank);"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var tank = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(tank, "Setup: Tank instance root was not created");
        var leftTurret = tank.transform.Find("LeftTurret");
        Assert.IsNotNull(leftTurret, "Setup: LeftTurret sub-object not found");

        // The real scene-side action: a plain child added under the instance's sub-object. Unity
        // records it as an AddedGameObjects override on the Tank instance — the state Sync must read.
        var muzzleFlash = new GameObject("MuzzleFlash");
        muzzleFlash.transform.SetParent(leftTurret, false);
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var added = PrefabUtility.GetAddedGameObjects(tank)
            ?? new System.Collections.Generic.List<UnityEditor.SceneManagement.AddedGameObject>();
        Assert.IsTrue(added.Any(a => a.instanceGameObject == muzzleFlash),
            "Setup: the live-added MuzzleFlash was not recorded as an AddedGameObjects override on the "
            + "Tank instance — the emit path would have nothing to read back.");

        var syncResult = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(syncResult.Changed,
            "Sync did not write — a scene-added child under the instance must be captured back into source.");

        var synced = File.ReadAllText(_builderPath);

        // EMIT direction, adapter boundary: the added child round-trips into source as a `.AddChild`
        // scoped to the instance (chained on `.Instance(Prefabs.Tank)`). The PARENT sub-object carries
        // a façade identity, so it emits the TYPED selector (`sel => sel.LeftTurret`) — typed-by-default,
        // matching `.On` (specs/27); the new child NAME stays a string.
        StringAssert.Contains(".AddChild(sel => sel.LeftTurret, \"MuzzleFlash\")", synced,
            "Sync did not emit a typed `.AddChild(sel => sel.Parent, name)` for the scene-added child.\n" + synced);
        StringAssert.Contains("Instance(Prefabs.Tank)", synced,
            "Sync dropped the typed instance the AddChild must scope under.\n" + synced);

        // And it must NEVER re-create the child as a plain top-level scene object.
        StringAssert.DoesNotContain("scene.Add(\"MuzzleFlash\"", synced,
            "Sync wrongly emitted a top-level `scene.Add(\"MuzzleFlash\")` — a scene-added child under an "
            + "instance must Sync to a scoped `.AddChild(...)`, not a top-level scene.Add that re-creates "
            + "it as a plain scene object.\n" + synced);
        StringAssert.DoesNotContain("scene.Add(\"MuzzleFlash\")", synced,
            "Sync wrongly emitted a top-level `scene.Add(\"MuzzleFlash\")`.\n" + synced);
    }

    // SceneSnapshotReader.FromRoots's type walk (CollectComponentTypeNames) only visits
    // node.Components/node.Children, so a component reachable ONLY through the prefab-instance
    // AddedComponents channel never registers a ComponentDefaults entry. Emit-side omission
    // (ComponentDefaultOmission.OmitDefaults) is fail-closed against a missing template, so it keeps
    // every field instead of omitting the defaulted ones, reintroducing the C2 noise-dump the feature
    // exists to remove on the instance-added-component path. A live Light added directly to the Tank
    // instance ROOT (an AddedComponents override, not AddedGameObjects) must Sync to a bare
    // `.AddComponent<UnityEngine.Light>();` with no default-valued field setters, and a second Sync
    // must be a fixed point.
    [Test]
    public void Sync_SceneAddedComponentOnInstance_EmitsBareAddComponent_NoDefaultFieldSetters()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source("        scene.Instance(Prefabs.Tank);"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var tank = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(tank, "Setup: Tank instance root was not created");

        // The real scene-side action: a component added directly to the instance ROOT, left at every
        // field's constructed default. Unity records it as an AddedComponents override on the Tank
        // instance: the state Sync must read.
        var light = tank.AddComponent<Light>();
        Assert.IsNotNull(light, "Setup: AddComponent<Light> on the Tank instance root failed");
        Assert.IsTrue(PrefabUtility.GetAddedComponents(tank).Any(a => a.instanceComponent is Light),
            "Setup: the live-added Light was not recorded as an AddedComponents override on the Tank "
            + "instance — the emit path would have nothing to read back.");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var syncResult = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(syncResult.Changed,
            "Sync did not write — a scene-added component on the instance must be captured back into source.");

        var synced = File.ReadAllText(_builderPath);

        StringAssert.Contains(".AddComponent<UnityEngine.Light>();", synced,
            "Sync did not emit a bare `.AddComponent<UnityEngine.Light>();` for the scene-added component.\n" + synced);
        StringAssert.DoesNotContain("m_Intensity", synced,
            "Sync dumped a default-valued field (m_Intensity) into the added component's source — the "
            + "instance-added-component ComponentDefaults template was not populated.\n" + synced);
        StringAssert.DoesNotContain("m_Range", synced,
            "Sync dumped a default-valued field (m_Range) into the added component's source.\n" + synced);
        StringAssert.DoesNotContain("c.Set(", synced,
            "Sync emitted field setters for a fresh, all-default added component.\n" + synced);

        // Fixed point: the added component now matches its authored `.AddComponent<Light>()`, so a
        // second Sync must report no change.
        var secondSync = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(secondSync.Changed, "Second Sync was not a fixed point.");
    }

    // The direct pin on the fix itself, not just its consequence: SceneSnapshotReader.FromRoots must
    // populate SceneSnapshot.ComponentDefaults for a type reachable ONLY through the prefab-instance
    // AddedComponents channel. Its absence is exactly what let the noise-dump above ship silently
    // (the emit-side omission was correct all along; only the registry feeding it was incomplete).
    [Test]
    public void Read_SceneAddedComponentOnInstance_PopulatesComponentDefaultsForAddedType()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source("        scene.Instance(Prefabs.Tank);"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var tank = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(tank, "Setup: Tank instance root was not created");

        tank.AddComponent<Light>();
        Assert.IsTrue(PrefabUtility.GetAddedComponents(tank).Any(a => a.instanceComponent is Light),
            "Setup: the live-added Light was not recorded as an AddedComponents override on the Tank instance.");

        var snapshot = SceneSnapshotReader.Read(EditorSceneManager.GetActiveScene(), resolveSceneRef: null);
        var tankNode = snapshot.Roots.First(n => n.Name == "Tank");
        Assert.IsTrue(tankNode.AddedComponents.Any(a => a.Component.Type.FullName == "UnityEngine.Light"),
            "Setup: the snapshot's Tank node did not carry the added Light on its AddedComponents channel.");

        Assert.IsTrue(snapshot.ComponentDefaults.Any(c => c.Type.FullName == "UnityEngine.Light"),
            "SceneSnapshot.ComponentDefaults carries no template for UnityEngine.Light, though the type is "
            + "present in the snapshot on the AddedComponents channel. FromRoots's type walk must visit "
            + "every ComponentData channel, not just node.Components/node.Children.");
    }
}
