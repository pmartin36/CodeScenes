using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using UnityEngine;

// The bypass check for the round-trip proof suite: every RoundTripProof*Tests.cs test method must
// reach the scene through a RoundTripProofHarness entry point, never a build/sync API directly.
public class RoundTripProofHarnessUsageTests
{
    private const string SelfFileName = "RoundTripProofHarnessUsageTests.cs";

    private static readonly string[] BannedIdentifiers =
    {
        "SceneBuilderBuild", "SceneBuilderSync", "SceneBuilderAutoSync", "EmittedCodeCompiles", "EditorSceneManager"
    };

    private static readonly string[] HarnessEntryPoints =
        { "ProveCodeToScene", "ProveSceneToCode", "ProveReauthoredCodeToScene", "ProveCodeToSceneFixedPoint" };

    private static readonly string[] TestAttributeNames = { "Test", "TestCase", "TestCaseSource", "UnityTest" };

    private static string StripAttributeSuffix(string name) =>
        name.EndsWith("Attribute") ? name.Substring(0, name.Length - "Attribute".Length) : name;

    // The name a call expression invokes, ignoring the receiver: `Foo.Bar(...)` and `Bar(...)`
    // both yield "Bar".
    private static string InvokedName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };

    // Parses a single proof-suite source file and returns one violation string per offense: a
    // direct call to a build/sync API, or a [Test]/[TestCase]/[TestCaseSource]/[UnityTest] method
    // that never reaches a RoundTripProofHarness entry point, directly or via an in-file helper.
    private static List<string> Violations(string fileName, string sourceText)
    {
        var violations = new List<string>();
        var root = CSharpSyntaxTree.ParseText(sourceText).GetCompilationUnitRoot();

        // Rule A: no direct door. Identifier NODES, not raw text, so a comment or string literal
        // naming one of these types never false-positives.
        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!BannedIdentifiers.Contains(identifier.Identifier.Text))
            {
                continue;
            }

            var line = identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            violations.Add(
                $"{fileName}:{line}: names '{identifier.Identifier.Text}' directly -- round-trip proofs must "
                    + "reach the scene through RoundTripProofHarness.");
        }

        // Rule B: reaches the harness, directly or via an in-file helper, closed to a fixed point.
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var callsByMethod = methods.ToDictionary(
            m => m.Identifier.Text,
            m => m.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(InvokedName)
                .Where(name => name != null)
                .ToHashSet());

        var reachesHarness = new HashSet<string>(
            callsByMethod.Where(kv => kv.Value.Overlaps(HarnessEntryPoints)).Select(kv => kv.Key));

        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var kv in callsByMethod)
            {
                if (reachesHarness.Contains(kv.Key))
                {
                    continue;
                }

                if (kv.Value.Any(reachesHarness.Contains))
                {
                    reachesHarness.Add(kv.Key);
                    grew = true;
                }
            }
        }

        foreach (var method in methods)
        {
            var isTestMethod = method.AttributeLists
                .SelectMany(list => list.Attributes)
                .Any(attribute => TestAttributeNames.Contains(StripAttributeSuffix(attribute.Name.ToString())));

            if (!isTestMethod || reachesHarness.Contains(method.Identifier.Text))
            {
                continue;
            }

            var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            violations.Add(
                $"{fileName}:{line}: test method '{method.Identifier.Text}' does not reach "
                    + "RoundTripProofHarness.ProveCodeToScene/ProveSceneToCode, directly or via an in-file helper.");
        }

        return violations;
    }

    // Every file under Assets/GateTests matching RoundTripProof*Tests.cs, minus this file itself.
    private static string[] ScannedProofTestFiles()
    {
        var dir = Path.Combine(Application.dataPath, "GateTests");
        return Directory.GetFiles(dir, "RoundTripProof*Tests.cs")
            .Where(path => Path.GetFileName(path) != SelfFileName)
            .OrderBy(path => path)
            .ToArray();
    }

    [Test]
    public void EveryProofTestMethod_ReachesTheSceneThroughTheHarness()
    {
        var violations = new List<string>();
        foreach (var path in ScannedProofTestFiles())
        {
            violations.AddRange(Violations(Path.GetFileName(path), File.ReadAllText(path)));
        }

        Assert.IsEmpty(violations, string.Join("\n", violations));
    }

    [Test]
    public void TheScan_CoversTheProofSuite_AndExcludesOnlyItself()
    {
        var scanned = ScannedProofTestFiles();
        var scannedNames = scanned.Select(Path.GetFileName).ToArray();

        Assert.IsNotEmpty(
            scanned,
            "The scan found no RoundTripProof*Tests.cs files -- a scan that finds nothing must fail loud, not pass silent.");
        CollectionAssert.Contains(scannedNames, "RoundTripProofSmokeTests.cs");
        CollectionAssert.DoesNotContain(scannedNames, SelfFileName);
    }

    [Test]
    public void TheScan_FlagsABypassingSample()
    {
        const string sample = @"
using NUnit.Framework;
using SceneBuilder.Editor;

public class SampleProofTests
{
    [Test]
    public void Compliant_ReachesTheHarness()
    {
        RoundTripProofHarness.ProveCodeToScene("""", ""body"", ctx => { });
    }

    [Test]
    public void Bypassing_CallsBuildDirectly()
    {
        SceneBuilderBuild.Run(""a"", ""b"", ""c"", default);
    }
}";

        var violations = Violations("SampleProofTests.cs", sample);

        Assert.AreEqual(2, violations.Count, string.Join("\n", violations));
        Assert.IsFalse(
            violations.Exists(v => v.Contains("Compliant_ReachesTheHarness")),
            "The compliant method must not be reported as a violation.\n" + string.Join("\n", violations));
    }
}
