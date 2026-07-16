using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Validation;

// b3-t2: SceneBuilderBuild.Run's FAILURE SURFACE changed from throw-on-first (a single
// ParseException/InvalidOperationException) to collect-all-refuse: on a planning-phase error, Run
// no longer throws — it returns a BuildResult carrying EVERY diagnostic found in one pass, with the
// scene left untouched. The per-error-class located-diagnostic assertions live in
// DuplicateSiblingNameTests/UnqualifiedTypeNameTests/RoundTripBuiltinRefErrorTests (updated
// alongside this file); this file proves the COLLECT-ALL property itself — two INDEPENDENT error
// classes (an unresolvable component type and a bad asset path) in ONE builder both show up in ONE
// BuildResult, not just the first one encountered.
public class BuildCollectAllTests
{
    private const string ScenePath = "Assets/GateTests/__BuildCollectAllTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using UnityEngine;
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
public class CollectAllScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_collectall_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "CollectAllScene.cs");
        _sidecarPath = Path.Combine(_dir, "CollectAllScene.sbmap.json");
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
            UnityEditor.AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    // The headline collect-all property: an unresolvable component type (SB2001) AND a bad asset
    // path (SB2101) in the SAME builder both appear in the SAME BuildResult — not just whichever one
    // a throw-on-first walk would have hit first. No throw, scene untouched (0 roots).
    [Test]
    public void Build_MultiErrorBuilder_RefusesWithAllDiagnostics_SceneUntouched()
    {
        File.WriteAllText(_builderPath, Source(
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Rigidbdy>();\n" +
            "        cube.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", " +
            "Asset(\"Assets/Materials/DoesNotExist.mat\")));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var result = SceneBuilderBuild.Run(
            _builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var codes = result.Diagnostics.Select(d => d.Code).ToArray();
        Assert.IsTrue(codes.Contains(DiagnosticCodes.UnresolvedType),
            "Expected SB2001 (unresolved component type) among the collected diagnostics. Got: "
            + string.Join(", ", codes));
        Assert.IsTrue(codes.Contains(DiagnosticCodes.AssetPathNotFound),
            "Expected SB2101 (asset path not found) among the collected diagnostics. Got: "
            + string.Join(", ", codes));
        Assert.GreaterOrEqual(result.Diagnostics.Count, 2,
            "Expected BOTH errors reported in one pass, not just the first encountered.");

        Assert.AreEqual(0, EditorSceneManager.GetActiveScene().GetRootGameObjects().Length,
            "Build refused but still mutated the scene.");
    }
}
