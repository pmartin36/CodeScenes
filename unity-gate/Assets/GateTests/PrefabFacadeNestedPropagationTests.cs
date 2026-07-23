using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Validation;

// b6-t5 (specs/25-typed-prefab-facades.md): FIX-4 — editing a NESTED prefab (Turret) must
// propagate to the composing PARENT's façade entry (Tank's LeftTurret/RightTurret), not just to
// Turret's own entry. Against the canonical fixture (b6-t1). No production code: the whole-catalog
// Rebuild in PrefabFacadeManifestGenerator (composes by reference across the full cache) is already
// wired; this is the required live-boundary proof. Per research.md's REFINED verdict, "compiles"
// against the new nested accessor is asserted at the RESOLUTION boundary (zero SB2203 diagnostics
// from a live SceneBuilderBuild.Run), not via BuilderCompileCheck — the façade types are emitted
// only into the real project csproj compilation, never into the editor's AppDomain-only check.
public class PrefabFacadeNestedPropagationTests
{
    private const string Dir = "Assets/GateTests/Fixtures_PrefabFacadeNestedPropagation";
    private const string ScenePath = "Assets/GateTests/__PrefabFacadeNestedPropagationTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class PrefabFacadeNestedPropagationGateScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _manifestPath = SceneBuilderPaths.FacadeManifestPath;
        _manifestExistedBefore = File.Exists(_manifestPath);
        _manifestBytesBefore = _manifestExistedBefore ? File.ReadAllBytes(_manifestPath) : null;

        var buildersDir = SceneBuilderPaths.EnsureBuildersDirectory();
        var name = "PrefabFacadeNestedPropagationGate_" + Guid.NewGuid().ToString("N");
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

    private static FacadeCatalog ReadManifest() =>
        FacadeCatalog.Deserialize(File.ReadAllText(SceneBuilderPaths.FacadeManifestPath));

    private static FacadeChild RequireChild(FacadeNode node, string realName)
    {
        var match = node.Children.FirstOrDefault(c => c.RealName == realName);
        Assert.IsNotNull(match, $"No child named '{realName}' among [{string.Join(",", node.Children.Select(c => c.RealName))}]");
        return match;
    }

    private static bool HasChild(FacadeNode node, string realName) =>
        node.Children.Any(c => c.RealName == realName);

    // Adds a "Sight" child inside the Turret NESTED prefab, via a real
    // LoadPrefabContents/SaveAsPrefabAsset/ImportAsset cycle — firing the live
    // AssetPostprocessor -> ReReadInto(Turret) -> whole-catalog Rebuild chain (FIX-4).
    private static void AddSightToTurret(PrefabFacadeFixture.Handles handles)
    {
        var contents = PrefabUtility.LoadPrefabContents(handles.TurretPath);
        var sight = new GameObject("Sight");
        sight.transform.SetParent(contents.transform);
        PrefabUtility.SaveAsPrefabAsset(contents, handles.TurretPath);
        PrefabUtility.UnloadPrefabContents(contents);

        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(handles.TurretPath, ImportAssetOptions.ForceUpdate);
    }

    [Test]
    public void NestedEdit_AddChildInTurret_PropagatesToComposingTankFacade()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        var before = ReadManifest();
        Assert.IsTrue(before.TryGetEntryByGuid(handles.TurretGuid, out var turretBefore));
        Assert.IsFalse(HasChild(turretBefore.Root, "Sight"), "Setup: Turret must not already have a 'Sight' child.");
        Assert.IsTrue(before.TryGetEntryByGuid(handles.TankGuid, out var tankBefore));
        var leftBefore = RequireChild(tankBefore.Root, "LeftTurret");
        Assert.IsFalse(HasChild(leftBefore.Node, "Sight"), "Setup: Tank's LeftTurret must not already have a 'Sight' child.");

        AddSightToTurret(handles);

        var after = ReadManifest();

        Assert.IsTrue(after.TryGetEntryByGuid(handles.TurretGuid, out var turretAfter), "No FacadeEntry for the Turret prefab after the nested edit.");
        RequireChild(turretAfter.Root, "Sight");

        Assert.IsTrue(after.TryGetEntryByGuid(handles.TankGuid, out var tankAfter), "No FacadeEntry for the Tank prefab (composing parent was not re-imported).");
        var left = RequireChild(tankAfter.Root, "LeftTurret");
        var right = RequireChild(tankAfter.Root, "RightTurret");
        Assert.AreEqual(turretAfter.Root.TypeName, left.Node.TypeName,
            "LeftTurret must still be composed BY REFERENCE to the (now-edited) Turret entry's own node.");
        Assert.AreEqual(turretAfter.Root.TypeName, right.Node.TypeName,
            "RightTurret must still be composed BY REFERENCE to the (now-edited) Turret entry's own node.");
        RequireChild(left.Node, "Sight");
        RequireChild(right.Node, "Sight");
    }

    [Test]
    public void NestedEdit_BuilderAddressingNewNestedAccessor_ResolvesThroughComposedFacade()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        var body = "        scene.Instance(Prefabs.Tank).On(t => t.LeftTurret.Sight, s => { });";
        File.WriteAllText(_builderPath, Source(body));

        var sceneBefore = EditorSceneManager.GetActiveScene();
        var preEditResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, sceneBefore);
        var preEditDiagnostic = preEditResult.Diagnostics.FirstOrDefault(d => d.Code == DiagnosticCodes.UnknownFacadeReference);
        Assert.IsNotNull(preEditDiagnostic,
            "Setup: before the nested edit, 't.LeftTurret.Sight' must not resolve — expected a located "
            + DiagnosticCodes.UnknownFacadeReference + " diagnostic.\n"
            + string.Join("; ", preEditResult.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        AddSightToTurret(handles);

        var sceneAfter = EditorSceneManager.GetActiveScene();
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, sceneAfter);

        Assert.IsEmpty(result.Diagnostics,
            "Build after the nested-prefab edit must resolve 't.LeftTurret.Sight' with zero diagnostics: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var root = EditorSceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(go => go.name == "Tank");
        Assert.IsNotNull(root, "Tank instance root was not created after the nested accessor resolved.");
    }
}
