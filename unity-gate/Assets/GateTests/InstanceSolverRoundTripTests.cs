using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using SceneBuilder.Authoring;
using SceneBuilder.Editor;

// The scene->code half of the instanced-prop solver workflow: a prefab instance carrying
// .FitSize/.AlignTo on its AddedComponents round-trips byte-stable through the real adapter.
// Mirrors RoundTripSpatialSyncTests.cs's harness (items 11/12) against
// InstanceSolverWorkflowTests.cs's nested-mesh fixture (mesh only in CHILD nodes, instance root
// Renderer-less) instead of a plain scene.Add(...) mesh object.
//
// Synchronous EditMode does NOT tick [ExecuteAlways].Update() — every geometry- or back-solve-
// dependent assertion explicitly calls component.Evaluate().
public class InstanceSolverRoundTripTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_InstanceSolverRoundTrip";
    private const string PrefabPath = FixturesDir + "/NestedProp.prefab";
    private const string ScenePath = "Assets/GateTests/__InstanceSolverRoundTripTemp.unity";
    private const float Tol = 1e-3f;

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
public class InstanceSolverRoundTripScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private const string FloorBody =
        "        var floor = scene.Add(\"Floor\")\n" +
        "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
        "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
        "            .Transform(scale: (10f, 1f, 10f));\n";

    private static string InstanceBody(string prefabPath) =>
        "        scene.Instance(\"" + prefabPath + "\")\n" +
        "            .Transform(pos: (0f, 0f, 0f))\n" +
        "            .FitSize(width: 2f)\n" +
        "            .AlignTo(floor, y: AxisAlign.AbutMax);\n";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    // Extracts the full `.Transform(pos: (...))` call text (balanced parens) so an assertion can
    // inspect the driven-axis literal without colliding with unrelated numeric literals elsewhere
    // in the file (e.g. the Floor's scale: (10f, 1f, 10f)).
    private static string ExtractTransformPosCall(string source)
    {
        const string marker = ".Transform(pos:";
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        int i = start + marker.Length;
        int depth = 1; // already inside the outer '(' of "Transform("
        while (i < source.Length && depth > 0)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')') depth--;
            i++;
        }

        return source.Substring(start, i - start);
    }

    // Scopes a check to the instance's own statement (from its `scene.Instance(...)` call to the
    // end of the file, which is safe since it's the last statement in Build): the Floor legitimately
    // authors its OWN unrelated, non-driven `.Transform(scale: (10f, 1f, 10f))`, which would
    // otherwise false-positive a whole-file substring check for a driven-scale leak.
    private static string ExtractInstanceStatement(string source)
    {
        int start = source.IndexOf("scene.Instance(", StringComparison.Ordinal);
        Assert.GreaterOrEqual(start, 0, "Expected a scene.Instance(...) statement in the rewritten source.\n" + source);
        return source.Substring(start);
    }

    // ComponentDefaultTemplate.Register lazily instantiates a throwaway probe object the FIRST time
    // a component type is snapshot-read, to compute a per-type default-field map. For FitSize/AlignTo
    // ([ExecuteAlways] with an OnValidate that logs a located error against that bare probe) this
    // fires exactly once per type, non-deterministically on whichever [Test] syncs one first. Warm it
    // up here, isolated from any real test's LogAssert window.
    [OneTimeSetUp]
    public void WarmSpatialDefaultFieldCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb_isrt_warm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var builderPath = Path.Combine(dir, "WarmScene.cs");
        var sidecarPath = Path.Combine(dir, "WarmScene.sbmap.json");
        try
        {
            File.WriteAllText(builderPath, Source(
                "        scene.Add(\"Warm\")\n" +
                "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
                "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
                "            .FitSize(height: 1f)\n" +
                "            .AlignTo(NodeHandle.None, y: AxisAlign.AbutMax);\n"));

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scene = EditorSceneManager.GetActiveScene();

            var prevIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                SceneBuilderBuild.Run(builderPath, ScenePath, sidecarPath, scene);
                EmittedCodeCompiles.SyncAndAssertCompiles(builderPath, sidecarPath, EditorSceneManager.GetActiveScene());
            }
            finally
            {
                LogAssert.ignoreFailingMessages = prevIgnore;
            }
        }
        finally
        {
            Directory.Delete(dir, true);
            if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_isrt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "InstanceSolverRoundTripScene.cs");
        _sidecarPath = Path.Combine(_dir, "InstanceSolverRoundTripScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_InstanceSolverRoundTrip");
        }

        // Empty root with two mesh-bearing CHILD cubes: the instance root itself carries no
        // Renderer/MeshFilter -- the shape this whole feature exists to size and rest.
        var source = new GameObject("NestedProp_Source");
        var left = GameObject.CreatePrimitive(PrimitiveType.Cube);
        left.name = "Left";
        left.transform.SetParent(source.transform, worldPositionStays: false);
        left.transform.localPosition = new Vector3(-1f, 0f, 0f);
        var right = GameObject.CreatePrimitive(PrimitiveType.Cube);
        right.name = "Right";
        right.transform.SetParent(source.transform, worldPositionStays: false);
        right.transform.localPosition = new Vector3(1f, 0f, 0f);

        PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
        UnityEngine.Object.DestroyImmediate(source);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);

        AssetDatabase.DeleteAsset(PrefabPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }

        // Reset the ambient active scene so this suite's Renderers never leak into a later suite's
        // AlignTo fallback-scan (a global "every Renderer in the scene" search).
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    private void RunBuild(Scene scene) => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

    // A no-op re-sync must never leak the FitSize-driven scale or the AlignTo-driven Y into source,
    // and must converge with zero patch edits.
    [Test]
    public void Instance_SolverVerbs_NoOpResync_IsByteStable_NoDrivenLeak()
    {
        File.WriteAllText(_builderPath, Source(FloorBody + InstanceBody(PrefabPath)));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), "NestedProp");
        Assert.IsNotNull(instanceRoot, "Instance root was not created by SceneBuilderBuild.Run");
        var fitSize = instanceRoot.GetComponent<FitSize>();
        var alignTo = instanceRoot.GetComponent<AlignTo>();
        Assert.IsNotNull(fitSize, "FitSize was not landed on the instance root.");
        Assert.IsNotNull(alignTo, "AlignTo was not landed on the instance root.");
        fitSize.Evaluate();
        alignTo.Evaluate();

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(result.Changed, "An unedited instance with FitSize/AlignTo must Sync as a no-op.");
        Assert.AreEqual(0, result.PatchEdits, "An unedited instance with FitSize/AlignTo must produce zero patch edits.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".FitSize(", rewritten, "Rewritten source must still carry the .FitSize(...) call.\n" + rewritten);
        StringAssert.Contains(".AlignTo(", rewritten, "Rewritten source must still carry the .AlignTo(...) call.\n" + rewritten);
        var instanceStatement = ExtractInstanceStatement(rewritten);
        StringAssert.DoesNotContain(".Transform(scale:", instanceStatement,
            "The FitSize-driven scale must never leak into source as a .Transform(scale:) write.\n" + rewritten);
    }

    // A real free-axis (X) drag on the instance root must sync back as a source edit; the
    // FitSize-driven scale and the AlignTo-driven Y must stay held (never written); the .FitSize/
    // .AlignTo calls survive verbatim; and a second, no-op sync converges (PatchEdits == 0).
    [Test]
    public void Instance_SolverVerbs_FreeAxisDrag_SyncsBackFreeX_HoldsDrivenAxes_SecondSyncByteStable()
    {
        File.WriteAllText(_builderPath, Source(FloorBody + InstanceBody(PrefabPath)));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), "NestedProp");
        Assert.IsNotNull(instanceRoot, "Instance root was not created by SceneBuilderBuild.Run");
        var fitSize = instanceRoot.GetComponent<FitSize>();
        var alignTo = instanceRoot.GetComponent<AlignTo>();
        fitSize.Evaluate();
        alignTo.Evaluate(); // baseline: combined bounds bottom flush on the floor

        const float draggedX = 3f;
        var pos = instanceRoot.transform.position;
        instanceRoot.transform.position = new Vector3(draggedX, pos.y, pos.z);
        alignTo.Evaluate(); // re-snap Y; X stays free

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "The free-X drag is a real scene change; Sync must report Changed.");

        var rewritten = File.ReadAllText(_builderPath);
        var posCall = ExtractTransformPosCall(rewritten);
        Assert.IsNotNull(posCall, "Expected a .Transform(pos: ...) call in the rewritten source for the instance.\n" + rewritten);
        StringAssert.Contains("3f", posCall, "The free X drag value must be present in the pos: argument.\n" + posCall);
        var instanceStatement = ExtractInstanceStatement(rewritten);
        StringAssert.DoesNotContain(".Transform(scale:", instanceStatement,
            "The FitSize-driven scale must never leak into source as a .Transform(scale:) write.\n" + rewritten);
        StringAssert.Contains(".FitSize(width: 2f)", instanceStatement, "The .FitSize(width: 2f) call must survive the drag verbatim.\n" + rewritten);
        StringAssert.Contains(".AlignTo(floor, y: AxisAlign.AbutMax)", instanceStatement,
            "The .AlignTo(floor, y: AxisAlign.AbutMax) call must survive the drag verbatim.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: a Sync immediately after the drag patch, with no further edit, reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits, "NOT CONVERGED: the second sync must produce zero patch edits.");
    }
}
