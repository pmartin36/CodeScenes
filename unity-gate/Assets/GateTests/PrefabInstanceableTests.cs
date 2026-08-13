using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Serialization;

// Pins the M6 self-registration self-registers seam: PrefabBuildSyncTarget.Build folds the prefab's
// OWN AssetEntry (Guid = AssetDatabase.AssetPathToGUID, TypeHint = "Prefab") into the sidecar
// Assets[] on save, so the same shape M6's AssetReferenceResolver already harvests for GUID
// move-recovery is present without a scene ever referencing the prefab.
public class PrefabInstanceableTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_PrefabInstanceable";
    private const string PrefabPath = FixturesDir + "/Widget.prefab";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source() => $@"
using SceneBuilder.Authoring;
public class PrefabInstanceableScene : IPrefabDefinition
{{
    public void Build(PrefabRoot root)
    {{
        root.Add(""Widget"");
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_pit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "PrefabInstanceableScene.cs");
        _sidecarPath = Path.Combine(_dir, "PrefabInstanceableScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_PrefabInstanceable");
        }
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
        {
            AssetDatabase.DeleteAsset(PrefabPath);
        }

        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }
    }

    [Test]
    public void SelfRegistered_InSidecarAssets()
    {
        File.WriteAllText(_builderPath, Source());

        var first = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(first.Diagnostics, "Create build reported diagnostics");

        var expectedGuid = AssetDatabase.AssetPathToGUID(PrefabPath);
        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var selfEntry = map.Assets.FirstOrDefault(a => a.Guid == expectedGuid);

        Assert.IsNotNull(selfEntry,
            "Sidecar Assets[] has no self-registered entry for the prefab's own GUID after Build.\n"
            + File.ReadAllText(_sidecarPath));
        Assert.AreEqual("Prefab", selfEntry.TypeHint,
            "Self-registered entry's TypeHint was not \"Prefab\".");

        var second = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(second.Diagnostics, "Unchanged second build reported diagnostics");

        var mapAfterSecond = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        Assert.AreEqual(1, mapAfterSecond.Assets.Count(a => a.Guid == expectedGuid),
            "Self-registration was duplicated by a second, unchanged build.");
    }
}
