using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Serialization;

// Gate for GlobalObjectIdCache + ChangeScopedSnapshot (spec checklist #6, the perf gate): a
// per-edit snapshot assemble must resolve GlobalObjectId proportional to the CHANGE SET, not the
// whole scene, and the assembled snapshot must stay byte-equivalent to a cold
// SceneSnapshotReader.Read for the same scene state. Real scene, real GameObjects, real
// GlobalObjectId — the Unity boundary these bugs escape through.
public class AutoIdentityTests
{
    private const string ScenePath = "Assets/GateTests/__AutoIdentityTemp.unity";

    [TearDown]
    public void TearDown()
    {
        if (System.IO.File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private static void SaveActiveScene()
    {
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
    }

    [Test]
    public void Identity_ColdSnapshot_ByteEqualsExistingReader()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var a = new GameObject("A");
        var b = new GameObject("B");
        new GameObject("Child").transform.SetParent(a.transform);
        a.AddComponent<Rigidbody>();
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var css = new ChangeScopedSnapshot();

        var cold = css.AssembleCold(scene, SceneRefResolver.None);
        var expected = SceneSnapshotReader.Read(scene, resolveSceneRef: null);

        Assert.AreEqual(
            CanonicalJson.Serialize(expected),
            CanonicalJson.Serialize(cold),
            "AssembleCold must produce the same snapshot as the existing cold SceneSnapshotReader.Read.");
    }

    [Test]
    public void Identity_BatchWarm_UsesBatchOverload_AndResolvesAllObjects()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        const int count = 220;
        for (var i = 0; i < count; i++)
        {
            new GameObject($"GO_{i}");
        }
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var css = new ChangeScopedSnapshot();

        css.AssembleCold(scene, SceneRefResolver.None);

        Assert.AreEqual(count, css.Ids.ResolutionCount,
            "Cold assemble of a scene with no prior state must resolve every GameObject exactly once.");
        Assert.IsTrue(css.Ids.LastWarmUsedBatch,
            "6000.5.3f1 has the batch GetGlobalObjectIdsSlow overload; cold warm must use it, not the per-object fallback.");
    }

    [Test]
    public void Identity_SingleFieldEdit_ResolutionCountProportionalToChangeSet()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        const int count = 50;
        GameObject target = null;
        for (var i = 0; i < count; i++)
        {
            var go = new GameObject($"GO_{i}");
            if (i == 25)
            {
                target = go;
            }
        }
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var css = new ChangeScopedSnapshot();
        css.AssembleCold(scene, SceneRefResolver.None);
        css.Ids.ResetCount();

        target.tag = "Player";
        SaveActiveScene();

        css.AssembleIncremental(scene, new[] { target.GetEntityId() }, SceneRefResolver.None);

