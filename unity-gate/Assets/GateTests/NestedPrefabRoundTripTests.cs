using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Editor;

// The reconcile (prefab->code) and materialize (code->prefab) round trip for a nested prefab
// instance living inside a prefab ASSET (as opposed to a scene) -- edited in Prefab Mode via
// LoadPrefabContents/SaveAsPrefabAsset, reconciled through the SAME PrefabBuildSyncTarget.Sync seam
// PrefabReconcileTests exercises for a plain child. Covers the whole symmetric operation set on the
// nested instance node itself: field edit, add/remove component, add/remove child, reparent, rename,
// and the reverse code->prefab direction.
public class NestedPrefabRoundTripTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_NestedPrefabRoundTrip";
    private const string BPrefabPath = FixturesDir + "/NRT_B.prefab";
    private const string APrefabPath = FixturesDir + "/NRT_A.prefab";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class NestedPrefabRoundTripScene : IPrefabDefinition
{{
    public void Build(PrefabRoot root)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_nrt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "NestedPrefabRoundTripScene.cs");
        _sidecarPath = Path.Combine(_dir, "NestedPrefabRoundTripScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_NestedPrefabRoundTrip");
        }

        var bRoot = new GameObject("NRT_B");
        var light = bRoot.AddComponent<Light>();
        light.intensity = 1f;
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

    // Simulates a Prefab-Mode edit on the OUTER prefab asset A: load its persisted contents, mutate
    // them, save back onto the same path, unload, then re-import so a following Sync sees the change.
    private static void ApplyPrefabModeEdit(Action<GameObject> mutate)
    {
        var contents = PrefabUtility.LoadPrefabContents(APrefabPath);
        mutate(contents);
        PrefabUtility.SaveAsPrefabAsset(contents, APrefabPath);
        PrefabUtility.UnloadPrefabContents(contents);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(APrefabPath, ImportAssetOptions.ForceUpdate);
    }

    [Test]
    public void Reconcile_FieldEditOnNestedInstance_PatchesSourceAndRoundTrips()
    {
        File.WriteAllText(_builderPath, Source(
            $"        root.Add(\"NRT_A\", w => {{ w.Instance(\"{BPrefabPath}\"); }});"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Create build reported diagnostics");

        ApplyPrefabModeEdit(contents =>
        {
            var b = contents.transform.Find("NRT_B");
            var light = b.GetComponent<Light>();
            light.intensity = 7f;
            PrefabUtility.RecordPrefabInstancePropertyModifications(light);
        });

        var sync = PrefabBuildSyncTarget.Sync(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a field edit on the nested instance");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".Override(", rewritten,
            "Builder source did not gain an .Override(...) call for the nested instance field edit.\n" + rewritten);
        StringAssert.Contains("m_Intensity", rewritten,
            "Builder source's override does not target m_Intensity.\n" + rewritten);

        var rebuild = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(rebuild.Diagnostics, "Rebuild from the reconciled source reported diagnostics");
        var rebuiltB = AssetDatabase.LoadAssetAtPath<GameObject>(APrefabPath).transform.Find("NRT_B");
        Assert.AreEqual(7f, rebuiltB.GetComponent<Light>().intensity, 0.0001f,
            "Rebuilding from the reconciled source did not materialize the synced field value");
    }

    [Test]
    public void Reconcile_AddComponentOnNestedInstance_PatchesSource()
    {
        File.WriteAllText(_builderPath, Source(
            $"        root.Add(\"NRT_A\", w => {{ w.Instance(\"{BPrefabPath}\"); }});"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Create build reported diagnostics");

        ApplyPrefabModeEdit(contents =>
        {
            var b = contents.transform.Find("NRT_B").gameObject;
            b.AddComponent<BoxCollider>();
        });

        var sync = PrefabBuildSyncTarget.Sync(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a component added on the nested instance");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".AddComponent<UnityEngine.BoxCollider>(", rewritten,
            "Builder source did not gain an .AddComponent<BoxCollider>() call for the nested instance.\n" + rewritten);
    }

    [Test]
    public void Reconcile_RemoveComponentOnNestedInstance_PatchesSource()
    {
        File.WriteAllText(_builderPath, Source(
            $"        root.Add(\"NRT_A\", w => {{ w.Instance(\"{BPrefabPath}\"); }});"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Create build reported diagnostics");

        ApplyPrefabModeEdit(contents =>
        {
            var b = contents.transform.Find("NRT_B").gameObject;
            UnityEngine.Object.DestroyImmediate(b.GetComponent<Light>());
        });

        var sync = PrefabBuildSyncTarget.Sync(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a component removed on the nested instance");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".RemoveComponent<UnityEngine.Light>(", rewritten,
            "Builder source did not gain a .RemoveComponent<Light>() call for the nested instance.\n" + rewritten);
    }

    [Test]
    public void Reconcile_AddChildUnderNestedInstance_PatchesSource()
    {
        File.WriteAllText(_builderPath, Source(
            $"        root.Add(\"NRT_A\", w => {{ w.Instance(\"{BPrefabPath}\"); }});"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Create build reported diagnostics");

        ApplyPrefabModeEdit(contents =>
        {
            var b = contents.transform.Find("NRT_B");
            var muzzle = new GameObject("Muzzle");
            muzzle.transform.SetParent(b);
        });

        var sync = PrefabBuildSyncTarget.Sync(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a child added under the nested instance");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("\"Muzzle\"", rewritten,
            "Builder source did not gain a child statement for the object added under the nested instance.\n" + rewritten);
    }

    [Test]
    public void Reconcile_RemoveChildUnderNestedInstance_DropsFromSource()
    {
        File.WriteAllText(_builderPath, Source(
            $"        root.Add(\"NRT_A\", w => {{ w.Instance(\"{BPrefabPath}\"); }});"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Create build reported diagnostics");

        // First capture the add (mirrors Reconcile_AddChildUnderNestedInstance_PatchesSource) so the
        // child this test removes has a genuine source anchor to drop, coming from a live reconcile
        // rather than a hand-authored AddChild statement.
        ApplyPrefabModeEdit(contents =>
        {
            var b = contents.transform.Find("NRT_B");
            var muzzle = new GameObject("Muzzle");
            muzzle.transform.SetParent(b);
        });

        var addSync = PrefabBuildSyncTarget.Sync(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsTrue(addSync.Changed, "Setup: sync did not capture the added child");
        StringAssert.Contains("\"Muzzle\"", File.ReadAllText(_builderPath),
            "Setup: the added child was not captured into source before this test removes it");

        ApplyPrefabModeEdit(contents =>
        {
            var muzzle = contents.transform.Find("NRT_B/Muzzle").gameObject;
            UnityEngine.Object.DestroyImmediate(muzzle);
        });

        var sync = PrefabBuildSyncTarget.Sync(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a child removed under the nested instance");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("\"Muzzle\"", rewritten,
            "Builder source still references the child removed under the nested instance.\n" + rewritten);
    }

    [Test]
    public void Reconcile_ReparentNestedInstance_PatchesSourceUnderNewParentHandle()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"NRT_A\", w =>\n" +
            "        {\n" +
            "            var host = w.Add(\"Host\");\n" +
            $"            w.Instance(\"{BPrefabPath}\");\n" +
            "        });"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Create build reported diagnostics");

        ApplyPrefabModeEdit(contents =>
        {
            var host = contents.transform.Find("Host");
            var b = contents.transform.Find("NRT_B");
            b.SetParent(host);
        });

        var sync = PrefabBuildSyncTarget.Sync(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite the nested instance being reparented");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("host.Instance(", rewritten,
            "Builder source did not reparent the nested instance under the 'host' handle.\n" + rewritten);
    }

    // A nested instance's name has no independent authoring representation -- it is always emitted
    // as `Instance(<path>)`, and that statement's first argument IS the path. A live rename must not
    // rewrite that argument.
    [Test]
    public void Reconcile_RenameNestedInstance_LeavesSourceUnchanged_SurfacesConflict_BuildStillCompiles()
    {
        File.WriteAllText(_builderPath, Source(
            $"        root.Add(\"NRT_A\", w => {{ w.Instance(\"{BPrefabPath}\"); }});"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Create build reported diagnostics");

        var beforeSync = File.ReadAllText(_builderPath);

        ApplyPrefabModeEdit(contents =>
        {
            contents.transform.Find("NRT_B").gameObject.name = "Renamed";
        });

        var sync = PrefabBuildSyncTarget.Sync(_builderPath, APrefabPath, _sidecarPath);

        var afterSync = File.ReadAllText(_builderPath);
        Assert.AreEqual(beforeSync, afterSync,
            "A rename of the nested instance node corrupted the builder source -- an Instance(...) "
            + "statement's argument is the source PATH, not a name, and must never be rewritten.\n" + afterSync);

        Assert.IsTrue(sync.Conflicts.Any(c => c.Kind == SceneBuilder.Core.Reconcile.ConflictKind.UnrepresentableInstanceRename),
            "Sync did not surface a located Conflict for the unrepresentable nested-instance rename.");

        var rebuild = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(rebuild.Diagnostics,
            "A rebuild from the source after the rename attempt reported diagnostics -- the corrupted "
            + "Instance(...) argument no longer resolves to a real asset.\n"
            + string.Join("; ", rebuild.Diagnostics.Select(d => d.Message)));
    }

    [Test]
    public void Build_AuthoredOverrideOnNestedInstance_MaterializesOntoReloadedAsset()
    {
        // A brand-new instance's creation emits no override-apply ops of its own (Differ.EmitCreateInstance),
        // so the override must be authored on a SECOND build against an already-materialized instance
        // to exercise the override-apply path at all.
        File.WriteAllText(_builderPath, Source(
            $"        root.Add(\"NRT_A\", w => {{ w.Instance(\"{BPrefabPath}\"); }});"));
        var setup = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(setup.Diagnostics, "Setup build reported diagnostics");

        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"NRT_A\", w =>\n" +
            "        {\n" +
            $"            w.Instance(\"{BPrefabPath}\").Override(e => e.Set((UnityEngine.Light l) => l.intensity, 7f));\n" +
            "        });"));

        var build = PrefabBuildSyncTarget.Build(_builderPath, APrefabPath, _sidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Build reported diagnostics");

        var aAsset = AssetDatabase.LoadAssetAtPath<GameObject>(APrefabPath);
        var nested = aAsset.transform.Find("NRT_B");
        Assert.IsNotNull(nested, "No nested 'NRT_B' child on the reloaded A.prefab");

        var light = nested.GetComponent<Light>();
        Assert.AreEqual(7f, light.intensity, 0.0001f,
            "The authored .Override(...) on the nested instance did not materialize onto the reloaded asset");

        var mods = PrefabUtility.GetPropertyModifications(nested.gameObject);
        Assert.IsTrue(mods != null && mods.Any(m => m.propertyPath == "m_Intensity"),
            "GetPropertyModifications on the nested instance carries no m_Intensity override");
    }
}
