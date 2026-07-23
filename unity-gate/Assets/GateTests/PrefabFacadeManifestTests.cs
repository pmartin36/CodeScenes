using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// b6-t2 (specs/25-typed-prefab-facades.md): the manifest-generator Unity boundary — real File IO
// against SceneBuilderPaths.FacadeManifestPath, driven by the live AssetPostprocessor hook and the
// canonical Tank/Turret fixture (b6-t1). Covers: manifest structure (nested composition + duplicate
// siblings + sanitization), byte-stable no-op regen, no Generated/*.cs written, add-child regen,
// DELETE-prefab regen (dropped entry + decoupled nested reference), and MOVE-prefab regen (GUID
// stable). The <AdditionalFiles> injection contract is already fully covered by
// BuilderProjectInjectorTests/AnalyzerToolkitInjectionTests (b5-t3) — not re-asserted here. The
// generated-type COMPILE contract (Prefabs.Tank / stale-ref failure) is covered headlessly by
// FacadeGeneratorTests against the byte-identical shipped generator (b3-t1/t2/t3) — infeasible to
// drive in-editor (see research.md b6-t2 verdict).
public class PrefabFacadeManifestTests
{
    private const string Dir = "Assets/GateTests/Fixtures_PrefabFacadeManifest";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    [SetUp]
    public void SetUp()
    {
        _manifestPath = SceneBuilderPaths.FacadeManifestPath;
        _manifestExistedBefore = File.Exists(_manifestPath);
        _manifestBytesBefore = _manifestExistedBefore ? File.ReadAllBytes(_manifestPath) : null;
    }

    [TearDown]
    public void TearDown()
    {
        PrefabFacadeFixture.Delete(Dir);

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

    [Test]
    public void Regenerate_AgainstCanonicalTank_WritesFacadeManifest_WithNestedAndDuplicateSiblingStructure()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        Assert.IsTrue(File.Exists(SceneBuilderPaths.FacadeManifestPath), "Regenerate must write the facade manifest.");

        var catalog = ReadManifest();

        Assert.IsTrue(catalog.TryGetEntryByGuid(handles.TankGuid, out var tankEntry), "No FacadeEntry for the Tank prefab.");
        Assert.IsTrue(catalog.TryGetEntryByGuid(handles.TurretGuid, out var turretEntry), "No FacadeEntry for the Turret prefab.");

        var left = RequireChild(tankEntry.Root, "LeftTurret");
        var right = RequireChild(tankEntry.Root, "RightTurret");
        Assert.AreEqual(turretEntry.Root.TypeName, left.Node.TypeName,
            "LeftTurret must be composed BY REFERENCE to the Turret entry's own node (same TypeName).");
        Assert.AreEqual(turretEntry.Root.TypeName, right.Node.TypeName,
            "RightTurret must be composed BY REFERENCE to the Turret entry's own node (same TypeName).");
        RequireChild(left.Node, "Barrel");
        RequireChild(right.Node, "Barrel");

        var chassis = RequireChild(tankEntry.Root, "Chassis");
        var wheels = chassis.Node.Children.Where(c => c.RealName == "Wheel").ToArray();
        Assert.AreEqual(2, wheels.Length, "Chassis must have exactly two 'Wheel' siblings.");
        Assert.AreNotEqual(wheels[0].LocalId, wheels[1].LocalId, "The two Wheel siblings must have distinct durable ids.");
        CollectionAssert.AreEquivalent(new[] { "Wheel", "Wheel2" }, wheels.Select(w => w.PropertyName).ToArray(),
            "Duplicate 'Wheel' siblings must be disambiguated Wheel/Wheel2.");

        var frontWheel = RequireChild(chassis.Node, "Front Wheel");
        Assert.AreEqual("FrontWheel", frontWheel.PropertyName, "'Front Wheel' must sanitize to 'FrontWheel' (word-boundary rule).");
    }

    [Test]
    public void Regenerate_SecondRun_IsByteStable_AndReportsNoWrite()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();
        var bytesAfterFirstRun = File.ReadAllBytes(SceneBuilderPaths.FacadeManifestPath);

        var changed = PrefabFacadeManifestGenerator.Regenerate();

