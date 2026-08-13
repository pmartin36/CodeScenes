using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Serialization;

// Pins PrefabBuildSyncTarget.Build: the single prefab-writing entry that mints a fresh .prefab on a
// CREATE build, edits the loaded contents IN PLACE on an UPDATE build (an unchanged sibling keeps its
// local fileID / GlobalObjectId across the regeneration), stamps prefab-internal identity anchored to
// the prefab asset's own GUID, refuses a multi-root builder before writing anything, and refuses a
// builder whose single root's name does not match the .prefab filename stem.
public class PrefabBuildTargetTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_PrefabBuild";
    private const string PrefabPath = FixturesDir + "/Widget.prefab";
    private const string MaterialPath = FixturesDir + "/PBT_Red.mat";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
public class PrefabBuildTargetScene : IPrefabDefinition
{{
    public void Build(PrefabRoot root)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_pbt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "PrefabBuildTargetScene.cs");
        _sidecarPath = Path.Combine(_dir, "PrefabBuildTargetScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_PrefabBuild");
        }

        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), MaterialPath);
        AssetDatabase.SaveAssets();
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

        AssetDatabase.DeleteAsset(MaterialPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }
    }

    [Test]
    public void Create_ProducesPrefabWithAuthoredHierarchy()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\", w =>\n" +
            "        {\n" +
            "            w.Transform(pos: (1f, 2f, 3f));\n" +
            "            w.Component<UnityEngine.BoxCollider>(c => c.Set(\"m_Size\", new Vector3(2f, 2f, 2f)));\n" +
            "            w.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + MaterialPath + "\") }));\n" +
            "            w.Add(\"Child\");\n" +
            "        });"));

        var result = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);

        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for a valid single-root prefab builder: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var contents = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        Assert.IsNotNull(contents, "No .prefab was written at " + PrefabPath);
        Assert.AreEqual("Widget", contents.name, "Root name did not match the authored builder");
        Assert.AreEqual(new Vector3(1f, 2f, 3f), contents.transform.localPosition,
            "Root transform position did not match the authored builder");

        var collider = contents.GetComponent<BoxCollider>();
        Assert.IsNotNull(collider, "Authored BoxCollider was not materialized on the root");
        Assert.AreEqual(new Vector3(2f, 2f, 2f), collider.size, "Authored collider size was not set");

        var renderer = contents.GetComponent<MeshRenderer>();
        Assert.IsNotNull(renderer, "Authored MeshRenderer was not materialized on the root");
        Assert.AreEqual("PBT_Red", renderer.sharedMaterial.name,
            "Authored asset-ref material did not resolve onto the renderer");

        var child = contents.transform.Find("Child");
        Assert.IsNotNull(child, "Authored child 'Child' was not materialized under the root");
    }

    [Test]
    public void Idempotent_SecondBuildZeroPlanOps()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\", w => { w.Transform(pos: (1f, 0f, 0f)); w.Add(\"Child\"); });"));

        var first = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(first.Diagnostics, "First (create) build reported diagnostics");
        Assert.Greater(first.PlanOpCount, 0, "First build against a fresh prefab produced zero plan ops");

        var second = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(second.Diagnostics, "Second (unchanged) build reported diagnostics");
        Assert.AreEqual(0, second.PlanOpCount,
            "An unchanged second build against the same source is not idempotent (PlanOpCount != 0)");
    }

    [Test]
    public void EditInPlace_UnchangedSiblingFileIdStable()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\", w =>\n" +
            "        {\n" +
            "            w.Add(\"A\");\n" +
            "            w.Add(\"B\");\n" +
            "        });"));

        var first = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(first.Diagnostics, "Create build reported diagnostics");

        var rootBefore = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        var bBefore = rootBefore.transform.Find("B").gameObject;
        Assert.IsTrue(
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(bBefore, out _, out long bFileIdBefore),
            "Could not resolve sibling B's local fileID after the create build");

        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\", w =>\n" +
            "        {\n" +
            "            w.Add(\"A\");\n" +
            "            w.Add(\"B\");\n" +
            "            w.Add(\"C\");\n" +
            "        });"));

        var second = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(second.Diagnostics, "Update build (adding a sibling) reported diagnostics");

        var rootAfter = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        var bAfter = rootAfter.transform.Find("B").gameObject;
        var cAfter = rootAfter.transform.Find("C").gameObject;
        Assert.IsTrue(
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(bAfter, out _, out long bFileIdAfter),
            "Could not resolve sibling B's local fileID after the update build");
        Assert.IsTrue(
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(cAfter, out _, out long cFileId),
            "Could not resolve new child C's local fileID after the update build");

        Assert.AreEqual(bFileIdBefore, bFileIdAfter,
            "Unchanged sibling B's local fileID changed across a structural regeneration (adding a "
            + "child) — the prefab was rebuilt fresh instead of edited in place");
        Assert.AreNotEqual(0L, cFileId, "New child C got a zero local fileID");
        Assert.AreNotEqual(bFileIdAfter, cFileId, "New child C reused sibling B's local fileID");
    }

    [Test]
    public void IdentityStamp_AnchoredToPrefabAsset()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\", w => { w.Add(\"Child\"); });"));

        var result = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(result.Diagnostics, "Build reported diagnostics");

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var rootEntry = map.Entries.SingleOrDefault(e => e.Name == "Widget" && e.Kind == "GameObject");
        Assert.IsNotNull(rootEntry,
            "Sidecar has no entry for the prefab root 'Widget'.\n" + File.ReadAllText(_sidecarPath));

        Assert.IsTrue(GlobalObjectId.TryParse(rootEntry.GlobalObjectId, out var goid),
            "Root entry's GlobalObjectId did not parse: " + rootEntry.GlobalObjectId);
        Assert.AreEqual(AssetDatabase.AssetPathToGUID(PrefabPath), goid.assetGUID.ToString(),
            "Root entry's GlobalObjectId assetGUID did not match the prefab's own AssetDatabase GUID");
        Assert.AreNotEqual(0UL, goid.targetObjectId,
            "Root entry's GlobalObjectId carries a zero targetObjectId — it does not anchor a real object");
    }

    [Test]
    public void RootNameMismatch_Refused()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Other\", w => { w.Add(\"Child\"); });"));

        var result = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);

        Assert.IsNotEmpty(result.Diagnostics,
            "A root name ('Other') that does not match the .prefab filename stem ('Widget') was not refused");
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "SB2402"),
            "Refusal diagnostics did not include the root-name-vs-stem code SB2402: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.IsFalse(File.Exists(_sidecarPath), "Sidecar was written despite the root-name-mismatch refusal");
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath),
            "A .prefab was written despite the root-name-mismatch refusal");
    }

    [Test]
    public void SidecarIdentity_StableAcrossCreateThenUpdate()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\", w =>\n" +
            "        {\n" +
            "            w.Add(\"A\");\n" +
            "            w.Add(\"B\");\n" +
            "        });"));

        var first = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(first.Diagnostics, "Create build reported diagnostics");

        var mapAfterCreate = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var bEntryCreate = mapAfterCreate.Entries.SingleOrDefault(e => e.Name == "B" && e.Kind == "GameObject");
        Assert.IsNotNull(bEntryCreate, "Sidecar after the create build has no entry for sibling 'B'");

        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\", w =>\n" +
            "        {\n" +
            "            w.Add(\"A\");\n" +
            "            w.Add(\"B\");\n" +
            "            w.Add(\"C\");\n" +
            "        });"));

        var second = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(second.Diagnostics, "Update build (adding a sibling) reported diagnostics");

        var mapAfterUpdate = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var bEntryUpdate = mapAfterUpdate.Entries.SingleOrDefault(e => e.Name == "B" && e.Kind == "GameObject");
        Assert.IsNotNull(bEntryUpdate, "Sidecar after the update build has no entry for sibling 'B'");

        Assert.AreEqual(bEntryCreate.GlobalObjectId, bEntryUpdate.GlobalObjectId,
            "Sibling B's sidecar GlobalObjectId changed between the CREATE build and the following "
            + "UPDATE build");
    }

    [Test]
    public void MultiRootPrefab_Refused()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\");\n        root.Add(\"SecondRoot\");"));

        var result = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);

        Assert.IsNotEmpty(result.Diagnostics, "A two-root prefab builder was not refused");
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "SB2401"),
            "Refusal diagnostics did not include the single-root code SB2401: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.IsFalse(File.Exists(_sidecarPath), "Sidecar was written despite the multi-root refusal");
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath),
            "A .prefab was written despite the multi-root refusal");
    }
}
