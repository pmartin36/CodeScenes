using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Serialization;

// Gate for ComponentData.GlobalObjectId: every component in a snapshot carries its OWN
// GlobalObjectId, distinct from its owning GameObject's, and the batch warm that resolves it
// costs one call for the whole cold assemble rather than a slow resolve per component. Real
// scene, real GameObjects, real components, real GlobalObjectId — the Unity boundary this
// contract lives on.
public class ComponentGoidSnapshotTests
{
    private const string ScenePath = "Assets/GateTests/__ComponentGoidSnapshotTemp.unity";

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
    public void Snapshot_EveryComponent_CarriesItsOwnGlobalObjectId()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var go = new GameObject("Anchored");
        var rigidbody = go.AddComponent<Rigidbody>();
        var boxCollider = go.AddComponent<BoxCollider>();
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var snapshot = SceneSnapshotReader.Read(scene, resolveSceneRef: null);
        var node = snapshot.Roots[0];

        var expectedNodeGoid = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
        var expectedRigidbodyGoid = GlobalObjectId.GetGlobalObjectIdSlow(rigidbody).ToString();
        var expectedBoxColliderGoid = GlobalObjectId.GetGlobalObjectIdSlow(boxCollider).ToString();

        Assert.AreEqual(expectedNodeGoid, node.GlobalObjectId);

        var rigidbodyData = System.Array.Find(node.Components, c => c.Type.FullName == "UnityEngine.Rigidbody");
        var boxColliderData = System.Array.Find(node.Components, c => c.Type.FullName == "UnityEngine.BoxCollider");
        Assert.IsNotNull(rigidbodyData, "Rigidbody must appear in the snapshot's component list.");
        Assert.IsNotNull(boxColliderData, "BoxCollider must appear in the snapshot's component list.");

        Assert.AreEqual(expectedRigidbodyGoid, rigidbodyData.GlobalObjectId);
        Assert.AreEqual(expectedBoxColliderGoid, boxColliderData.GlobalObjectId);

        Assert.AreNotEqual(node.GlobalObjectId, rigidbodyData.GlobalObjectId,
            "A component's GlobalObjectId must differ from its owning GameObject's.");
        Assert.AreNotEqual(rigidbodyData.GlobalObjectId, boxColliderData.GlobalObjectId,
            "Two components on the same GameObject must carry distinct GlobalObjectIds.");
    }

    [Test]
    public void ColdWarm_WithComponents_UsesTheBatchOverload_AndResolvesEveryComponent()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var a = new GameObject("A");
        a.AddComponent<Rigidbody>();
        a.AddComponent<BoxCollider>();
        var b = new GameObject("B");
        b.AddComponent<Rigidbody>();
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var css = new ChangeScopedSnapshot();

        css.AssembleCold(scene, SceneRefResolver.None);

        // 2 GameObjects + 3 components (Rigidbody+BoxCollider on A, Rigidbody on B) — Transform is
        // never resolved (ReadNodeShallow skips it).
        Assert.AreEqual(5, css.Ids.ResolutionCount,
            "Cold assemble must resolve every GameObject AND every component exactly once, batched.");
        Assert.IsTrue(css.Ids.LastWarmUsedBatch,
            "6000.5.3f1 has the batch GetGlobalObjectIdsSlow overload; cold warm must use it for components too.");
    }

    [Test]
    public void SingleObjectEdit_WithComponentsInTheScene_ResolvesExactlyOneGlobalObjectId()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var withComponents = new GameObject("WithComponents");
        withComponents.AddComponent<Rigidbody>();
        withComponents.AddComponent<BoxCollider>();
        var target = new GameObject("Target");
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var css = new ChangeScopedSnapshot();
        css.AssembleCold(scene, SceneRefResolver.None);
        css.Ids.ResetCount();

        target.tag = "Player";
        SaveActiveScene();

        css.AssembleIncremental(scene, new[] { target.GetEntityId() }, SceneRefResolver.None);

        Assert.AreEqual(1, css.Ids.ResolutionCount,
            "Editing a component-free GameObject must resolve exactly 1 GlobalObjectId even when other " +
            "objects in the scene carry components — invalidation must scope to the changed GameObject's " +
            "own owned objects, not sweep the whole scene's component set.");
    }

    [Test]
    public void ColdAndIncrementalAssemble_WithComponents_StayByteEqual()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var scene = EditorSceneManager.GetActiveScene();
        var css = new ChangeScopedSnapshot();

        var go = new GameObject("Anchored");
        go.AddComponent<Rigidbody>();

        // Cold assemble while the scene is still UNSAVED: go and its Rigidbody warm the cache under
        // their transient (pre-save) GlobalObjectId. The first save below is what earns them their
        // durable id, the same sequence SceneBuilderAutoSync.NeedsSaveForDurableId exists for.
        css.AssembleCold(scene, SceneRefResolver.None);

        SaveActiveScene();

        var incremental = css.AssembleIncremental(scene, new[] { go.GetEntityId() }, SceneRefResolver.None);
        var freshCold = SceneSnapshotReader.Read(scene, resolveSceneRef: null);

        Assert.AreEqual(
            CanonicalJson.Serialize(freshCold),
            CanonicalJson.Serialize(incremental),
            "An incremental assemble of a GameObject whose durable id is only earned (by save) between " +
            "the cold warm and the incremental assemble must be byte-equal to a fresh cold read — " +
            "invalidating the changed GameObject must invalidate its cached component ids too, not just " +
            "its own.");
    }
}