        Assert.IsFalse(changed, "A second Regenerate with no asset change must report no write.");
        Assert.AreEqual(bytesAfterFirstRun, File.ReadAllBytes(SceneBuilderPaths.FacadeManifestPath),
            "The manifest bytes must be byte-identical across a no-op regen.");
    }

    [Test]
    public void Generated_ContainsNoCsFiles_AndManifestIsOutsideAssets()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        var csFiles = Directory.GetFiles(SceneBuilderPaths.GeneratedDirectory, "*.cs", SearchOption.TopDirectoryOnly);
        Assert.IsEmpty(csFiles, "The manifest write must never emit a .cs file into Generated/.");

        var manifestFull = Path.GetFullPath(SceneBuilderPaths.FacadeManifestPath);
        var assetsPrefix = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
        Assert.IsFalse(manifestFull.StartsWith(assetsPrefix, System.StringComparison.Ordinal),
            "The facade manifest must live outside Assets/ — a write there would cost a domain reload.");
    }

    [Test]
    public void AddChildUnderTank_RegeneratesManifest_ExposesNewAccessor()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        // Unity disallows re-parenting under a transform loaded straight off the asset
        // (AssetDatabase.LoadAssetAtPath) — that mutation must go through an edit scope.
        var contents = PrefabUtility.LoadPrefabContents(handles.TankPath);
        var turbo = new GameObject("Turbo");
        turbo.transform.SetParent(contents.transform);
        PrefabUtility.SaveAsPrefabAsset(contents, handles.TankPath);
        PrefabUtility.UnloadPrefabContents(contents);

        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(handles.TankPath, ImportAssetOptions.ForceUpdate);

        var catalog = ReadManifest();
        Assert.IsTrue(catalog.TryGetEntryByGuid(handles.TankGuid, out var tankEntry), "No FacadeEntry for the Tank prefab after add-child.");
        RequireChild(tankEntry.Root, "Turbo");
    }

    [Test]
    public void DeleteTurretPrefab_DropsItsFacadeEntry()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        // Deleting Turret.prefab while Tank.prefab still nests two instances of it makes Unity emit
        // an expected Error-level "Missing Prefab" reimport log for Tank.prefab — not a test failure.
        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            AssetDatabase.DeleteAsset(handles.TurretPath);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = prevIgnore;
        }

        var catalog = ReadManifest();
        Assert.IsFalse(catalog.TryGetEntryByGuid(handles.TurretGuid, out _), "The Turret FacadeEntry must be dropped after its asset is deleted.");

        Assert.IsTrue(catalog.TryGetEntryByGuid(handles.TankGuid, out var tankEntry), "Tank entry must still exist.");
        // Unity appends a "(Missing Prefab with guid: ...)" suffix to a broken nested instance's own
        // name on reimport — match by prefix rather than the exact pre-delete RealName.
        var left = tankEntry.Root.Children.FirstOrDefault(c => c.RealName.StartsWith("LeftTurret", System.StringComparison.Ordinal));
        Assert.IsNotNull(left, "No child whose RealName starts with 'LeftTurret' after the Turret asset was deleted.");
        Assert.IsEmpty(left.Node.Children, "LeftTurret must no longer resolve to the deleted Turret entry's children.");
    }

    [Test]
    public void MoveTankPrefab_RegeneratesWithStableGuid()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        const string MovedFolder = Dir + "/MovedFolder";
        AssetDatabase.CreateFolder(Dir, "MovedFolder");
        var movedPath = MovedFolder + "/Tank.prefab";

        try
        {
            var moveError = AssetDatabase.MoveAsset(handles.TankPath, movedPath);
            Assert.IsTrue(string.IsNullOrEmpty(moveError), $"MoveAsset failed: {moveError}");

            var catalog = ReadManifest();
            Assert.IsTrue(catalog.TryGetEntryByGuid(handles.TankGuid, out var tankEntry),
                "The Tank FacadeEntry must survive a move, keyed on the SAME (stable) Guid.");
            Assert.AreEqual("Tank", tankEntry.PropertyName, "A pure move (no rename) must not change the PropertyName.");
        }
        finally
        {
            AssetDatabase.DeleteAsset(movedPath);
            AssetDatabase.DeleteAsset(MovedFolder);
        }
    }
}
