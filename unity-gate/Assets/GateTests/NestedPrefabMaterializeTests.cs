using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Serialization;

// A nested instance authored inside a PrefabRoot body (`root.Add("A", w => w.Instance(bPath))`)
// materializes through PrefabBuildSyncTarget.Build — the single prefab-writing target, no fork — as
// a genuine CONNECTED prefab instance (never flattened GameObjects), with its identity stamped from
// persisted state after the save, and both A's own objects and B's internal objects keeping their
// fileIDs across a structural edit-in-place regeneration.
public class NestedPrefabMaterializeTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_NestedPrefabMaterialize";
    private const string BPrefabPath = FixturesDir + "/NPM_B.prefab";
    private const string APrefabPath = FixturesDir + "/NPM_A.prefab";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class NestedPrefabMaterializeScene : IPrefabDefinition
{{
    public void Build(PrefabRoot root)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_npm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "NestedPrefabMaterializeScene.cs");
        _sidecarPath = Path.Combine(_dir, "NestedPrefabMaterializeScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_NestedPrefabMaterialize");
        }

        var bRoot = new GameObject("NPM_B");
        var bChild = new GameObject("BChild");
        bChild.transform.SetParent(bRoot.transform);
        PrefabUtility.SaveAsPrefabAsset(bRoot, BPrefabPath);
        UnityEngine.Object.DestroyImmediate(bRoot);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (AssetDatabase.LoadAssetAtPath<GameObject>(APrefabPath) != null)
        {
            AssetDatabase.DeleteAsset(APrefabPath);
        }

        AssetDatabase.DeleteAsset(BPrefabPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }
    }

    // INVARIANT C: the nested child is a real connected instance of B, not flattened GameObjects —
    // its own internal descendant ("BChild") is present via the prefab connection.
    [Test]
    public void Nested_Build_ProducesGenuineConnectedInstance()
    {
        File.WriteAllText(_builderPath, Source(
            $"        root.Add(\"NPM_A\", w => {{ w.Instance(\"{BPrefabPath}\"); }});"));

        var result = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var aAsset = AssetDatabase.LoadAssetAtPath<GameObject>(APrefabPath);
        Assert.IsNotNull(aAsset, "No A.prefab was written at " + APrefabPath);

        var nestedChild = aAsset.transform.Find("NPM_B");
        Assert.IsNotNull(nestedChild, "A.prefab has no nested 'NPM_B' child");

        Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(nestedChild.gameObject),
            "Nested child is not part of a prefab instance — it was flattened instead of connected");

        var source = PrefabUtility.GetCorrespondingObjectFromSource(nestedChild.gameObject);
        Assert.IsNotNull(source, "Nested child has no corresponding source object");
        var sourcePath = AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromOriginalSource(nestedChild.gameObject));
        Assert.AreEqual(BPrefabPath, sourcePath,
            "Nested child's connected source does not resolve to B.prefab");

        var bInternalChild = nestedChild.Find("BChild");
        Assert.IsNotNull(bInternalChild,
            "B's own internal child 'BChild' is missing from the nested instance — the connection did not carry B's hierarchy");
    }

    // INVARIANT P: the nested instance's stamped identity is read off PERSISTED state (non-zero,
    // resolvable), never an unsaved in-contents instance.
    [Test]
    public void Nested_Instance_Identity_StampedNonZero_FromPersistedState()
    {
        File.WriteAllText(_builderPath, Source(
            $"        root.Add(\"NPM_A\", w => {{ w.Instance(\"{BPrefabPath}\"); }});"));

        var result = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(result.Diagnostics, "Build reported diagnostics");

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var entry = map.Entries.SingleOrDefault(e => e.Kind == "PrefabInstance");
        Assert.IsNotNull(entry, "Sidecar has no Kind=PrefabInstance entry.\n" + File.ReadAllText(_sidecarPath));

        Assert.IsTrue(GlobalObjectId.TryParse(entry.GlobalObjectId, out var goid),
            "Nested instance entry's GlobalObjectId did not parse: " + entry.GlobalObjectId);
        Assert.AreNotEqual(0UL, goid.targetObjectId,
            "Nested instance's stamped GlobalObjectId carries a zero targetObjectId — it does not anchor persisted state");
        Assert.AreEqual(AssetDatabase.AssetPathToGUID(APrefabPath), goid.assetGUID.ToString(),
            "Nested instance's stamped GlobalObjectId is not anchored to A.prefab's own GUID");

        var reopened = PrefabUtility.LoadPrefabContents(APrefabPath);
        try
        {
            var resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(goid);
            Assert.IsNotNull(resolved, "Stamped GlobalObjectId does not resolve back to a live object");
            Assert.AreEqual("NPM_B", resolved.name, "Stamped GlobalObjectId resolved to the wrong object");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(reopened);
        }
    }

    // A structural edit-in-place (adding a sibling) keeps BOTH A's own unchanged object AND the
    // nested B instance's internal unchanged object byte-stable (fileID-stable).
    [Test]
    public void EditInPlace_AtDepth_KeepsAOwnAndNestedBInternalFileIds()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"NPM_A\", w =>\n" +
            "        {\n" +
            "            w.Add(\"Keep\");\n" +
            $"            w.Instance(\"{BPrefabPath}\");\n" +
            "        });"));

        var first = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(first.Diagnostics, "Create build reported diagnostics");

        var aBefore = AssetDatabase.LoadAssetAtPath<GameObject>(APrefabPath);
        var keepBefore = aBefore.transform.Find("Keep");
        var bInternalBefore = aBefore.transform.Find("NPM_B/BChild");
        Assert.IsNotNull(keepBefore, "'Keep' missing after create build");
        Assert.IsNotNull(bInternalBefore, "Nested B's internal 'BChild' missing after create build");

        Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
            keepBefore.gameObject, out _, out long keepFileIdBefore),
            "Could not resolve 'Keep' local fileID before the update build");
        Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
            bInternalBefore.gameObject, out _, out long bInternalFileIdBefore),
            "Could not resolve nested B's internal 'BChild' local fileID before the update build");

        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"NPM_A\", w =>\n" +
            "        {\n" +
            "            w.Add(\"Keep\");\n" +
            $"            w.Instance(\"{BPrefabPath}\");\n" +
            "            w.Add(\"New\");\n" +
            "        });"));

        var second = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(second.Diagnostics, "Update build (adding a sibling) reported diagnostics");

        var aAfter = AssetDatabase.LoadAssetAtPath<GameObject>(APrefabPath);
        var keepAfter = aAfter.transform.Find("Keep");
        var bInternalAfter = aAfter.transform.Find("NPM_B/BChild");
        var newSibling = aAfter.transform.Find("New");
        Assert.IsNotNull(keepAfter, "'Keep' missing after update build");
        Assert.IsNotNull(bInternalAfter, "Nested B's internal 'BChild' missing after update build");
        Assert.IsNotNull(newSibling, "'New' sibling was not added by the update build");

        Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
            keepAfter.gameObject, out _, out long keepFileIdAfter),
            "Could not resolve 'Keep' local fileID after the update build");
        Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
            bInternalAfter.gameObject, out _, out long bInternalFileIdAfter),
            "Could not resolve nested B's internal 'BChild' local fileID after the update build");

        Assert.AreEqual(keepFileIdBefore, keepFileIdAfter,
            "A's own unchanged object 'Keep' lost its fileID across a structural regeneration");
        Assert.AreEqual(bInternalFileIdBefore, bInternalFileIdAfter,
            "Nested B instance's internal unchanged object 'BChild' lost its fileID across a structural regeneration");
    }
}
