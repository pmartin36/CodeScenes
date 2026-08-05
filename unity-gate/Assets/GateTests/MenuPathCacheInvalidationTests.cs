using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// spec 35 D3, manual-menu-path gate: reproduces the spec's five-step sequence through the ACTUAL
// menu-command entry points (SceneBuilderSync.SyncActiveScene / SceneBuilderBuild.BuildAll), not a
// hand-rolled ChangeScopedSnapshot call. An auto-sync code->scene cycle caches a node whose
// reference field targets a hand-created, unmapped scene object (a raw GlobalObjectId); a menu
// command then maps that target; an unrelated nudge must trigger an incremental assemble that
// behaves exactly like a cold read of the same scene state — no dangling-reference report, the
// reference field actually patched into the builder source, and the written bytes byte-identical
// to a fresh cold Sync from the same starting bytes.
public class MenuPathCacheInvalidationTests
{
    private const string BuilderName = "MenuPathCacheInvalidationScene";
    private const string ScenePath = "Assets/GateTests/__MenuPathCacheInvalidationTemp.unity";

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class MenuPathCacheInvalidationScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    private static System.Collections.Generic.List<(string Message, LogType Type)> CaptureLogs(System.Action action)
    {
        var messages = new System.Collections.Generic.List<(string, LogType)>();
        void Handler(string condition, string stackTrace, LogType type) => messages.Add((condition, type));
        Application.logMessageReceived += Handler;
        try { action(); }
        finally { Application.logMessageReceived -= Handler; }
        return messages;
    }

    [SetUp]
    public void SetUp()
    {
        SceneBuilderPaths.EnsureBuildersDirectory();
        _builderPath = SceneBuilderPaths.Builder(BuilderName);
        _sidecarPath = SceneBuilderPaths.Sidecar(BuilderName);

        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();

        if (File.Exists(_builderPath)) File.Delete(_builderPath);
        if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);