        Assert.AreEqual(1, css.Ids.ResolutionCount,
            "A single-object change must resolve exactly 1 GlobalObjectId (the owning GameObject), " +
            "not the whole 50-object scene — this is the O(changed) perf gate.");
    }

    [Test]
    public void Identity_IncrementalSnapshot_ByteEqualsColdRead()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        const int count = 10;
        GameObject target = null;
        for (var i = 0; i < count; i++)
        {
            var go = new GameObject($"GO_{i}");
            if (i == 3)
            {
                target = go;
            }
        }
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var css = new ChangeScopedSnapshot();
        css.AssembleCold(scene, SceneRefResolver.None);

        target.tag = "Player";
        SaveActiveScene();

        var incremental = css.AssembleIncremental(scene, new[] { target.GetEntityId() }, SceneRefResolver.None);
        var freshCold = SceneSnapshotReader.Read(scene, resolveSceneRef: null);

        Assert.AreEqual(
            CanonicalJson.Serialize(freshCold),
            CanonicalJson.Serialize(incremental),
            "An incremental assemble after a single-field edit must be byte-equal to a fresh cold read.");
    }

    [Test]
    public void Identity_Cache_Invalidate_ForcesReResolve()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var go = new GameObject("Solo");
        SaveActiveScene();

        var cache = new GlobalObjectIdCache();

        var first = cache.Resolve(go);
        Assert.AreEqual(1, cache.ResolutionCount, "First Resolve of an uncached object must be a miss.");

        var second = cache.Resolve(go);
        Assert.AreEqual(first, second, "Resolve must be stable for the same object.");
        Assert.AreEqual(1, cache.ResolutionCount, "A second Resolve of the same object must be a cache hit (no increment).");

        cache.Invalidate(go.GetEntityId());
        var third = cache.Resolve(go);

        Assert.AreEqual(first, third, "The GlobalObjectId itself is unchanged by invalidation.");
        Assert.AreEqual(2, cache.ResolutionCount, "Resolve after Invalidate must re-resolve (count increments).");
    }

    // D3: an incremental assemble must not serve a cached node computed under a stale IdentityMap.
    // The generation SceneRefResolver.ForMap carries is the cache's invalidation key.
    [Test]
    public void Identity_IncrementalAfterIdentityMapChange_ByteEqualsColdRead()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var openerGo = new GameObject("Opener");
        var doorGo = new GameObject("Door");
        var bystanderGo = new GameObject("Bystander");
        var doorOpener = openerGo.AddComponent<DoorOpener>();
        doorOpener.target = doorGo;
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var css = new ChangeScopedSnapshot();

        var mapWithoutDoor = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry
                {
                    LogicalId = "Opener", Kind = "GameObject",
                    GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(openerGo).ToString(),
                },
            },
        };
        css.AssembleCold(scene, SceneRefResolver.ForMap(mapWithoutDoor));

        var mapWithDoor = new IdentityMap
        {
            Entries = new[]
            {
                mapWithoutDoor.Entries[0],
                new IdentityMapEntry
                {
                    LogicalId = "Door", Kind = "GameObject",
                    GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(doorGo).ToString(),
                },
            },
        };
        var sceneRefWithDoor = SceneRefResolver.ForMap(mapWithDoor);

        // Bystander (NOT Opener/Door) is the changed id, so the cache is exercised across the map
        // change without the changed-id path forcing a re-read of Opener's own node by itself.
        var incremental = css.AssembleIncremental(scene, new[] { bystanderGo.GetEntityId() }, sceneRefWithDoor);
        var freshCold = SceneSnapshotReader.Read(scene, sceneRefWithDoor.Resolve);

        Assert.AreEqual(
            CanonicalJson.Serialize(freshCold),
            CanonicalJson.Serialize(incremental),
            "An incremental assemble after the IdentityMap gained a mapped target must be byte-equal to a " +
            "fresh cold read under the new map — a cached node must not keep resolving the target's raw " +
            "GlobalObjectId once the map covers it.");
    }

    // The perf clause alongside the invalidation above: two SEPARATE IdentityMap instances with
    // EQUAL mapped-node content (the real auto-sync situation — a fresh deserialize per cycle) must
    // NOT force a cold assemble; an incremental assemble must still resolve proportional to the
    // change set.
    [Test]
    public void Identity_EqualContentMapAcrossAssembles_StaysIncremental()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        const int count = 50;
        GameObject target = null;
        for (var i = 0; i < count; i++)
        {
            var go = new GameObject($"GO_{i}");
            if (i == 25)
            {
                target = go;
            }
        }
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var css = new ChangeScopedSnapshot();

        var mapA = new IdentityMap();
        var mapB = new IdentityMap();

        css.AssembleCold(scene, SceneRefResolver.ForMap(mapA));
        css.Ids.ResetCount();

        target.tag = "Player";
        SaveActiveScene();

        css.AssembleIncremental(scene, new[] { target.GetEntityId() }, SceneRefResolver.ForMap(mapB));

        Assert.AreEqual(1, css.Ids.ResolutionCount,
            "Two IdentityMap instances with equal (empty) mapped-node content must produce the same " +
            "generation, so a single-object change still resolves exactly 1 GlobalObjectId, not the whole " +
            $"{count}-object scene.");
    }

    // SceneRefResolver.None (no identity map: scene refs read Unsupported) and
    // SceneRefResolver.ForMap(new IdentityMap()) (an empty but present map: scene refs read their raw
    // GlobalObjectId) answer differently — a cache built under one must not be served to the other.
    [Test]
    public void Identity_NoneAndEmptyMap_AreDifferentGenerations()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var openerGo = new GameObject("Opener");
        var doorGo = new GameObject("Door");
        var bystanderGo = new GameObject("Bystander");
        var doorOpener = openerGo.AddComponent<DoorOpener>();
        doorOpener.target = doorGo;
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var css = new ChangeScopedSnapshot();

        css.AssembleCold(scene, SceneRefResolver.None);

        var emptyMapResolver = SceneRefResolver.ForMap(new IdentityMap());
        var incremental = css.AssembleIncremental(scene, new[] { bystanderGo.GetEntityId() }, emptyMapResolver);
        var freshCold = SceneSnapshotReader.Read(scene, emptyMapResolver.Resolve);

        Assert.AreEqual(
            CanonicalJson.Serialize(freshCold),
            CanonicalJson.Serialize(incremental),
            "An incremental assemble switching from SceneRefResolver.None to ForMap(empty map) must be " +
            "byte-equal to a fresh cold read under the empty-map resolver — None and an empty map answer " +
            "scene-ref reads differently (Unsupported vs. raw GlobalObjectId), so serving a None-built " +
            "cache to a ForMap(empty) assemble is the same defect in a second dress.");
    }
}
