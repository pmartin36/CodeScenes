using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// specs/27-typed-child-selectors.md — the typed `.RemoveChild(sel => sel.A.B)` /
// `.AddChild(sel => sel.Parent, "New")` overloads at the LIVE Unity boundary, against the canonical
// PrefabFacadeFixture (Tank -> LeftTurret{Barrel(Light), Antenna(MeshRenderer)}, RightTurret,
// Chassis). Mirrors PrefabFacadeRenameTests' SceneBuilderBuild.Run + real-rename harness. Proves:
//   (1) a typed RemoveChild resolves through the real facade manifest and removes the live source
//       child (identical scene effect to the string form),
//   (2) a typed-parent AddChild creates the live child under the resolved sub-object,
//   (3) a genuine LeftTurret->Cannon prefab rename auto-syncs the typed selector in the on-disk
//       builder (LeftTurret -> Cannon), exactly like `.On` — the compiler-safety payoff.
public class TypedChildSelectorTests
{
    private const string Dir = "Assets/GateTests/Fixtures_TypedChildSelector";
    private const string ScenePath = "Assets/GateTests/__TypedChildSelectorTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class TypedChildSelectorGateScene : ISceneDefinition
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
        var name = "TypedChildSelectorGate_" + Guid.NewGuid().ToString("N");
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

        if (_manifestExistedBefore) File.WriteAllBytes(_manifestPath, _manifestBytesBefore);
        else if (File.Exists(_manifestPath)) File.Delete(_manifestPath);
    }

    private static void RenameLeftTurretToCannon(PrefabFacadeFixture.Handles handles)
    {
        var contents = PrefabUtility.LoadPrefabContents(handles.TankPath);
        var leftTurret = contents.transform.Find("LeftTurret");
        Assert.IsNotNull(leftTurret, "Setup: fixture Tank prefab has no 'LeftTurret' child.");
        leftTurret.gameObject.name = "Cannon";
        PrefabUtility.SaveAsPrefabAsset(contents, handles.TankPath);
        PrefabUtility.UnloadPrefabContents(contents);

        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(handles.TankPath, ImportAssetOptions.ForceUpdate);
    }

    // The instance-override diff (RemoveInstanceChild/AddInstanceChild) applies only once the
    // instance is MAPPED — a fresh create emits AddInstance+SetTransform only (Differ.EmitCreateInstance),
    // so child overrides converge on the SECOND build, identically for the string and typed forms.
    // Build once to create+map the Tank, then again to apply the typed child override to the live scene.
    private void BuildTwice()
    {
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var second = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(second.Diagnostics,
            "Typed child-selector build reported diagnostics (the typed selector must resolve against the real façade manifest): "
            + string.Join("; ", second.Diagnostics.Select(d => d.Message)));
    }

    [Test]
    public void TypedRemoveChild_RemovesLiveSourceChild_SameAsStringForm()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source(
            "        scene.Instance(Prefabs.Tank).RemoveChild(t => t.LeftTurret.Antenna);"));

        BuildTwice();

        var tank = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(tank, "Tank instance root was not created by SceneBuilderBuild.Run");

        var leftTurret = tank.transform.Find("LeftTurret");
        Assert.IsNotNull(leftTurret, "LeftTurret sub-object missing after build");
        Assert.IsNull(leftTurret.Find("Antenna"),
            "Typed RemoveChild(t => t.LeftTurret.Antenna) did not remove the live Antenna source child");
        Assert.IsNotNull(leftTurret.Find("Barrel"),
            "Typed RemoveChild removed an unrelated sibling (Barrel) — selector resolved to the wrong child");
    }

    [Test]
    public void TypedAddChild_CreatesLiveChild_UnderResolvedParent()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source(
            "        scene.Instance(Prefabs.Tank).AddChild(t => t.LeftTurret, \"MuzzleFlash\");"));

        BuildTwice();

        var tank = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(tank, "Tank instance root was not created by SceneBuilderBuild.Run");

        var leftTurret = tank.transform.Find("LeftTurret");
        Assert.IsNotNull(leftTurret, "LeftTurret sub-object missing after build");
        Assert.IsNotNull(leftTurret.Find("MuzzleFlash"),
            "Typed AddChild(t => t.LeftTurret, \"MuzzleFlash\") did not create the live MuzzleFlash child under the resolved parent");
    }

    [Test]
    public void RenameTurretChild_AutoSyncsTypedRemoveAndAddChildSelectors()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        var original = Source(
            "        scene.Instance(Prefabs.Tank).RemoveChild(t => t.LeftTurret.Antenna);\n" +
            "        scene.Instance(Prefabs.Tank).AddChild(t => t.LeftTurret, \"MuzzleFlash\");\n" +
            "        scene.Instance(Prefabs.Tank).RemoveChild(t => t.RightTurret.Antenna);");
        File.WriteAllText(_builderPath, original);

        RenameLeftTurretToCannon(handles);

        var patched = File.ReadAllText(_builderPath);
        var expected = original
            .Replace("RemoveChild(t => t.LeftTurret.Antenna)", "RemoveChild(t => t.Cannon.Antenna)")
            .Replace("AddChild(t => t.LeftTurret, \"MuzzleFlash\")", "AddChild(t => t.Cannon, \"MuzzleFlash\")");

        Assert.AreEqual(expected, patched,
            "The rename patch must rewrite ONLY the renamed segment in the typed .RemoveChild/.AddChild selectors, leaving the RightTurret chain and the \"MuzzleFlash\" name string unchanged.\n" + patched);
        StringAssert.DoesNotContain("t.LeftTurret", patched, "Stale 't.LeftTurret' accessor survived the rename patch.\n" + patched);
        StringAssert.Contains("\"MuzzleFlash\"", patched, "The new-child NAME string must never be rewritten by the rename patch.\n" + patched);
    }
}
