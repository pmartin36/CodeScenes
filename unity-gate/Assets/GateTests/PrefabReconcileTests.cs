using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Editor;

// Pins the prefab reconcile direction (prefab->code): PrefabBuildSyncTarget.Sync routes an edit made
// to the loaded prefab contents through the shared structural-matching identity model, patching the
// builder source. Covers the whole symmetric operation set — field edit, add, remove, rename,
// reparent, and reorder — each simulating a Prefab-Mode edit via LoadPrefabContents -> mutate ->
// SaveAsPrefabAsset -> UnloadPrefabContents, then re-importing so the change is visible to the
// following read. Rename/reparent/reorder apply to a CHILD, never the root: the root's name is
// pinned to the .prefab filename stem and cannot reconcile.
public class PrefabReconcileTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_PrefabReconcile";
    private const string PrefabPath = FixturesDir + "/Widget.prefab";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class PrefabReconcileScene : IPrefabDefinition
{{
    public void Build(PrefabRoot root)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_prt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "PrefabReconcileScene.cs");
        _sidecarPath = Path.Combine(_dir, "PrefabReconcileScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_PrefabReconcile");
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

    // Simulates a Prefab-Mode edit: load the persisted contents, mutate them in place, save back onto
    // the same asset path, unload, then re-import so a following read/build sees the change.
    private static void ApplyPrefabModeEdit(Action<GameObject> mutate)
    {
        var contents = PrefabUtility.LoadPrefabContents(PrefabPath);
        mutate(contents);
        PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
        PrefabUtility.UnloadPrefabContents(contents);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceUpdate);
    }

    [Test]
    public void Reconcile_FieldEdit_PatchesSource()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\", w => { w.Add(\"A\").Transform(pos: (1f, 0f, 0f)); });"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Create build reported diagnostics");

        ApplyPrefabModeEdit(contents =>
        {
            var a = contents.transform.Find("A");
            a.localPosition = new Vector3(5f, 0f, 0f);
        });

        var sync = PrefabBuildSyncTarget.Sync(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a field edit made in Prefab Mode");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("5f, 0f, 0f", rewritten,
            "Builder source did not pick up the Prefab-Mode field edit.\n" + rewritten);
    }

    [Test]
    public void Reconcile_AddChild_PatchesSource()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\", w => { w.Add(\"A\"); });"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Create build reported diagnostics");

        ApplyPrefabModeEdit(contents =>
        {
            var b = new GameObject("B");
            b.transform.SetParent(contents.transform);
        });

        var sync = PrefabBuildSyncTarget.Sync(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a child added in Prefab Mode");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Add(\"B\")", rewritten,
            "Builder source did not gain an Add(\"B\") statement for the Prefab-Mode-created child.\n" + rewritten);
    }

    [Test]
    public void Reconcile_RemoveChild_PatchesSource()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\", w => { w.Add(\"A\"); w.Add(\"B\"); });"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Create build reported diagnostics");

        ApplyPrefabModeEdit(contents =>
        {
            var b = contents.transform.Find("B").gameObject;
            UnityEngine.Object.DestroyImmediate(b);
        });

        var sync = PrefabBuildSyncTarget.Sync(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a child removed in Prefab Mode");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("Add(\"B\")", rewritten,
            "Builder source still references the Prefab-Mode-deleted child.\n" + rewritten);
        StringAssert.Contains("Add(\"A\")", rewritten,
            "Builder source lost the surviving child.\n" + rewritten);
    }

    [Test]
    public void Reconcile_RenameChild_PatchesSource()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\", w => { w.Add(\"Original\"); });"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Create build reported diagnostics");

        ApplyPrefabModeEdit(contents =>
        {
            contents.transform.Find("Original").gameObject.name = "Renamed";
        });

        var sync = PrefabBuildSyncTarget.Sync(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a child renamed in Prefab Mode");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Add(\"Renamed\")", rewritten,
            "Builder source did not pick up the Prefab-Mode rename.\n" + rewritten);
        StringAssert.DoesNotContain("Add(\"Original\")", rewritten,
            "Builder source still carries the child's old name.\n" + rewritten);
    }

    [Test]
    public void Reconcile_ReparentChild_PatchesSource()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\", w => { var alpha = w.Add(\"Alpha\"); w.Add(\"Beta\"); });"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Create build reported diagnostics");

        ApplyPrefabModeEdit(contents =>
        {
            var alpha = contents.transform.Find("Alpha");
            var beta = contents.transform.Find("Beta");
            beta.SetParent(alpha);
        });

        var sync = PrefabBuildSyncTarget.Sync(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a reparent made in Prefab Mode");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("alpha.Add(\"Beta\")", rewritten,
            "Builder source did not reparent Beta under Alpha's handle.\n" + rewritten);
    }

    [Test]
    public void Reconcile_ReorderChildren_PatchesSource()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"Widget\", w => { w.Add(\"A\"); w.Add(\"B\"); });"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Create build reported diagnostics");

        ApplyPrefabModeEdit(contents =>
        {
            contents.transform.Find("B").SetSiblingIndex(0);
        });

        var sync = PrefabBuildSyncTarget.Sync(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a sibling reorder made in Prefab Mode");

        var rewritten = File.ReadAllText(_builderPath);
        var indexOfA = rewritten.IndexOf("Add(\"A\")", StringComparison.Ordinal);
        var indexOfB = rewritten.IndexOf("Add(\"B\")", StringComparison.Ordinal);
        Assert.IsTrue(indexOfA >= 0 && indexOfB >= 0, "Builder source lost a child statement.\n" + rewritten);
        Assert.Less(indexOfB, indexOfA,
            "Builder source statement order did not follow the Prefab-Mode sibling reorder (B before A).\n" + rewritten);
    }
}
