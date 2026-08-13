using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// A below-root `.On(sel, c => c.RemoveComponent<T>())` authored alongside its own first (CREATE)
// `Instance(...)` statement. Differ.EmitCreateInstance applies the authored RemovedComponents on
// build 1; the second build's diff re-reads the live snapshot's SubKey-keyed removal and must
// converge with the authored target's own SubKey. The removed component's owner (the below-root
// sub-object itself) stays live, so NestedOverrideBootstrap resolves the target's real SubKey from
// it before the second build's diff runs. Pins that convergence: without it, the diff would misread
// the authored removal as "desired wants this reverted" and PrefabUtility.RevertRemovedComponent
// would silently restore the component the user asked to remove.
public class RemovedComponentSubKeyTests
{
    private const string Dir = "Assets/GateTests/Fixtures_RemovedComponentSubKey";
    private const string ScenePath = "Assets/GateTests/__RemovedComponentSubKeyTemp.unity";

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class RemovedComponentSubKeyGateScene : ISceneDefinition
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
        var buildersDir = SceneBuilderPaths.EnsureBuildersDirectory();
        var name = "RemovedComponentSubKeyGate_" + Guid.NewGuid().ToString("N");
        _builderPath = Path.Combine(buildersDir, name + ".cs");
        _sidecarPath = Path.Combine(buildersDir, name + ".sbmap.json");

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_builderPath)) File.Delete(_builderPath);
        if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);

        PrefabFacadeFixture.Delete(Dir);

        if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);
    }

    [Test]
    public void BelowRootRemoveComponent_AuthoredOnFreshInstance_ConvergesAcrossTwoBuilds()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source(
            "        scene.Instance(Prefabs.Tank).On(t => t.LeftTurret.Barrel, c => c.RemoveComponent<UnityEngine.Light>());"));

        var first = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(first.Diagnostics,
            "First build reported diagnostics: " + string.Join("; ", first.Diagnostics.Select(d => d.Message)));

        var tank = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(tank, "Tank instance root was not created by the first build");
        var barrel = tank.transform.Find("LeftTurret/Barrel");
        Assert.IsNotNull(barrel, "LeftTurret/Barrel sub-object missing after the first build");
        Assert.IsNull(barrel.GetComponent<Light>(),
            "Authored RemoveComponent<Light>() on LeftTurret/Barrel did not remove it on the CREATE build");

        var second = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(second.Diagnostics,
            "Second build reported diagnostics (below-root RemoveComponent SubKey did not converge): "
            + string.Join("; ", second.Diagnostics.Select(d => d.Message)));

        tank = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        barrel = tank.transform.Find("LeftTurret/Barrel");
        Assert.IsNull(barrel.GetComponent<Light>(),
            "The second build's re-diff spuriously reverted the authored RemoveComponent<Light>() — "
            + "LeftTurret/Barrel regained its Light component");
        Assert.IsNotNull(tank.transform.Find("LeftTurret/Antenna").GetComponent<MeshRenderer>(),
            "Second build removed an unrelated component (LeftTurret/Antenna's MeshRenderer) — SubKey resolved to the wrong sub-object");
    }
}
