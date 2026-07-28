using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;

// b7-t2 iteration 2 (specs/28-typed-asset-catalogs.md): pins the CENTRAL reconciliation between
// "emit typed by default" (b4, shipped) and "generated C# must compile" (CLAUDE.md), enforced
// through the single inherited seam BuilderCompileCheck.Check. The `Assets` static class is
// emitted by CodeScenes.Analyzers' source generator into the REAL project's csproj compilation
// ONLY — never into this editor AppDomain's compile check — so without a central fix EVERY
// scene->code sync of a catalogued project asset (now typed by default) reports a false
// "DOES NOT COMPILE" (CS0103 on `Assets`). These tests pin that BuilderCompileCheck resolves a
// generator-emitted `Assets.*` root sourced from the CURRENT on-disk AssetCatalog, so a member
// that genuinely exists compiles and a member absent from the catalog is a real, located error
// naming the ABSENT MEMBER — never the missing `Assets` root itself. Distinct file/class from the
// pre-existing generic BuilderCompileCheckTests.cs (that file predates typed catalogs and is not
// stale — it covers the check's baseline reference-set/perf/wiring contract, untouched here).
public class AssetCatalogCompileCheckTests
{
    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class AssetCatalogCompileCheckGateScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _manifestPath = SceneBuilderPaths.AssetManifestPath;
        _manifestExistedBefore = File.Exists(_manifestPath);
        _manifestBytesBefore = _manifestExistedBefore ? File.ReadAllBytes(_manifestPath) : null;
    }

    [TearDown]
    public void TearDown()
    {
        AssetCatalogBuildFixtures.Delete();

        if (_manifestExistedBefore)
        {
            File.WriteAllBytes(_manifestPath, _manifestBytesBefore);
        }
        else if (File.Exists(_manifestPath))
        {
            File.Delete(_manifestPath);
        }
    }

    // A builder referencing a member that genuinely exists in the CURRENT on-disk catalog must
    // compile via the inherited BuilderCompileCheck seam — the same seam production Sync/Build and
    // every EmittedCodeCompiles caller already route through with zero per-caller opt-in.
    [Test]
    public void Check_BuilderReferencingCataloguedTypedMember_Compiles()
    {
        AssetCatalogBuildFixtures.Create();

        var source = Source(
            "        var rock = scene.Add(\"Rock\");\n" +
            "        rock.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { "
            + AssetCatalogBuildFixtures.TypedRocksStone + " }));");

        var diagnostics = BuilderCompileCheck.Check(source);

        Assert.IsEmpty(diagnostics,
            "A builder referencing a CATALOGUED typed member must compile — BuilderCompileCheck "
            + "must resolve the generator-emitted `Assets` root from the on-disk AssetCatalog.\n"
            + string.Join("; ", diagnostics.Select(d => d.ToString())));
    }

    // A member absent from the CURRENT catalog (stale/renamed-away) must still be a genuine, located
    // compile error — the honesty requirement: the stub must NOT paper over a real stale reference.
    // Distinguishes the target contract from today's blanket "Assets does not exist" failure: once
    // fixed, `Assets` itself resolves and only the absent LEAF member is reported.
    [Test]
    public void Check_MemberAbsentFromCurrentCatalog_ReportsLocatedErrorNamingTheMember_NotTheAssetsRoot()
    {
        AssetCatalogBuildFixtures.Create();

        var source = Source(
            "        var rock = scene.Add(\"Rock\");\n" +
            "        rock.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { "
            + "Assets.Materials." + AssetCatalogBuildFixtures.RootPrefix + ".Environment.Rocks.NoSuchMember"
            + " }));");

        var diagnostics = BuilderCompileCheck.Check(source);

        Assert.IsNotEmpty(diagnostics,
            "A member absent from the CURRENT on-disk catalog must be a genuine, located compile "
            + "error — never silently suppressed.");
        Assert.IsTrue(diagnostics.Any(d => d.Line > 0),
            "Expected a LOCATED diagnostic (Line > 0) for the absent typed member.\n"
            + string.Join("; ", diagnostics.Select(d => d.ToString())));
        Assert.IsFalse(
            diagnostics.Any(d => d.Message.Contains("'Assets'") && d.Message.Contains("does not exist")),
            "The generated `Assets` root itself must resolve from the current catalog — the reported "
            + "diagnostic must name the absent MEMBER, not the missing `Assets` root.\n"
            + string.Join("; ", diagnostics.Select(d => d.ToString())));
    }

    // Baseline regression guard: with no manifest on disk, clean hand-written source (no `Assets.*`
    // usage) must be entirely unaffected by the stub injection — null catalog => no stub tree added.
    [Test]
    public void Check_NoManifest_HandWrittenSourceUnaffected()
    {
        if (File.Exists(_manifestPath))
        {
            File.Delete(_manifestPath);
        }

        var source = Source("        scene.Add(\"Anchor\");");

        var diagnostics = BuilderCompileCheck.Check(source);

        Assert.IsEmpty(diagnostics,
            "Clean hand-written source with NO manifest present must be unaffected by the stub "
            + "injection.\n" + string.Join("; ", diagnostics.Select(d => d.ToString())));
    }
}
