using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Reconcile;

// A component field referencing a PREFAB-INSTANCE ROOT must round-trip exactly like a reference to a
// plain scene.Add(...) GameObject: the live property assigns, a settled rebuild emits no ops for the
// reference, and a sync never reports it as dangling or rewrites the handle name away. The same
// contract holds when the field is authored in the typed member-selector spelling (c.Set(x => x.field,
// target)) rather than the string spelling.
public class RoundTripObjectRefPrefabInstanceTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_ObjRefInstance";
    private const string PrefabPath = FixturesDir + "/RefTarget.prefab";
    private const string InstanceName = "RefTarget"; // scene.Instance(path)'s name derives from the path stem
    private const string ScenePath = "Assets/GateTests/__RoundTripObjectRefPrefabInstanceTemp.unity";

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class RoundTripObjectRefPrefabInstanceScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private const string InstanceBody =
        "        var tank = scene.Instance(\"" + PrefabPath + "\");\n" +
        "        var opener = scene.Add(\"Opener\");\n" +
        "        opener.Component<DoorOpener>(c => c.Set(\"target\", tank));";

    // CONTROL: identical fixture, except the target is a plain scene.Add(...) GameObject instead of
    // a prefab instance — isolates any op count the reference itself would contribute from ops an
    // instance-bearing scene carries for unrelated reasons.
    private const string ControlBody =
        "        var doorControl = scene.Add(\"DoorControl\");\n" +
        "        var openerControl = scene.Add(\"OpenerControl\");\n" +
        "        openerControl.Component<DoorOpener>(c => c.Set(\"target\", doorControl));";

    // The TYPED spelling, targeting a plain node. The rewire moves it onto the instance root; sync
    // patches only the value argument, so the selector survives and the argument becomes an
    // InstanceHandle.
    private const string TypedSelectorBody =
        "        var tank = scene.Instance(\"" + PrefabPath + "\");\n" +
        "        var door = scene.Add(\"Door\");\n" +
        "        var opener = scene.Add(\"Opener\");\n" +
        "        opener.Component<DoorOpener>(c => c.Set(x => x.target, door));";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_ObjRefInstance");
        }

        var source = new GameObject("RefTarget_Source");
        PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
        UnityEngine.Object.DestroyImmediate(source);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(PrefabPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    // Runs a fresh builder+scene cycle for `body`, converges it (asserting zero patch edits on the
    // second sync), rebuilds once more from the converged source, and returns that rebuild's
    // BuildResult.PlanOpCount.
    private static int ConvergedRebuildPlanOpCount(string body, string dirSuffix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb_rtorpi_" + dirSuffix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var builderPath = Path.Combine(dir, "RoundTripObjectRefPrefabInstanceScene.cs");
            var sidecarPath = Path.Combine(dir, "RoundTripObjectRefPrefabInstanceScene.sbmap.json");
            File.WriteAllText(builderPath, Source(body));

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scene = EditorSceneManager.GetActiveScene();
            SceneBuilderBuild.Run(builderPath, ScenePath, sidecarPath, scene);

            EmittedCodeCompiles.SyncAndAssertCompiles(builderPath, sidecarPath, EditorSceneManager.GetActiveScene());
            var convergenceCheck = EmittedCodeCompiles.SyncAndAssertCompiles(builderPath, sidecarPath, EditorSceneManager.GetActiveScene());
            Assert.AreEqual(0, convergenceCheck.PatchEdits,
                "Source did not converge to a stable baseline for '" + dirSuffix + "'.");

            var rebuild = SceneBuilderBuild.Run(builderPath, ScenePath, sidecarPath, EditorSceneManager.GetActiveScene());
            return rebuild.PlanOpCount;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Build_ReferenceToPrefabInstanceRoot_AssignsTheLiveRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb_rtorpi_precondition_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var builderPath = Path.Combine(dir, "RoundTripObjectRefPrefabInstanceScene.cs");
            var sidecarPath = Path.Combine(dir, "RoundTripObjectRefPrefabInstanceScene.sbmap.json");
            File.WriteAllText(builderPath, Source(InstanceBody));

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scene = EditorSceneManager.GetActiveScene();
            SceneBuilderBuild.Run(builderPath, ScenePath, sidecarPath, scene);

            var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), InstanceName);
            var opener = FindRoot(EditorSceneManager.GetActiveScene(), "Opener");
            Assert.IsNotNull(instanceRoot, InstanceName + " instance root was not created by SceneBuilderBuild.Run");
            Assert.IsNotNull(opener, "Opener was not created by SceneBuilderBuild.Run");
            Assert.AreSame(instanceRoot, opener.GetComponent<DoorOpener>().target,
                "DoorOpener.target was not assigned to the live prefab-instance root");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Rebuild_FromConvergedSourceWithPrefabInstanceReference_EmitsNoSetReference()
    {
        var instanceOps = ConvergedRebuildPlanOpCount(InstanceBody, "instance");
        var controlOps = ConvergedRebuildPlanOpCount(ControlBody, "control");

        Assert.AreEqual(controlOps, instanceOps,
            "Rebuilding from a converged source whose reference targets a prefab-instance root emitted "
            + instanceOps + " plan op(s) against a plain-GameObject control's " + controlOps
            + "; a settled reference to a live instance root must not be rewritten on every build.");
    }

    [Test]
    public void Sync_ReferenceToPrefabInstanceRoot_ReportsNoDanglingReferenceAndIsAFixedPoint()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb_rtorpi_sync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var builderPath = Path.Combine(dir, "RoundTripObjectRefPrefabInstanceScene.cs");
            var sidecarPath = Path.Combine(dir, "RoundTripObjectRefPrefabInstanceScene.sbmap.json");
            File.WriteAllText(builderPath, Source(InstanceBody));

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scene = EditorSceneManager.GetActiveScene();
            SceneBuilderBuild.Run(builderPath, ScenePath, sidecarPath, scene);

            EmittedCodeCompiles.SyncAndAssertCompiles(builderPath, sidecarPath, EditorSceneManager.GetActiveScene());
            var result = EmittedCodeCompiles.SyncAndAssertCompiles(builderPath, sidecarPath, EditorSceneManager.GetActiveScene());

            Assert.AreEqual(0, result.Conflicts.Count(c => c.Kind == ConflictKind.DanglingReference),
                "A live, mapped prefab-instance-root target must never report DanglingReference.");
            Assert.AreEqual(0, result.PatchEdits, "A settled prefab-instance reference must sync as a no-op.");
            StringAssert.Contains(", tank)", File.ReadAllText(builderPath),
                "The authored handle name must survive the sync, not be rewritten to a raw GlobalObjectId.");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void SceneToCode_TypedSelectorRewiredOntoInstanceRoot_CompilesAndIsAFixedPoint()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb_rtorpi_typed_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var builderPath = Path.Combine(dir, "RoundTripObjectRefPrefabInstanceScene.cs");
            var sidecarPath = Path.Combine(dir, "RoundTripObjectRefPrefabInstanceScene.sbmap.json");
            File.WriteAllText(builderPath, Source(TypedSelectorBody));

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scene = EditorSceneManager.GetActiveScene();
            SceneBuilderBuild.Run(builderPath, ScenePath, sidecarPath, scene);

            var opener = FindRoot(EditorSceneManager.GetActiveScene(), "Opener");
            var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), InstanceName);
            Assert.IsNotNull(opener, "Opener was not created by SceneBuilderBuild.Run");
            Assert.IsNotNull(instanceRoot, InstanceName + " instance root was not created by SceneBuilderBuild.Run");
            opener.GetComponent<DoorOpener>().target = instanceRoot;

            var result = EmittedCodeCompiles.SyncAndAssertCompiles(builderPath, sidecarPath, EditorSceneManager.GetActiveScene());

            Assert.IsTrue(result.Changed, "Sync reported no change despite a typed-selector field rewired onto the instance root");
            Assert.AreEqual(1, result.PatchEdits, "Rewiring the field onto the instance root must produce exactly one patch edit.");
            Assert.AreEqual(0, result.CompileErrors.Length,
                "The typed-selector rewire onto a prefab-instance root wrote source that does not compile.");
            Assert.AreEqual(0, result.Conflicts.Count(c => c.Kind == ConflictKind.DanglingReference),
                "A live, mapped prefab-instance-root target must never report DanglingReference.");

            var rewritten = File.ReadAllText(builderPath);
            StringAssert.Contains("c.Set(x => x.target, tank)", rewritten,
                "The typed member selector must survive the sync, with only its value argument swapped to the instance handle.\n" + rewritten);
            StringAssert.DoesNotContain("x.target, door", rewritten,
                "Builder source still carries the old 'door' handle argument.\n" + rewritten);

            var second = EmittedCodeCompiles.SyncAndAssertCompiles(builderPath, sidecarPath, EditorSceneManager.GetActiveScene());
            Assert.AreEqual(0, second.PatchEdits, "A settled typed-selector instance reference must sync as a no-op.");
            Assert.IsFalse(second.Changed, "A no-op re-sync must not report Changed=true.");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A reference-list field holding one plain scene object and one prefab-instance root has no
    // common C# element type for an implicitly-typed array; the emitted list literal must still
    // compile, whatever kinds its live elements happen to be.
    [Test]
    public void SceneToCode_ReferenceListMixingNodeAndInstanceRoot_CompilesAndIsAFixedPoint()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb_rtorpi_list_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var builderPath = Path.Combine(dir, "RoundTripObjectRefPrefabInstanceScene.cs");
            var sidecarPath = Path.Combine(dir, "RoundTripObjectRefPrefabInstanceScene.sbmap.json");
            const string body =
                "        var tank = scene.Instance(\"" + PrefabPath + "\");\n" +
                "        var door = scene.Add(\"Door\");\n" +
                "        var path = scene.Add(\"Path\");\n" +
                "        path.Component<WaypointPath>(c => c.Set(\"waypoints\", new[] { door }));";
            File.WriteAllText(builderPath, Source(body));

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scene = EditorSceneManager.GetActiveScene();
            SceneBuilderBuild.Run(builderPath, ScenePath, sidecarPath, scene);

            var doorGo = FindRoot(EditorSceneManager.GetActiveScene(), "Door");
            var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), InstanceName);
            var pathGo = FindRoot(EditorSceneManager.GetActiveScene(), "Path");
            Assert.IsNotNull(doorGo, "Door was not created by SceneBuilderBuild.Run");
            Assert.IsNotNull(instanceRoot, InstanceName + " instance root was not created by SceneBuilderBuild.Run");
            Assert.IsNotNull(pathGo, "Path was not created by SceneBuilderBuild.Run");

            var waypointPath = pathGo.GetComponent<WaypointPath>();
            waypointPath.waypoints = new[] { doorGo, instanceRoot };
            EditorUtility.SetDirty(waypointPath);

            var result = EmittedCodeCompiles.SyncAndAssertCompiles(builderPath, sidecarPath, EditorSceneManager.GetActiveScene());

            Assert.AreEqual(0, result.CompileErrors.Length,
                "A reference list mixing a plain scene object and a prefab-instance root wrote source that does not compile.");
            Assert.AreEqual(0, result.Conflicts.Count(c => c.Kind == ConflictKind.DanglingReference),
                "A live, mapped list of references must never report DanglingReference.");

            var second = EmittedCodeCompiles.SyncAndAssertCompiles(builderPath, sidecarPath, EditorSceneManager.GetActiveScene());
            Assert.AreEqual(0, second.PatchEdits, "A settled mixed-kind reference list must sync as a no-op.");
            Assert.IsFalse(second.Changed, "A no-op re-sync must not report Changed=true.");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void RoundTrip_TypedSelectorOnInstanceRoot_RebuildMaterializesTheInstanceRootAndReSyncIsANoOp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb_rtorpi_typedrt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var builderPath = Path.Combine(dir, "RoundTripObjectRefPrefabInstanceScene.cs");
            var sidecarPath = Path.Combine(dir, "RoundTripObjectRefPrefabInstanceScene.sbmap.json");
            File.WriteAllText(builderPath, Source(TypedSelectorBody));

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scene = EditorSceneManager.GetActiveScene();
            SceneBuilderBuild.Run(builderPath, ScenePath, sidecarPath, scene);

            var opener = FindRoot(EditorSceneManager.GetActiveScene(), "Opener");
            var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), InstanceName);
            opener.GetComponent<DoorOpener>().target = instanceRoot;
            EmittedCodeCompiles.SyncAndAssertCompiles(builderPath, sidecarPath, EditorSceneManager.GetActiveScene());

            SceneBuilderBuild.Run(builderPath, ScenePath, sidecarPath, EditorSceneManager.GetActiveScene());
            var rebuiltOpener = FindRoot(EditorSceneManager.GetActiveScene(), "Opener");
            var rebuiltInstanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), InstanceName);
            Assert.AreSame(rebuiltInstanceRoot, rebuiltOpener.GetComponent<DoorOpener>().target,
                "Rebuilding from the typed-selector source did not materialize the instance root as the target.");

            var second = EmittedCodeCompiles.SyncAndAssertCompiles(builderPath, sidecarPath, EditorSceneManager.GetActiveScene());
            Assert.AreEqual(0, second.PatchEdits,
                "NOT CONVERGED: the no-op re-sync's reconcile produced " + second.PatchEdits + " patch edit(s).");
            Assert.IsFalse(second.Changed,
                "NOT CONVERGED: a Sync immediately after a rebuild, with no further scene change, reported Changed=true.");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
