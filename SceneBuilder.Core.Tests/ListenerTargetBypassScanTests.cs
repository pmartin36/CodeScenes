using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Two count-free, per-file guards on "ONE mechanism owns listener-target identity"
    // (SceneBuilder.Core.Identity.ComponentTargetResolution), modelled on
    // ValueContainerDescentScanTests' UnityEventListeners allowlist pair: a syntax-only Roslyn
    // token walk, a permission-not-obligation allowlist, the same "names the file, the token and
    // the remedy" message shape.
    //
    // Scan A: a site that reads a persistent call's target/object-argument fields directly
    // (`m_Target`, `m_TargetAssemblyTypeName`, `m_ObjectArgument`,
    // `m_ObjectArgumentAssemblyTypeName`, `m_PersistentCalls`, `GetPersistentTarget`) bypasses
    // ComponentTargetResolution's classification.
    // Scan B: a site that composes/parses the component-LogicalId `#` format itself, rather than
    // calling ComposeLogicalId/TryParseLogicalId.
    public class ListenerTargetBypassScanTests
    {
        private readonly record struct Match(string RelativePath, string Member, int Line, string Token);

        private static readonly string[] ScanRoots = { "SceneBuilder.Core", "com.codescenes" };

        private static IEnumerable<string> ProductionFiles(string repoRoot)
        {
            foreach (var scanRoot in ScanRoots)
            {
                var rootPath = Path.Combine(repoRoot, scanRoot);
                if (!Directory.Exists(rootPath))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                    if (relative.Contains("/obj/") || relative.Contains("/bin/"))
                    {
                        continue;
                    }

                    yield return relative;
                }
            }
        }

        private static string EnclosingMember(SyntaxToken token)
        {
            for (var node = token.Parent; node != null; node = node.Parent)
            {
                switch (node)
                {
                    case MethodDeclarationSyntax m: return m.Identifier.Text;
                    case LocalFunctionStatementSyntax l: return l.Identifier.Text;
                    case ConstructorDeclarationSyntax c: return c.Identifier.Text;
                    case PropertyDeclarationSyntax p: return p.Identifier.Text;
                    case TypeDeclarationSyntax t: return t.Identifier.Text;
                }
            }

            return "<top-level>";
        }

        // ---- Scan A: persistent-call target/object-argument tokens ---------------------------

        private static readonly string[] PersistentCallTargetTokens =
        {
            "m_Target", "m_TargetAssemblyTypeName", "m_ObjectArgument", "m_ObjectArgumentAssemblyTypeName",
            "m_PersistentCalls", "GetPersistentTarget",
        };

        // UnityEventReader.cs / UnityEventWriter.cs are deliberately NOT here: after
        // ModeArgBypassScanTests' Scan A closes to SceneBuilder.Core/Model/UnityEventProjection.cs
        // alone, neither adapter path may spell a persistent-call token, target or argument
        // included, so this allowlist agrees with that one about the same two files.
        private static readonly string[] PersistentCallTargetAllowlist =
        {
            "com.codescenes/Editor/ObjectReferenceResolver.cs",
            "SceneBuilder.Core/Model/UnityEventProjection.cs",
        };

        private static IEnumerable<Match> PersistentCallTargetTokensIn(string sourceText, string relativePath)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText, path: relativePath);
            var root = tree.GetRoot();

            foreach (var token in root.DescendantTokens())
            {
                var isIdentifierHit = token.Kind() == SyntaxKind.IdentifierToken
                    && PersistentCallTargetTokens.Contains(token.Text);
                var isStringHit = token.Kind() == SyntaxKind.StringLiteralToken
                    && token.Value is string s && PersistentCallTargetTokens.Contains(s);

                if (!isIdentifierHit && !isStringHit)
                {
                    continue;
                }

                var line = token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                yield return new Match(relativePath, EnclosingMember(token), line, token.ToString());
            }
        }

        private static IReadOnlyList<string> PersistentCallTargetViolations(
            IEnumerable<string> relativePaths, Func<string, string> readText)
        {
            var violations = new List<string>();
            foreach (var relativePath in relativePaths)
            {
                if (PersistentCallTargetAllowlist.Contains(relativePath))
                {
                    continue;
                }

                var matches = PersistentCallTargetTokensIn(readText(relativePath), relativePath).ToList();
                if (matches.Count == 0)
                {
                    continue;
                }

                violations.Add(
                    $"{relativePath} names a persistent-call target/argument token at " +
                    string.Join(", ", matches.Select(m => $"{m.Member}@{m.Line}:{m.Token}")) +
                    " but is not on the allowlist. Route this site's target/argument classification " +
                    "through SceneBuilder.Core.Identity.ComponentTargetResolution.Classify, or add the " +
                    "file to the allowlist.");
            }

            return violations;
        }

        [Fact]
        public void ScanA_ProductionSource_PersistentCallTargetTokenOnlyInAllowlistedFiles()
        {
            var repoRoot = RepoRootLocator.Find();
            var files = ProductionFiles(repoRoot).ToList();
            Assert.True(files.Count > 0, "scan found zero production .cs files -- repo root resolution is broken");

            var violations = PersistentCallTargetViolations(
                files, relativePath => File.ReadAllText(Path.Combine(repoRoot, relativePath)));

            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        [Fact]
        public void ScanA_UndeclaredFileNamingPersistentCallTarget_FailsNamingFileAndTokenAndRemedy()
        {
            const string relativePath = "com.codescenes/Editor/SomeOtherPass.cs";
            const string synthetic = @"
namespace SceneBuilder.Editor
{
    using UnityEditor;

    internal static class SomeOtherPass
    {
        private static SerializedProperty Target(SerializedProperty call) => call.FindPropertyRelative(""m_Target"");
    }
}
";
            var violations = PersistentCallTargetViolations(new[] { relativePath }, _ => synthetic);

            Assert.True(violations.Count == 1,
                $"expected exactly one violation for an undeclared file, found {violations.Count}: {string.Join(" | ", violations)}");
            Assert.Contains(relativePath, violations[0]);
            Assert.Contains("m_Target", violations[0]);
            Assert.Contains("ComponentTargetResolution", violations[0]);
        }

        [Fact]
        public void ScanA_AllowlistedFileWithNoTokens_IsPermitted()
        {
            const string relativePath = "SceneBuilder.Core/Model/UnityEventProjection.cs";
            Assert.Contains(relativePath, PersistentCallTargetAllowlist);

            var violations = PersistentCallTargetViolations(
                new[] { relativePath },
                _ => "namespace SceneBuilder.Core.Model { internal static class UnityEventProjection { } }");

            Assert.Empty(violations);
        }

        // ---- Scan B: the component-LogicalId '#' format ---------------------------------------

        // The format's owner plus the three surviving sites, each excluded here for a stated
        // reason. ComponentReconciler.cs, ReconcilerInstances.Nested.cs, ComponentPatchApplier.cs
        // and PlanExecutor.cs route through ComposeLogicalId/TryParseLogicalId/OwnerOfLogicalId and
        // carry no component-LogicalId format token of their own, so none of the four is
        // allowlisted. BuilderParser.cs and BuilderParser.Instance.cs are the same case and were
        // never on this list.
        private static readonly string[] ComponentLogicalIdFormatAllowlist =
        {
            "SceneBuilder.Core/Identity/ComponentTargetResolution.cs",
            "com.codescenes/Editor/InstanceOverrideExecutor.cs",
            "com.codescenes/Editor/SceneHierarchyPath.cs",
            "com.codescenes/Editor/PrefabInstanceProbe.cs",
        };

        private static IEnumerable<Match> ComponentLogicalIdFormatTokensIn(string sourceText, string relativePath)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText, path: relativePath);
            var root = tree.GetRoot();

            foreach (var token in root.DescendantTokens())
            {
                var isCharHit = token.Kind() == SyntaxKind.CharacterLiteralToken
                    && token.Value is char c && c == '#';
                var isInterpolatedTextHit = token.Kind() == SyntaxKind.InterpolatedStringTextToken
                    && token.Text.Contains('#');
                var isStringHit = token.Kind() == SyntaxKind.StringLiteralToken
                    && token.Value is string s && s == "#";

                if (!isCharHit && !isInterpolatedTextHit && !isStringHit)
                {
                    continue;
                }

                var line = token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                yield return new Match(relativePath, EnclosingMember(token), line, token.ToString());
            }
        }

        private static IReadOnlyList<string> ComponentLogicalIdFormatViolations(
            IEnumerable<string> relativePaths, Func<string, string> readText)
        {
            var violations = new List<string>();
            foreach (var relativePath in relativePaths)
            {
                if (ComponentLogicalIdFormatAllowlist.Contains(relativePath))
                {
                    continue;
                }

                var matches = ComponentLogicalIdFormatTokensIn(readText(relativePath), relativePath).ToList();
                if (matches.Count == 0)
                {
                    continue;
                }

                violations.Add(
                    $"{relativePath} names a '#' component-LogicalId format token at " +
                    string.Join(", ", matches.Select(m => $"{m.Member}@{m.Line}")) +
                    " but is not on the allowlist. Route this site through " +
                    "SceneBuilder.Core.Identity.ComponentTargetResolution.ComposeLogicalId/TryParseLogicalId, " +
                    "or add the file to the allowlist.");
            }

            return violations;
        }

        // Pins the "one owner" claim itself: BuilderParser.cs and BuilderParser.Instance.cs compose
        // a component LogicalId through ComposeLogicalId rather than interpolating `#{ordinal}`
        // locally, so neither needs a place on the allowlist.
        [Fact]
        public void ScanB_ProductionSource_ComponentLogicalIdFormatTokenOnlyInAllowlistedFiles()
        {
            var repoRoot = RepoRootLocator.Find();
            var files = ProductionFiles(repoRoot).ToList();
            Assert.True(files.Count > 0, "scan found zero production .cs files -- repo root resolution is broken");

            var violations = ComponentLogicalIdFormatViolations(
                files, relativePath => File.ReadAllText(Path.Combine(repoRoot, relativePath)));

            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        [Fact]
        public void ScanB_UndeclaredFileNamingTheFormat_FailsNamingFileAndTokenAndRemedy()
        {
            const string relativePath = "SceneBuilder.Core/Reconcile/SomeOtherReconcile.cs";
            const string synthetic = @"
namespace SceneBuilder.Core.Reconcile
{
    internal static class SomeOtherReconcile
    {
        private static string ComposeId(string owner, string type, int ordinal) => $""{owner}/{type}#{ordinal}"";
    }
}
";
            var violations = ComponentLogicalIdFormatViolations(new[] { relativePath }, _ => synthetic);

            Assert.True(violations.Count == 1,
                $"expected exactly one violation for an undeclared file, found {violations.Count}: {string.Join(" | ", violations)}");
            Assert.Contains(relativePath, violations[0]);
            Assert.Contains("ComponentTargetResolution", violations[0]);
        }

        [Fact]
        public void ScanB_AllowlistedFileWithNoTokens_IsPermitted()
        {
            const string relativePath = "com.codescenes/Editor/SceneHierarchyPath.cs";
            Assert.Contains(relativePath, ComponentLogicalIdFormatAllowlist);

            var violations = ComponentLogicalIdFormatViolations(
                new[] { relativePath },
                _ => "namespace SceneBuilder.Editor { internal static class SceneHierarchyPath { } }");

            Assert.Empty(violations);
        }
    }
}