        var coldBuilderPath = _builderPath + ".cold.cs";
        var coldSidecarPath = _sidecarPath + ".cold.json";
        if (File.Exists(coldBuilderPath)) File.Delete(coldBuilderPath);
        if (File.Exists(coldSidecarPath)) File.Delete(coldSidecarPath);

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    // Seeds a converged builder (Opener carrying an UNauthored DoorOpener field, plus an unrelated
    // Other), hand-creates Door live and wires it as Opener's live target, then runs the code->scene
    // auto cycle whose CaptureBaseline tail cold-assembles under a sidecar with no entry for
    // Door — arming the assembler cache with Door's raw GlobalObjectId as the read value.
    private (GameObject Other, GameObject Door, string DoorGoid) SeedAndWarmCache()
    {
        File.WriteAllText(_builderPath, Source(
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>();\n" +
            "        scene.Add(\"Other\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        SceneBuilderRouter.ResetForTests();

        var opener = FindRoot(EditorSceneManager.GetActiveScene(), "Opener");
        var other = FindRoot(EditorSceneManager.GetActiveScene(), "Other");
        Assert.IsNotNull(opener, "Seed build did not materialize Opener.");
        Assert.IsNotNull(other, "Seed build did not materialize Other.");

        // A CHILD of Opener, not a scene root: a root-level append always lands after every
        // existing root statement (Reconciler.DetectAppends seats a new root at
        // expected.Roots.Length), which would place Door's declaration AFTER the Component() call
        // that comes to reference it — an unrelated ordering defect this scenario must not trip.
        // A child append seats immediately after its parent's own declaration instead, which keeps
        // Door's declaration ahead of the statement that references its handle.
        var door = new GameObject("Door");
        door.transform.SetParent(opener.transform, worldPositionStays: false);
        opener.GetComponent<DoorOpener>().target = door;
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());

        var doorGoid = GlobalObjectId.GetGlobalObjectIdSlow(door).ToString();
        var mapBeforeWarm = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        Assert.IsFalse(mapBeforeWarm.IsManaged(doorGoid),
            "Precondition: Door must not be mapped before the cache-warming auto cycle.");

        SceneBuilderAutoSync.ExecuteCodeToScene(new[] { _builderPath });

        var mapAfterWarm = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        Assert.IsFalse(mapAfterWarm.IsManaged(doorGoid),
            "Precondition: the cache-warming cycle itself must not map Door, or the cached node never " +
            "holds a raw GlobalObjectId and the scenario is vacuous.");

        return (other, door, doorGoid);
    }

    [Test]
    public void MenuPath_SyncActiveSceneThenUnrelatedNudge_NoDanglingReference_PatchNotSuppressed_ByteEqualsColdRead()
    {
        var (other, door, doorGoid) = SeedAndWarmCache();

        SceneBuilderSync.SyncActiveScene();

        var mapAfterSync = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        Assert.IsTrue(mapAfterSync.IsManaged(doorGoid),
            "SyncActiveScene must map the hand-created Door object.");
        var sourceAfterSync = File.ReadAllText(_builderPath);
        StringAssert.Contains("Add(\"Door\")", sourceAfterSync,
            "SyncActiveScene must append Door to the builder source.\n" + sourceAfterSync);
        StringAssert.DoesNotContain("\"target\"", sourceAfterSync,
            "The target field must stay suppressed the instant Door is added in the same sync that " +
            "discovers it (Pending target).\n" + sourceAfterSync);

        var coldBuilderPath = _builderPath + ".cold.cs";
        var coldSidecarPath = _sidecarPath + ".cold.json";
        File.Copy(_builderPath, coldBuilderPath, overwrite: true);
        File.Copy(_sidecarPath, coldSidecarPath, overwrite: true);

        other.transform.position = new Vector3(1f, 2f, 3f);

        var logs = CaptureLogs(() => SceneBuilderAutoSync.ExecuteSceneToCode(new[] { other.GetEntityId() }));

        Assert.IsFalse(logs.Any(l => l.Message.Contains("DanglingReference")),
            "The nudge cycle must not report Door as a dangling reference once the sidecar maps it.\n" +
            string.Join("\n", logs.Select(l => l.Message)));
        Assert.IsFalse(logs.Any(l => l.Message.Contains("Dangling reference")),
            "The nudge cycle must not report Door as a dangling reference once the sidecar maps it.\n" +
            string.Join("\n", logs.Select(l => l.Message)));

        var sourceAfterNudge = File.ReadAllText(_builderPath);
        StringAssert.Contains("var door", sourceAfterNudge,
            "The nudge cycle must introduce a handle for Door once the reference resolves.\n" + sourceAfterNudge);
        StringAssert.Contains("\"target\", door", sourceAfterNudge,
            "The nudge cycle must introduce the target setter, not leave it suppressed.\n" + sourceAfterNudge);

        var liveScene = EditorSceneManager.GetActiveScene();
        SceneBuilderSync.Run(coldBuilderPath, coldSidecarPath, liveScene);

        Assert.AreEqual(sourceAfterNudge, File.ReadAllText(coldBuilderPath),
            "The real incremental path must write the same builder source a cold read produces from the same starting bytes.");
        Assert.AreEqual(File.ReadAllText(_sidecarPath), File.ReadAllText(coldSidecarPath),
            "The real incremental path must write the same sidecar a cold read produces from the same starting bytes.");

        Assert.AreEqual(doorGoid, GlobalObjectId.GetGlobalObjectIdSlow(door).ToString(),
            "Door's GlobalObjectId must be unchanged across the whole sequence.");
    }

    // Build All runs immediately after Sync Active Scene, the order a user actually performs. Its
    // distinct claim: Build All does not refresh the assembler cache either, so the invalidation
    // must not depend on which manual command happens to run last.
    [Test]
    public void MenuPath_SyncActiveSceneThenBuildAll_SameSequence_StillConverges()
    {
        var (other, door, doorGoid) = SeedAndWarmCache();

        SceneBuilderSync.SyncActiveScene();
        SceneBuilderBuild.BuildAll();

        var mapAfterBuildAll = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        Assert.IsTrue(mapAfterBuildAll.IsManaged(doorGoid),
            "Build All must not un-map Door.");

        var coldBuilderPath = _builderPath + ".cold.cs";
        var coldSidecarPath = _sidecarPath + ".cold.json";
        File.Copy(_builderPath, coldBuilderPath, overwrite: true);
        File.Copy(_sidecarPath, coldSidecarPath, overwrite: true);

        other.transform.position = new Vector3(4f, 5f, 6f);

        var logs = CaptureLogs(() => SceneBuilderAutoSync.ExecuteSceneToCode(new[] { other.GetEntityId() }));

        Assert.IsFalse(logs.Any(l => l.Message.Contains("DanglingReference")),
            "The nudge cycle must not report Door as a dangling reference after Sync then Build All.\n" +
            string.Join("\n", logs.Select(l => l.Message)));
        Assert.IsFalse(logs.Any(l => l.Message.Contains("Dangling reference")),
            "The nudge cycle must not report Door as a dangling reference after Sync then Build All.\n" +
            string.Join("\n", logs.Select(l => l.Message)));

        var sourceAfterNudge = File.ReadAllText(_builderPath);
        StringAssert.Contains("var door", sourceAfterNudge,
            "The nudge cycle must introduce a handle for Door once the reference resolves.\n" + sourceAfterNudge);
        StringAssert.Contains("\"target\", door", sourceAfterNudge,
            "The nudge cycle must introduce the target setter, not leave it suppressed.\n" + sourceAfterNudge);

        var liveScene = EditorSceneManager.GetActiveScene();
        SceneBuilderSync.Run(coldBuilderPath, coldSidecarPath, liveScene);

        Assert.AreEqual(sourceAfterNudge, File.ReadAllText(coldBuilderPath),
            "The real incremental path must write the same builder source a cold read produces from the same starting bytes.");
        Assert.AreEqual(File.ReadAllText(_sidecarPath), File.ReadAllText(coldSidecarPath),
            "The real incremental path must write the same sidecar a cold read produces from the same starting bytes.");
    }
}
