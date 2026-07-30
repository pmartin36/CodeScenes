using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Reconcile;
using SceneBuilder.Core.Serialization;

// m-ui-recttransform b3-t4: this file covers a live UI prefab INSTANCE ROOT dropped into the
// scene. The instance-root create path (ReconcilerInstances.cs's HandleInstanceNode) is the THIRD
// site that builds a create AppendStatement, routed through Reconciler.SplitCreatedPayload with
// `canHostRectTransformCall: false` (InstanceHandle has no `.RectTransform(...)` member), the
// same driven-mask + D1 X/Y hold the plain-Add (ReconcilerAppends.cs) and AddChild
// (ReconcilerInstances.Nested.cs) create sites apply.
//
// Deliberately a ROOT-level instance (`scene.Instance(...)`, not nested under a NodeHandle
// parent): HandleInstanceNode does not look at its parent, and a NodeHandle-typed
// `.Instance(Prefabs.X)` overload does not exist (SceneRoot.Instance<TRef> does; NodeHandle.Instance
// does not) -- orthogonal to what this file covers.
public class RoundTripUiPrefabInstanceTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_UiPrefabInstance";
    private const string PrefabPath = FixturesDir + "/UiPanel.prefab";
    private const string ScenePath = "Assets/GateTests/__RoundTripUiPrefabInstanceTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private const string EmptySource = @"
