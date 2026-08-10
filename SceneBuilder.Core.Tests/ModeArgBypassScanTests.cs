using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Two count-free guards on "ONE mechanism (SceneBuilder.Core.Model.UnityEventProjection) owns
    // a persistent call's mode/argument table", modelled on ListenerTargetBypassScanTests: a
    // syntax-only Roslyn token walk, a permission-not-obligation allowlist for Scan A, and no
    // allowlist at all for Scan B.
    //
    // Scan A: a site naming the raw serialized mode/argument vocabulary (`m_Mode`, `m_CallState`,
    // `m_Arguments`, its four typed fields, `PersistentListenerMode`, and the
    // Add*/Set*/GetPersistentListenerState family) bypasses the projection.
    // Scan B: an adapter site naming the model's own mode enums (`ListenerArgMode` /
    // `ListenerCallState`) instead of consuming the already-projected `PersistentCallFields`.
    public class ModeArgBypassScanTests
    {
        private readonly record struct Match(string RelativePath, string Member, int Line, string Token);

        private static IEnumerable<string> ProductionFiles(string repoRoot, IReadOnlyList<string> scanRoots)
        {
            foreach (var scanRoot in scanRoots)
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

        // ---- Scan A: serialized persistent-call vocabulary --------------------------------------

        private static readonly string[] ScanARoots = { "SceneBuilder.Core", "com.codescenes" };

        // The non-serialized Unity API names a bypass could also hand-write instead of routing
        // through UnityEventProjection. Not a serialized spelling, so not part of PersistentCallPaths.
        private static readonly string[] ApiNameTokens =
        {
            "PersistentListenerMode", "SetPersistentListenerState", "GetPersistentListenerState",
            "AddVoidPersistentListener", "AddIntPersistentListener", "AddFloatPersistentListener",
            "AddStringPersistentListener", "AddBoolPersistentListener", "AddObjectPersistentListener",
        };

        // Derived from PersistentCallPaths.All rather than hand-maintained: extending the
        // vocabulary widens this guard automatically.
        private static readonly string[] ScanATokens =
            PersistentCallPaths.All.Concat(ApiNameTokens).ToArray();

        // Closed to Core's projection alone: PersistentCallPaths is the single owner of every
        // serialized spelling, so the two adapter paths (UnityEventWriter.cs, UnityEventReader.cs)
        // are no longer permitted to spell one themselves and were removed from this list.
        private static readonly string[] ScanAAllowlist =
        {
            "SceneBuilder.Core/Model/UnityEventProjection.cs",
        };

        private static IEnumerable<Match> ScanATokensIn(string sourceText, string relativePath)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText, path: relativePath);
            var root = tree.GetRoot();

            foreach (var token in root.DescendantTokens())
            {
                var isIdentifierHit = token.Kind() == SyntaxKind.IdentifierToken && ScanATokens.Contains(token.Text);
                var isStringHit = token.Kind() == SyntaxKind.StringLiteralToken
                    && token.Value is string s && ScanATokens.Contains(s);

                if (!isIdentifierHit && !isStringHit)
                {
                    continue;
                }

                var line = token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                yield return new Match(relativePath, EnclosingMember(token), line, token.ToString());
            }
        }

        private static IReadOnlyList<string> ScanAViolations(IEnumerable<string> relativePaths, Func<string, string> readText)
        {
            var violations = new List<string>();
            foreach (var relativePath in relativePaths)
            {
                if (ScanAAllowlist.Contains(relativePath))
                {
                    continue;
                }

                var matches = ScanATokensIn(readText(relativePath), relativePath).ToList();
                if (matches.Count == 0)
                {
                    continue;
                }

                violations.Add(
                    $"{relativePath} names a serialized persistent-call token at " +
                    string.Join(", ", matches.Select(m => $"{m.Member}@{m.Line}:{m.Token}")) +
                    " but is not on the allowlist. Route this site's mode/argument handling through " +
                    "SceneBuilder.Core.Model.UnityEventProjection, or add the file to the allowlist.");
            }

            return violations;
        }

        [Fact]
        public void ScanA_ProductionSource_SerializedTokenOnlyInAllowlistedFiles()
        {
            var repoRoot = RepoRootLocator.Find();
            var files = ProductionFiles(repoRoot, ScanARoots).ToList();
            Assert.True(files.Count > 0, "scan found zero production .cs files -- repo root resolution is broken");

            var violations = ScanAViolations(files, relativePath => File.ReadAllText(Path.Combine(repoRoot, relativePath)));

            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        [Fact]
        public void ScanA_UndeclaredFileNamingASerializedToken_FailsNamingFileTokenAndRemedy()
        {
            const string relativePath = "com.codescenes/Editor/SomeOtherPass.cs";
            const string synthetic = @"
namespace SceneBuilder.Editor
{
    using UnityEditor;

    internal static class SomeOtherPass
    {
        private static void Poke(SerializedProperty call) { call.FindPropertyRelative(""m_Mode"").intValue = 3; }
    }
}
";
            var violations = ScanAViolations(new[] { relativePath }, _ => synthetic);

            Assert.True(violations.Count == 1,
                $"expected exactly one violation for an undeclared file, found {violations.Count}: {string.Join(" | ", violations)}");
            Assert.Contains(relativePath, violations[0]);
            Assert.Contains("m_Mode", violations[0]);
            Assert.Contains("UnityEventProjection", violations[0]);
        }

        [Fact]
        public void ScanA_IdentifierFormOfTheToken_AlsoMatches()
        {
            const string relativePath = "com.codescenes/Editor/SomeOtherPass.cs";
            const string synthetic = @"
namespace SceneBuilder.Editor
{
    internal static class SomeOtherPass
    {
        private static int ReadMode(PersistentCall call) => call.m_Mode;
    }
}
";
            var violations = ScanAViolations(new[] { relativePath }, _ => synthetic);

            Assert.True(violations.Count == 1,
                $"expected exactly one violation, found {violations.Count}: {string.Join(" | ", violations)}");
            Assert.Contains("m_Mode", violations[0]);
        }

        [Fact]
        public void ScanA_TokenMentionedOnlyInAComment_DoesNotMatch()
        {
            const string relativePath = "com.codescenes/Editor/SomeOtherPass.cs";
            const string synthetic = @"
namespace SceneBuilder.Editor
{
    internal static class SomeOtherPass
    {
        // reads m_Mode from the persistent call
        private static void Poke() { }
    }
}
";
            var violations = ScanAViolations(new[] { relativePath }, _ => synthetic);

            Assert.Empty(violations);
        }

        [Fact]
        public void ScanA_AllowlistedFileWithNoTokens_IsPermitted()
        {
            const string relativePath = "SceneBuilder.Core/Model/UnityEventProjection.cs";
            Assert.Contains(relativePath, ScanAAllowlist);

            var violations = ScanAViolations(
                new[] { relativePath },
                _ => "namespace SceneBuilder.Core.Model { internal static class UnityEventProjection { } }");

            Assert.Empty(violations);
        }

        [Fact]
        public void ScanA_Allowlist_ContainsOnlyCoresProjection()
        {
            Assert.Equal(
                new[] { "SceneBuilder.Core/Model/UnityEventProjection.cs" },
                ScanAAllowlist);
        }

        [Fact]
        public void ScanA_TokenList_CoversEverySerializedSpellingInTheVocabulary()
        {
            Assert.NotEmpty(PersistentCallPaths.All);

            foreach (var spelling in PersistentCallPaths.All)
            {
                Assert.Contains(spelling, ScanATokens);
            }

            foreach (var apiName in ApiNameTokens)
            {
                Assert.Contains(apiName, ScanATokens);
            }
        }

        [Fact]
        public void ScanA_UnityEventWriterSpellingASerializedPathItself_IsNowAViolation()
        {
            const string relativePath = "com.codescenes/Editor/UnityEventWriter.cs";
            const string synthetic = @"
namespace SceneBuilder.Editor
{
    using UnityEditor;

    internal static class UnityEventWriter
    {
        private static void Write(SerializedProperty call) { call.FindPropertyRelative(""m_Mode"").intValue = 3; }
    }
}
";
            var violations = ScanAViolations(new[] { relativePath }, _ => synthetic);

            Assert.True(violations.Count == 1,
                $"expected exactly one violation, found {violations.Count}: {string.Join(" | ", violations)}");
            Assert.Contains(relativePath, violations[0]);
            Assert.Contains("m_Mode", violations[0]);
            Assert.Contains("UnityEventProjection", violations[0]);
        }

        [Fact]
        public void ScanA_UnityEventReaderHandWritingAModeTable_IsNowAViolation()
        {
            const string relativePath = "com.codescenes/Editor/UnityEventReader.cs";
            const string synthetic = @"
namespace SceneBuilder.Editor
{
    internal static class UnityEventReader
    {
        private static readonly int[] ModeTable = { PersistentListenerMode.EventDefined, PersistentListenerMode.Void };
        private const string ArgPath = ""m_IntArgument"";
    }
}
";
            var violations = ScanAViolations(new[] { relativePath }, _ => synthetic);

            Assert.True(violations.Count == 1,
                $"expected exactly one violation, found {violations.Count}: {string.Join(" | ", violations)}");
            Assert.Contains(relativePath, violations[0]);
        }

        [Fact]
        public void ScanA_NewlyGuardedTokenInAnUndeclaredFile_IsAViolation()
        {
            const string relativePath = "com.codescenes/Editor/SomeOtherPass.cs";
            const string synthetic = @"
namespace SceneBuilder.Editor
{
    using UnityEditor;

    internal static class SomeOtherPass
    {
        private static SerializedProperty AssemblyType(SerializedProperty call) =>
            call.FindPropertyRelative(""m_TargetAssemblyTypeName"");
    }
}
";
            var violations = ScanAViolations(new[] { relativePath }, _ => synthetic);

            Assert.True(violations.Count == 1,
                $"expected exactly one violation, found {violations.Count}: {string.Join(" | ", violations)}");
            Assert.Contains(relativePath, violations[0]);
            Assert.Contains("m_TargetAssemblyTypeName", violations[0]);
        }

        // ---- Scan B: the adapter never names the model's argument-mode enums --------------------

        private static bool IsInScanBScope(string relativePath) =>
            relativePath.StartsWith("com.codescenes/", StringComparison.Ordinal);

        private static IEnumerable<Match> ScanBTokensIn(string sourceText, string relativePath)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText, path: relativePath);
            var root = tree.GetRoot();

            foreach (var token in root.DescendantTokens())
            {
                if (token.Kind() != SyntaxKind.IdentifierToken
                    || (token.Text != "ListenerArgMode" && token.Text != "ListenerCallState"))
                {
                    continue;
                }

                var line = token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                yield return new Match(relativePath, EnclosingMember(token), line, token.Text);
            }
        }

        private static IReadOnlyList<string> ScanBViolations(IEnumerable<string> relativePaths, Func<string, string> readText)
        {
            var violations = new List<string>();
            foreach (var relativePath in relativePaths)
            {
                if (!IsInScanBScope(relativePath))
                {
                    continue;
                }

                var matches = ScanBTokensIn(readText(relativePath), relativePath).ToList();
                if (matches.Count == 0)
                {
                    continue;
                }

                violations.Add(
                    $"{relativePath} names the model's argument-mode enum at " +
                    string.Join(", ", matches.Select(m => $"{m.Member}@{m.Line}:{m.Token}")) +
                    " -- the adapter must consume SceneBuilder.Core.Model.PersistentCallFields, never " +
                    "branch on the mode/callstate enum itself.");
            }

            return violations;
        }

        [Fact]
        public void ScanB_ProductionSource_ArgModeTokenNeverAppearsUnderTheAdapter()
        {
            var repoRoot = RepoRootLocator.Find();
            var files = ProductionFiles(repoRoot, new[] { "com.codescenes" }).ToList();
            Assert.True(files.Count > 0, "scan found zero production .cs files -- repo root resolution is broken");

            var violations = ScanBViolations(files, relativePath => File.ReadAllText(Path.Combine(repoRoot, relativePath)));

            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        [Fact]
        public void ScanB_AdapterFileNamingTheModelEnum_FailsNamingFileTokenAndRemedy()
        {
            const string relativePath = "com.codescenes/Editor/SomePass.cs";
            const string synthetic = @"
namespace SceneBuilder.Editor
{
    using SceneBuilder.Core.Model;

    internal static class SomePass
    {
        private static void Handle(ListenerArgMode mode) { }
    }
}
";
            var violations = ScanBViolations(new[] { relativePath }, _ => synthetic);

            Assert.True(violations.Count == 1,
                $"expected exactly one violation, found {violations.Count}: {string.Join(" | ", violations)}");
            Assert.Contains(relativePath, violations[0]);
            Assert.Contains("ListenerArgMode", violations[0]);
        }

        [Fact]
        public void ScanB_TheSameSourceUnderCoreParsing_IsNotFlagged_ScanIsAdapterScoped()
        {
            const string relativePath = "SceneBuilder.Core/Parsing/SomeParser.cs";
            const string synthetic = @"
namespace SceneBuilder.Core.Parsing
{
    using SceneBuilder.Core.Model;

    internal static class SomeParser
    {
        private static void Handle(ListenerArgMode mode) { }
    }
}
";
            var violations = ScanBViolations(new[] { relativePath }, _ => synthetic);

            Assert.Empty(violations);
        }
    }
}
