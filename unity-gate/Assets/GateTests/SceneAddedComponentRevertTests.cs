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
using UnityAddedComponent = UnityEditor.SceneManagement.AddedComponent;

// specs/30-live-verify-bug-fixes.md Bug 4, task b4-t1. Live repro of "scene-added component not
// reverted on code->scene rebuild": a LIVE-added component override (GameObject.AddComponent on a
// prefab instance in the open scene, never authored in the builder) must be reverted by the next
// code->scene Build. The hand-fed unit test (PlanExecutorInstanceOverrideTests.
// Execute_RevertAddedComponent_RemovesAddedComponent) is a false pass: it feeds a well-formed
// "{owner}/{FullName}#{ordinal}" ComponentLogicalId the real read->diff->execute pipeline never
// produces (PrefabInstanceProbe.ReadAddedComponents never sets Component.LogicalId, so
// InstanceOverrideDiff.EmitAddedComponents emits RevertAddedComponent.ComponentLogicalId=="", and
// the executor's StripOrdinal("")=="" match against AddedComponentsOn(...) never resolves the live
// component). This test goes through the real SceneBuilderBuild.Run pipeline so it exercises that
// path end to end.
public class SceneAddedComponentRevertTests
{
    private const string Dir = "Assets/GateTests/Fixtures_SceneAddedComponentRevert";
    private const string ScenePath = "Assets/GateTests/__SceneAddedComponentRevertTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class SceneAddedComponentRevertGateScene : ISceneDefinition
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
        var name = "SceneAddedComponentRevertGate_" + Guid.NewGuid().ToString("N");
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

    // Adds a live Rigidbody component to the fixture's Tank.prefab ROOT so a declared root-level
    // `.Override(e => e.Set((Rigidbody x) => x.mass, v))` has a real component/field to materialize
    // onto (mirrors TypedInstanceOverrideSyncTests.AddRigidbodyToTankRoot).
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

    [Test]
    public void RevertsUndeclaredSceneAddedComponent()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source("        scene.Instance(Prefabs.Tank);"));
        var scene = EditorSceneManager.GetActiveScene();
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        var root = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(root, "Setup: Tank instance root was not created");

        // A LIVE, un-authored added-component override — the scene-only override the desired model
        // (still a bare `scene.Instance(Prefabs.Tank);`) never declares.
        root.AddComponent<Light>();
        Assert.IsTrue((PrefabUtility.GetAddedComponents(root) ?? new List<UnityAddedComponent>())
            .Any(a => a.instanceComponent is Light),
            "Setup: manually added Light was not recorded as an added-component override");

        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();

        // Rebuild from the SAME (unchanged) builder source — a real code->scene rebuild whose
        // desired model does not declare the Light — must revert the live scene-added override.
        var rebuildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(rebuildResult.Diagnostics,
            "Rebuild reverting the undeclared added component reported diagnostics: "
            + string.Join("; ", rebuildResult.Diagnostics.Select(d => d.Message)));

        var rootAfter = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(rootAfter, "Tank instance root disappeared across rebuild");
        Assert.IsNull(rootAfter.GetComponent<Light>(),
            "Rebuild did not revert the undeclared scene-added Light component.");
        Assert.IsFalse((PrefabUtility.GetAddedComponents(rootAfter) ?? new List<UnityAddedComponent>())
            .Any(a => a.instanceComponent is Light),
            "The Light still shows as a live added-component override after rebuild.");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(rootAfter).ToString(),
            "GlobalObjectId changed — instance was re-instantiated instead of reverted in place.");
    }

    [Test]
    public void MixedPass_DeclaredOverrideStays_UndeclaredAddedReverts()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        AddRigidbodyToTankRoot(handles);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source("        scene.Instance(Prefabs.Tank);"));
        var scene = EditorSceneManager.GetActiveScene();
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        // Declare a root-level property override in the same builder that will govern the rebuild.
        File.WriteAllText(_builderPath, Source(
            "        scene.Instance(Prefabs.Tank)\n" +
            "            .Override(e => e.Set((UnityEngine.Rigidbody x) => x.mass, 7f));"));
        var overrideBuild = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(overrideBuild.Diagnostics,
            "Build authoring the declared Rigidbody override reported diagnostics: "
            + string.Join("; ", overrideBuild.Diagnostics.Select(d => d.Message)));

        var root = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(root, "Setup: Tank instance root was not created");
        Assert.AreEqual(7f, root.GetComponent<Rigidbody>().mass, 0.0001f,
            "Setup: the declared Rigidbody override did not materialize m_Mass=7");

        // A LIVE, un-authored added-component override alongside the declared property override.
        root.AddComponent<Light>();
        Assert.IsTrue((PrefabUtility.GetAddedComponents(root) ?? new List<UnityAddedComponent>())
            .Any(a => a.instanceComponent is Light),
            "Setup: manually added Light was not recorded as an added-component override");

        // Rebuild from the SAME (unchanged) builder source — the declared override must stay bold,
        // the undeclared added Light must be reverted, in the same pass.
        var rebuildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(rebuildResult.Diagnostics,
            "Mixed-pass rebuild reported diagnostics: "
            + string.Join("; ", rebuildResult.Diagnostics.Select(d => d.Message)));

        var rootAfter = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(rootAfter, "Tank instance root disappeared across rebuild");
        Assert.IsNull(rootAfter.GetComponent<Light>(),
            "Mixed-pass rebuild did not revert the undeclared scene-added Light component.");
        Assert.AreEqual(7f, rootAfter.GetComponent<Rigidbody>().mass, 0.0001f,
            "Mixed-pass rebuild disturbed the declared Rigidbody override — it must stay bold.");
        Assert.IsTrue((PrefabUtility.GetPropertyModifications(rootAfter) ?? Array.Empty<PropertyModification>())
            .Any(m => m.propertyPath == "m_Mass"),
            "Declared m_Mass override was not recorded as a bold override after the mixed-pass rebuild.");
    }
}