using SceneBuilder.Authoring;
public class UiPrefabInstanceScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
    }
}
";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtupi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "UiPrefabInstanceScene.cs");
        _sidecarPath = Path.Combine(_dir, "UiPrefabInstanceScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_UiPrefabInstance");
        }

        // Fixture: a UI prefab with a non-default RectTransform layout.
        var source = new GameObject("UiPanel", typeof(RectTransform), typeof(Image));
        var sourceRect = source.GetComponent<RectTransform>();
        sourceRect.anchoredPosition = new Vector2(10f, 10f);
        sourceRect.sizeDelta = new Vector2(100f, 30f);
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

    // The empty-source build + Assets[] seed steps every case in this file needs, factored so the
    // three cases do not each clone ~20 lines of setup. Returns the freshly-instantiated prefab
    // instance's GameObject.
    private GameObject BuildEmptySourceAndInstantiateFixture()
    {
        File.WriteAllText(_builderPath, EmptySource);
        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        // Seed the sidecar's Assets[] with the prefab's GUID -- the documented adapter
        // responsibility (ReconcilerInstances.cs:64-67 / prefabPathByGuid is fed ONLY from
        // identityMap.Assets), done here directly rather than via an authored `.Instance(...)`
        // statement so this test's ONLY appended `.Instance(` occurrence is the one under test.
        var prefabGuid = AssetDatabase.AssetPathToGUID(PrefabPath);
        var seededMap = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        seededMap = seededMap with
        {
            Assets = seededMap.Assets.Append(new AssetEntry { Guid = prefabGuid, LastKnownPath = PrefabPath, TypeHint = "Prefab" }).ToArray(),
        };
        File.WriteAllText(_sidecarPath, IdentityMapJson.Serialize(seededMap));

        var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        return (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset, EditorSceneManager.GetActiveScene());
    }

    [Test]
    public void SceneToCode_UiPrefabInstanceCreatedAtRoot_AppendsInstanceWithoutPosOrRectTransform_SecondSyncNoOp()
    {
        var instance = BuildEmptySourceAndInstantiateFixture();

        // Drop a live root-level instance of the UI prefab, with a layout distinct from the prefab
        // asset's own default (proves the read is of the LIVE value, not a baked default).
        var instanceRect = instance.GetComponent<RectTransform>();
        instanceRect.anchoredPosition = new Vector2(50f, -30f);

        // PREMISE: the live object IS a RectTransform with the value just set, and its DERIVED
        // localPosition is non-zero -- or this test would pass vacuously.
        Assert.IsNotNull(instanceRect, "Setup: instantiated UI prefab has no RectTransform");
        Assert.AreEqual(new Vector2(50f, -30f), instanceRect.anchoredPosition, "Setup: anchoredPosition did not take");
        Assert.AreNotEqual(0f, instance.transform.localPosition.x, "Setup premise: derived localPosition.x must be non-zero");
        Assert.AreNotEqual(0f, instance.transform.localPosition.y, "Setup premise: derived localPosition.y must be non-zero");

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        var syncResult = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(syncResult.Changed, "Sync reported no change despite a new UI prefab instance");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".Instance(", rewritten,
            "Builder source did not gain an .Instance(...) statement for the new UI prefab instance.\n" + rewritten);
        // Either the typed `Prefabs.UiPanel` form or a string literal path names this fixture --
        // which form is chosen is orthogonal to this case.
        StringAssert.Contains("UiPanel", rewritten,
            "Appended .Instance(...) does not reference the fixture prefab.\n" + rewritten);

        // D1: the instance's derived m_LocalPosition must never reach source as `pos:`, and
        // InstanceHandle has no `.RectTransform(...)` member to carry the layout either.
        StringAssert.DoesNotContain(".Transform(", rewritten,
            "The UI prefab instance's derived position leaked into source as `.Transform(pos:...)`.\n" + rewritten);
        StringAssert.DoesNotContain(".RectTransform(", rewritten,
            "InstanceHandle has no `.RectTransform(...)` member -- this would not compile.\n" + rewritten);

        // Second sync must be a no-op: the appended statement stays byte-identical.
        var beforeSecondSync = File.ReadAllText(_builderPath);
        var secondSyncResult = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, secondSyncResult.PatchEdits, "A second Sync with no live change produced non-zero PatchEdits.");
        Assert.AreEqual(beforeSecondSync, File.ReadAllText(_builderPath), "A second Sync with no live change rewrote the file.");
    }

    // A mapped UI prefab instance whose layout is untouched (still exactly its prefab asset's own
    // layout) has nothing unpersisted -- the asset itself already carries it -- so neither the
    // create-path sync nor a later matched-path sync ever reports the unlocalizable-layout conflict.
    [Test]
    public void SceneToCode_UiPrefabInstance_LayoutUntouched_NeverReportsUnlocalizableLayout()
    {
        var instance = BuildEmptySourceAndInstantiateFixture();
        var instanceRect = instance.GetComponent<RectTransform>();

        // PREMISE: the live layout IS the asset's own default (nothing touched it) -- or this test
        // would pass vacuously by accidentally exercising the divergent case instead.
        Assert.AreEqual(new Vector2(10f, 10f), instanceRect.anchoredPosition, "Setup premise: anchoredPosition must equal the prefab asset's own value");
        Assert.AreEqual(new Vector2(100f, 30f), instanceRect.sizeDelta, "Setup premise: sizeDelta must equal the prefab asset's own value");

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        var syncResult1 = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(
            syncResult1.Conflicts.Any(c => c.Kind == ConflictKind.UnlocalizableRectTransform),
            "Create-path sync reported an unlocalizable-layout conflict for a layout matching the prefab asset.\n"
                + string.Join("; ", syncResult1.Conflicts.Select(c => c.Reason)));

        // Now mapped: a second sync goes through the MATCHED path. Still untouched, still quiet.
        var beforeSecondSync = File.ReadAllText(_builderPath);
        var syncResult2 = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(
            syncResult2.Conflicts.Any(c => c.Kind == ConflictKind.UnlocalizableRectTransform),
            "Matched-path sync reported an unlocalizable-layout conflict for a layout matching the prefab asset.\n"
                + string.Join("; ", syncResult2.Conflicts.Select(c => c.Reason)));
        Assert.AreEqual(0, syncResult2.PatchEdits, "Matched-path sync with no live change produced non-zero PatchEdits.");
        Assert.AreEqual(beforeSecondSync, File.ReadAllText(_builderPath), "Matched-path sync with no live change rewrote the file.");
    }

    // Once mapped, a genuine divergence from the prefab asset's own layout still reports -- exactly
    // ONE located conflict -- and writes no source (InstanceHandle has no `.RectTransform(...)`
    // member).
    [Test]
    public void SceneToCode_UiPrefabInstance_LayoutDivergesFromAsset_ReportsOneLocatedConflict_AndWritesNoSource()
    {
        var instance = BuildEmptySourceAndInstantiateFixture();
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());

        // First sync: create path, maps the instance (layout untouched at this point).
        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        // Now diverge the live layout from the prefab asset's own (10,10).
        var instanceRect = instance.GetComponent<RectTransform>();
        instanceRect.anchoredPosition = new Vector2(200f, -80f);

        // PREMISE: the value took and the derived local position actually moved.
        Assert.AreEqual(new Vector2(200f, -80f), instanceRect.anchoredPosition, "Setup: anchoredPosition did not take");
        Assert.AreNotEqual(0f, instance.transform.localPosition.x, "Setup premise: derived localPosition.x must be non-zero");

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        var beforeSecondSync = File.ReadAllText(_builderPath);
        var syncResult2 = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var conflict = syncResult2.Conflicts.Single(c => c.Kind == ConflictKind.UnlocalizableRectTransform);
        Assert.IsNotEmpty(conflict.LogicalId, "Conflict did not name a LogicalId.");
        StringAssert.Contains("anchoredPos", conflict.Reason);

        var afterSecondSync = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain(".RectTransform(", afterSecondSync);
        StringAssert.DoesNotContain(".Transform(", afterSecondSync);
        Assert.AreEqual(beforeSecondSync, afterSecondSync, "The genuinely-diverged layout was written to source.");
        Assert.AreEqual(0, syncResult2.PatchEdits, "The genuinely-diverged layout produced a PatchArgument edit.");
    }
}
