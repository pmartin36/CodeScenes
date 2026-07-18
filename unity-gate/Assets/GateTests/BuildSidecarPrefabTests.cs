using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Serialization;

// b5-t3: build-side (code->scene) sidecar write path for prefab instances. After
// SceneBuilderBuild.Run on a builder authoring scene.Instance(...), the written sidecar must carry
// Kind="PrefabInstance" per instance, a nonzero PrefabKey (TargetPrefabId/TargetObjectId) split from
// the instantiated root's GlobalObjectId, a SourcePrefabGuid == the prefab's AssetDatabase GUID, and
// an Assets[] entry with TypeHint="Prefab" for the harvested source GUID. Two same-prefab instances
// share SourcePrefabGuid with distinct TargetObjectId. A plain scene.Add sibling stays byte-stable
// (Kind="GameObject", null PrefabKey/SourcePrefabGuid) — guards the IdentityRemapper flattening
// regression this task also fixes.
public class BuildSidecarPrefabTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_M6BuildSidecar";
    private const string PrefabPath = FixturesDir + "/M6_Enemy.prefab";
    private const string ScenePath = "Assets/GateTests/__BuildSidecarPrefabTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class BuildSidecarPrefabScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_bsp_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "BuildSidecarPrefabScene.cs");
        _sidecarPath = Path.Combine(_dir, "BuildSidecarPrefabScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_M6BuildSidecar");
        }

        var source = new UnityEngine.GameObject("M6_Enemy_Source");
        PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
        UnityEngine.Object.DestroyImmediate(source);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }

        AssetDatabase.DeleteAsset(PrefabPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }
    }

    [Test]
    public void Run_SceneInstance_SidecarEntryHasPrefabInstanceKindKeyAndSourceGuid()
    {
        File.WriteAllText(_builderPath, Source($"        scene.Instance(\"{PrefabPath}\");"));

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for a valid scene.Instance(...): "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var entry = map.Entries.SingleOrDefault(e => e.Kind == "PrefabInstance");
        Assert.IsNotNull(entry, "Sidecar has no Kind=PrefabInstance entry.\n" + File.ReadAllText(_sidecarPath));

        Assert.IsNotNull(entry.PrefabKey, "PrefabInstance entry has no PrefabKey");
        Assert.AreNotEqual(0UL, entry.PrefabKey.TargetPrefabId, "PrefabKey.TargetPrefabId is zero");
        Assert.AreNotEqual(0UL, entry.PrefabKey.TargetObjectId, "PrefabKey.TargetObjectId is zero");

        var expectedGuid = AssetDatabase.AssetPathToGUID(PrefabPath);
        Assert.AreEqual(expectedGuid, entry.SourcePrefabGuid, "SourcePrefabGuid did not match the prefab's GUID");

        var assetEntry = map.Assets.SingleOrDefault(a => a.Guid == expectedGuid);
        Assert.IsNotNull(assetEntry, "Sidecar Assets[] has no entry for the source prefab's GUID");
        Assert.AreEqual("Prefab", assetEntry.TypeHint, "Harvested prefab asset entry has the wrong TypeHint");
    }

    [Test]
    public void Run_TwoInstancesOfSamePrefab_ShareSourceGuidAndTargetObjectIdWithDistinctTargetPrefabId()
    {
        File.WriteAllText(_builderPath, Source(
            $"        scene.Instance(\"{PrefabPath}\");\n" +
            $"        scene.Instance(\"{PrefabPath}\");"));

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for two scene.Instance(...) of the same prefab: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var instanceEntries = map.Entries.Where(e => e.Kind == "PrefabInstance").ToList();
        Assert.AreEqual(2, instanceEntries.Count,
            "Expected exactly two PrefabInstance entries.\n" + File.ReadAllText(_sidecarPath));

        var expectedGuid = AssetDatabase.AssetPathToGUID(PrefabPath);
        Assert.IsTrue(instanceEntries.All(e => e.SourcePrefabGuid == expectedGuid),
            "Both instances must share the same SourcePrefabGuid");

        // GlobalObjectId semantics: targetObjectId is the SOURCE object's local file id, shared by
        // every instance of the same prefab; targetPrefabId is the per-instance-unique id. The
        // pair-key's distinguishing axis is TargetPrefabId, not TargetObjectId.
        Assert.AreEqual(instanceEntries[0].PrefabKey.TargetObjectId, instanceEntries[1].PrefabKey.TargetObjectId,
            "Two instances of the same prefab must share TargetObjectId (the source object's local file id)");
        Assert.AreNotEqual(instanceEntries[0].PrefabKey.TargetPrefabId, instanceEntries[1].PrefabKey.TargetPrefabId,
            "Two distinct instances must have distinct TargetPrefabId");
    }

    [Test]
    public void Run_PlainGameObjectAlongsideInstance_StaysGameObjectKindWithNullPrefabFields()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Plain\");\n" +
            $"        scene.Instance(\"{PrefabPath}\");"));

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var plainEntry = map.Entries.SingleOrDefault(e => e.Name == "Plain");
        Assert.IsNotNull(plainEntry, "No entry found for the plain GameObject 'Plain'");
        Assert.AreEqual("GameObject", plainEntry.Kind);
        Assert.IsNull(plainEntry.PrefabKey, "Plain GameObject entry must not carry a PrefabKey");
        Assert.IsNull(plainEntry.SourcePrefabGuid, "Plain GameObject entry must not carry a SourcePrefabGuid");
    }
}
