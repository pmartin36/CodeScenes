using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Validation;

// b5-t1 — THE consistency contract: SceneBuilderBuild.Run (live editor Build, driven by
// UnityResolutionProvider) and HeadlessValidator.Validate (disk-backed, driven by
// DiskResolutionProvider) must agree over the SAME parsed builder source — same refused/Ok verdict
// and identical sets of Error-severity (Code, Line) diagnostics — for every corpus member below.
// Both sides run PlanningValidator.Validate over the SAME parse (b2-t1's ONE shared walk), so a
// disagreement here means the two resolvers made a different call, not a parse difference. Corpus
// builders are runtime-authored strings (never committed .cs) because several are DELIBERATELY
// non-resolving and would break compilation of GateFixtures/GateTests if they were real source
// under Assets/ — mirrors BuildCollectAllTests.cs / UnqualifiedTypeNameTests.cs / DuplicateSiblingNameTests.cs.
public class HeadlessValidationConsistencyTests
{
    private const string ScenePath = "Assets/GateTests/__HeadlessConsistencyTemp.unity";
    private const string FixturesDir = "Assets/GateTests/Fixtures";
    private const string RedMatPath = FixturesDir + "/Red.mat";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string usings, string body) => $@"{usings}
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
public class HeadlessConsistencyScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_hvc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "HeadlessConsistencyScene.cs");
        _sidecarPath = Path.Combine(_dir, "HeadlessConsistencyScene.sbmap.json");

        // A real committed-shape Material + .meta (RoundTripAssetRefTests.cs:65-74 pattern) so BOTH
        // AssetDatabase (editor side) and a direct .meta read (headless side) resolve the SAME guid.
        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures");
        }
        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), RedMatPath);
        AssetDatabase.SaveAssets();
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

        if (File.Exists(RedMatPath))
        {
            AssetDatabase.DeleteAsset(RedMatPath);
        }
    }

    // Drives BOTH SceneBuilderBuild.Run (live editor) and HeadlessValidator.Validate (disk-backed)
    // over the IDENTICAL temp builder file, and asserts they agree: same refused/Ok verdict, and the
    // same set of Error-severity (Code, Line) diagnostics. `Assert.IsEmpty(headless.Skipped)` guards
    // the "a skip is never a pass" invariant — if the running editor's own managed DLL dir could not
    // be located, type/asset parity would silently pass by skipping instead of actually checking.
    private void AssertConsistent(string usings, string body)
    {
        File.WriteAllText(_builderPath, Source(usings, body));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        var editorErrors = buildResult.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => (d.Code, d.Line))
            .ToHashSet();
        var editorFailed = editorErrors.Count > 0;

        // The RUNNING editor's own managed DLLs — guarantees the disk metadata scan matches the
        // exact Unity version TypeCache/ComponentTypeResolver used on the editor side.
        var editorManagedDir = Path.Combine(EditorApplication.applicationContentsPath, "Managed");
        var layout = ProjectLayout.Infer(
            _builderPath, projectOverride: SceneBuilderPaths.ProjectRoot, managedOverride: editorManagedDir);
        var headless = HeadlessValidator.Validate(_builderPath, layout);

        Assert.IsEmpty(headless.Skipped,
            "Headless validator SKIPPED a check category instead of running it (managed DLL dir "
            + "unlocatable?) — a skip is never a pass, it would silently mask a real parity gap. Skipped: "
            + string.Join(", ", headless.Skipped));

        var headlessErrors = headless.Result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => (d.Code, d.Line))
            .ToHashSet();

        Assert.AreEqual(editorFailed, !headless.Ok,
            $"Editor Build refused={editorFailed} but headless Ok={headless.Ok} — the editor and the "
            + $"headless validator disagree on whether this builder is valid.\n"
            + $"Editor diagnostics: {string.Join("; ", buildResult.Diagnostics.Select(d => $"{d.Code}@{d.Line}"))}\n"
            + $"Headless diagnostics: {string.Join("; ", headless.Result.Diagnostics.Select(d => $"{d.Code}@{d.Line}"))}");

        CollectionAssert.AreEquivalent(editorErrors, headlessErrors,
            $"Editor and headless Error-severity (Code, Line) diagnostic sets disagree.\n"
            + $"Editor:    {string.Join("; ", editorErrors)}\n"
            + $"Headless:  {string.Join("; ", headlessErrors)}");
    }

    // Check 1: a clean builder (resolvable type + a valid project asset ref) is green on both sides.
    [Test]
    public void Clean_BothAgreeOk()
    {
        AssertConsistent(
            "using UnityEngine;",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Rigidbody>();\n" +
            "        cube.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + RedMatPath + "\") }));");
    }

    // Check 2 (unresolvable type): a typo'd component name flags SB2001 on both sides, same line.
    [Test]
    public void BadType_BothFlagSB2001_SameLine()
    {
        AssertConsistent(
            "using UnityEngine;",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Rigidbdy>();");
    }

    // Check 2 (missing using): a short name with NO in-scope using flags SB2001 on both sides.
    [Test]
    public void MissingUsing_BothFlagSB2001_SameLine()
    {
        AssertConsistent(
            "",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Rigidbody>();");
    }

    // Check 2 (ambiguous type): two in-scope namespaces both defining "Rigidbody" flag SB2002 on
    // both sides — UnityEngine.Rigidbody vs the MyGame.Physics.Rigidbody fixture.
    [Test]
    public void AmbiguousType_BothFlagSB2002_SameLine()
    {
        AssertConsistent(
            "using UnityEngine;\nusing MyGame.Physics;",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Rigidbody>();");
    }

    // Check 2 (bad asset path): a project-relative path with no .meta on disk flags SB2101 on both
    // sides, at the same argument span.
    [Test]
    public void BadAsset_BothFlagSB2101_SameSpan()
    {
        AssertConsistent(
            "using UnityEngine;",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", " +
            "Asset(\"Assets/Materials/DoesNotExist.mat\")));");
    }

    // Check 2 (duplicate siblings): two positional same-named Adds are a STRUCTURAL ambiguity
    // (parse.Ambiguities), never resolver-dependent — flags SB2201 identically on both sides.
    [Test]
    public void DupSiblings_BothFlagSB2201_SameLine()
    {
        AssertConsistent(
            "",
            "        scene.Add(\"Enemy\");\n" +
            "        scene.Add(\"Enemy\");");
    }

    // Check 3: TWO independent error classes (bad type + bad asset) in ONE builder — both sides
    // collect BOTH diagnostics (not just the first), and the two sets agree as sets.
    [Test]
    public void MultiError_BothReportBothErrors_SetsAgree()
    {
        AssertConsistent(
            "using UnityEngine;",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Rigidbdy>();\n" +
            "        cube.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", " +
            "Asset(\"Assets/Materials/DoesNotExist.mat\")));");
    }

    // Check 4: a user MonoBehaviour referenced by short name (compiled into
    // Library/ScriptAssemblies/GateFixtures.dll) resolves on both sides — the disk-backed provider's
    // assembly scan must see the SAME user script the live editor's TypeCache sees.
    [Test]
    public void UserScriptType_BothResolve()
    {
        AssertConsistent(
            "using MyGame.Enemies;",
            "        var e = scene.Add(\"EnemyObj\");\n" +
            "        e.Component<Enemy>();");
    }

    // Check 5: a valid project asset ref resolves clean on both sides, and (in the SAME builder) a
    // bad path flags SB2101 on both sides at the same span — proving asset parity isn't a fluke of
    // only ever testing one branch.
    [Test]
    public void AssetPath_ValidResolves_BadFlagsSB2101_BothAgree()
    {
        AssertConsistent(
            "using UnityEngine;",
            "        var good = scene.Add(\"Good\");\n" +
            "        good.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", " +
            "new[] { Asset(\"" + RedMatPath + "\") }));\n" +
            "        var bad = scene.Add(\"Bad\");\n" +
            "        bad.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", " +
            "Asset(\"Assets/Materials/DoesNotExist.mat\")));");
    }

    // Check 6 (the honest boundary): a Builtin("Cube") ref RESOLVES in the live editor
    // (UnityResolutionProvider.ResolveBuiltin finds the real primitive mesh) but the disk-backed
    // provider can only ever DEFER on a builtin (existence is editor-only) — both sides must still
    // agree Ok==true with an EMPTY error set. Deferred must never masquerade as a false alarm.
    [Test]
    public void Builtin_EditorResolves_HeadlessDefers_BothOk()
    {
        AssertConsistent(
            "using UnityEngine;",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")));");
    }
}
