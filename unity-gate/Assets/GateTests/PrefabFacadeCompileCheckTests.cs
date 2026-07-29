using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;

// live-verify-bug-fixes b5-t1 (specs/30-live-verify-bug-fixes.md, Bug 5): mirrors
// AssetCatalogCompileCheckTests but pins the equivalent `Prefabs` façade contract. Today
// BuilderCompileCheck has no stub for the generator-emitted `Prefabs` static class (it only exists
// in the REAL project csproj via CodeScenes.Analyzers' PrefabFacadeGenerator, never in this editor
// AppDomain), so EVERY typed `Prefabs.*` reference — the north-star typed instance form — reports a
// false CS0103 "The name 'Prefabs' does not exist" from BuilderCompileCheck.Check, which the façade
// Sync sites currently paper over with LogAssert.ignoreFailingMessages (b5-t2 removes that
// suppression once this stub lands). These tests pin that Check() resolves a `Prefabs.*` root
// sourced from the CURRENT on-disk façade manifest (Prefabs.sbfacade.json), exactly as
// AssetCatalogCompileCheckTests pins for `Assets.*`.
public class PrefabFacadeCompileCheckTests
{
    private const string Dir = "Assets/GateTests/Fixtures_PrefabFacadeCompileCheck";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class PrefabFacadeCompileCheckGateScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _manifestPath = SceneBuilderPaths.FacadeManifestPath;
        _manifestExistedBefore = File.Exists(_manifestPath);
        _manifestBytesBefore = _manifestExistedBefore ? File.ReadAllBytes(_manifestPath) : null;
    }

    [TearDown]
    public void TearDown()
    {
        PrefabFacadeFixture.Delete(Dir);

        if (_manifestExistedBefore)
        {
            File.WriteAllBytes(_manifestPath, _manifestBytesBefore);
        }
        else if (File.Exists(_manifestPath))
        {
            File.Delete(_manifestPath);
        }
    }

    // A builder referencing a typed `Prefabs.*` root that genuinely exists in the CURRENT on-disk
    // façade manifest must compile via the inherited BuilderCompileCheck seam — the same seam
    // production Sync/Build routes through with zero per-caller opt-in.
    [Test]
    public void Check_BuilderReferencingFacadePrefab_Compiles()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        var source = Source("        scene.Instance(Prefabs.Tank);");

        var diagnostics = BuilderCompileCheck.Check(source);

        Assert.IsEmpty(diagnostics,
            "A builder referencing a CATALOGUED typed façade member must compile — BuilderCompileCheck "
            + "must resolve the generator-emitted `Prefabs` root from the on-disk façade manifest.\n"
            + string.Join("; ", diagnostics.Select(d => d.ToString())));
    }

    // A member absent from the CURRENT façade manifest (stale/renamed-away) must still be a genuine,
    // located compile error — the honesty requirement mirrored from the Assets stub. Distinguishes
    // the target contract from today's blanket "Prefabs does not exist" failure: once fixed,
    // `Prefabs` itself resolves and only the absent LEAF member is reported.
    [Test]
    public void Check_MemberAbsentFromFacadeManifest_ReportsLocatedError_NotThePrefabsRoot()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        var source = Source("        scene.Instance(Prefabs.NoSuchPrefab);");

        var diagnostics = BuilderCompileCheck.Check(source);

        Assert.IsNotEmpty(diagnostics,
            "A member absent from the CURRENT on-disk façade manifest must be a genuine, located "
            + "compile error — never silently suppressed.");
        Assert.IsTrue(diagnostics.Any(d => d.Line > 0),
            "Expected a LOCATED diagnostic (Line > 0) for the absent façade member.\n"
            + string.Join("; ", diagnostics.Select(d => d.ToString())));
        Assert.IsFalse(
            diagnostics.Any(d => d.Message.Contains("'Prefabs'") && d.Message.Contains("does not exist")),
            "The generated `Prefabs` root itself must resolve from the current façade manifest — the "
            + "reported diagnostic must name the absent MEMBER, not the missing `Prefabs` root.\n"
            + string.Join("; ", diagnostics.Select(d => d.ToString())));
    }

    // Baseline regression guard: with no façade manifest on disk, clean hand-written source (no
    // `Prefabs.*` usage) must be entirely unaffected by the stub injection — null catalog => no
    // façade stub tree added.
    [Test]
    public void Check_NoFacadeManifest_HandWrittenSourceUnaffected()
    {
        if (File.Exists(_manifestPath))
        {
            File.Delete(_manifestPath);
        }

        var source = Source("        scene.Add(\"Anchor\");");

        var diagnostics = BuilderCompileCheck.Check(source);

        Assert.IsEmpty(diagnostics,
            "Clean hand-written source with NO façade manifest present must be unaffected by the stub "
            + "injection.\n" + string.Join("; ", diagnostics.Select(d => d.ToString())));
    }

    // Both stubs (Assets + Prefabs) must resolve together — a builder is free to mix typed asset and
    // typed prefab references in one file.
    [Test]
    public void Check_MixedAssetsAndPrefabsRefs_Compile()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        var source = Source(
            "        scene.Instance(Prefabs.Tank);\n"
            + "        scene.Add(\"Anchor\");");

        var diagnostics = BuilderCompileCheck.Check(source);

        Assert.IsEmpty(diagnostics,
            "A builder mixing a typed `Prefabs.*` reference with plain scene calls must compile.\n"
            + string.Join("; ", diagnostics.Select(d => d.ToString())));
    }
}
