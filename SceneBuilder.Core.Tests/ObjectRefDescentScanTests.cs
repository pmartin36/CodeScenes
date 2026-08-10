using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A site-granular guard on "ONE mechanism owns descending into an ObjectRef": every raw
    // `ValueNode.ObjectRef` token in production source (SceneBuilder.Core/**, com.codescenes/**)
    // must either be declared here with a reason, or fail the scan. A new bypass in a new member
    // fails on being undeclared; an extra occurrence inside an already-declared member fails on
    // its count -- allowlisting cannot be file-granular, since several of these files also hold
    // legitimate declared sites.
    public class ObjectRefDescentScanTests
    {
        private sealed record Site(string RelativePath, string Member, int Count, string Reason);

        // The declared, legitimate inventory once every descent-shaped site routes through
        // SceneBuilder.Core.Model.ObjectRefValues. A (file, member) pair absent here allows ZERO
        // raw `ValueNode.ObjectRef` tokens.
        private static readonly Site[] DeclaredInventory =
        {
            new("com.codescenes/Editor/AssetReferenceResolver.cs", "ReadObjectReference", 1,
                "constructs the node from one live SerializedProperty; no container in hand"),
            new("com.codescenes/Editor/AssetReferenceResolver.cs", "ReadObjectReferenceValue", 1,
                "constructs the node from one live SerializedProperty; no container in hand"),
            new("com.codescenes/Editor/InstanceOverrideExecutor.cs", "WriteFieldValue", 1,
                "Unity delivers each array element as its own PropertyModification; an override value is never a container"),
            new("SceneBuilder.Core/Lowering/ObjectRefLowering.cs", "LowerObjectRef", 3,
                "lowers ONE already-descended ref; the descent is ObjectRefValues.Substitute"),
            new("SceneBuilder.Core/Materialize/Materializer.cs", "EmitFieldOp", 1,
                "matches a bare ObjectRef to emit SetReference"),
            new("SceneBuilder.Core/Materialize/Materializer.cs", "IsReferenceList", 2,
                "classifies each list item's kind for one-level per-index lowering, the depth the write boundary supports, not a descent"),
            new("SceneBuilder.Core/Model/ValueNode.cs", "ValueNode", 1,
                "the union's JsonDerivedType registration"),
            new("SceneBuilder.Core/Parsing/BuilderParser.Instance.cs", "ParseOverrideSet", 1,
                "routes one parsed override value; an override carries one property, never a container"),
            new("SceneBuilder.Core/Parsing/ValueNodeParser.cs", "Parse", 3,
                "constructs the node from one parsed expression"),
            new("SceneBuilder.Core/Reconcile/ComponentReconciler.cs", "RenderFieldValue", 1,
                "the per-ref render function ObjectRefValues.Substitute applies to one already-descended ref"),
            new("SceneBuilder.Core/Reconcile/NestedValueEmission.cs", "IsRepresentable", 1,
                "a per-node representability rule applied inside a ValueWalk.Map descent"),
            new("SceneBuilder.Core/Model/UnityEventListener.cs", "ValidateTarget", 1,
                "the listener target-slot kind rule: null | ObjectRef | AssetRef | Unsupported; one already-whole reference, no container"),
            new("SceneBuilder.Core/Model/UnityEventListener.cs", "ValidateArgValue", 1,
                "the object-mode argument's kind rule, the same one-reference test as the target slot"),
            new("SceneBuilder.Core/Identity/ComponentTargetResolution.cs", "ToValueNode", 2,
                "the ONE lowering of a classified listener reference into a listener's Target/ArgValue slot: SceneObject -> ObjectRef(LogicalId), Unresolvable -> ObjectRef(raw GlobalObjectId)"),
            new("SceneBuilder.Core/Parsing/BuilderParser.UnityEvents.cs", "ClassifyStaticArg", 1,
                "matches an already-lowered value's kind to pick ArgMode; one already-whole reference, no container, the same one-reference test ValidateArgValue applies"),
        };

        private static readonly string[] ScanRoots = { "SceneBuilder.Core", "com.codescenes" };

        private static string RepoRoot([CallerFilePath] string here = "")
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !File.Exists(Path.Combine(dir, "SceneBuilder.sln")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            if (dir == null)
            {
                throw new InvalidOperationException(
                    $"ObjectRefDescentScanTests.RepoRoot: no SceneBuilder.sln found walking up from '{here}'");
            }

            return dir;
        }

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

                    // The owner is exempt by name: its own body legitimately mentions the kind it
                    // is named for.
                    if (Path.GetFileName(file) == "ObjectRefValues.cs")
                    {
                        continue;
                    }

                    yield return relative;
                }
            }
        }

        // Every raw `ValueNode.ObjectRef` TOKEN in `sourceText`, with its enclosing member. A
        // syntax-only parse: comment/XML-doc prose carries no tokens at all, so trivia can never
        // satisfy this regardless of what English words it contains.
        private static IEnumerable<(string Member, int Line)> RawObjectRefTokens(string sourceText, string path)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText, path: path);
            var root = tree.GetRoot();

            foreach (var token in root.DescendantTokens())
            {
                if (token.Kind() != SyntaxKind.IdentifierToken || token.Text != "ObjectRef")
                {
                    continue;
                }

                var dot = token.GetPreviousToken();
                if (dot.Kind() != SyntaxKind.DotToken)
                {
                    continue;
                }

                var qualifier = dot.GetPreviousToken();
                if (qualifier.Text != "ValueNode")
                {
                    continue;
                }

                yield return (EnclosingMember(token), token.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
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

        [Fact]
        public void Scan_ProductionSource_MatchesDeclaredInventory_NoUndeclaredBypass()
        {
            var repoRoot = RepoRoot();
            Assert.True(
                File.Exists(Path.Combine(repoRoot, "SceneBuilder.sln")),
                $"resolved repo root '{repoRoot}' has no SceneBuilder.sln next to it");

            var files = ProductionFiles(repoRoot).ToList();
            Assert.True(files.Count > 0, "scan found zero production .cs files -- repo root resolution is broken");

            var actual = new Dictionary<(string File, string Member), int>();
            var totalSites = 0;
            foreach (var relativePath in files)
            {
                var fullPath = Path.Combine(repoRoot, relativePath);
                foreach (var (member, _) in RawObjectRefTokens(File.ReadAllText(fullPath), relativePath))
                {
                    (string File, string Member) key = (relativePath, member);
                    actual[key] = actual.GetValueOrDefault(key) + 1;
                    totalSites++;
                }
            }

            Assert.True(totalSites > 0, "scan found zero ValueNode.ObjectRef token sites -- the token match itself is broken");

            var expected = DeclaredInventory.ToDictionary(s => (File: s.RelativePath, Member: s.Member), s => s.Count);

            var mismatches = new List<string>();
            foreach ((string File, string Member) key in expected.Keys.Union(actual.Keys))
            {
                var expectedCount = expected.GetValueOrDefault(key, 0);
                var actualCount = actual.GetValueOrDefault(key, 0);
                if (expectedCount != actualCount)
                {
                    mismatches.Add(
                        $"{key.File} :: {key.Member} -- expected {expectedCount}, found {actualCount}. " +
                        "Route this site through SceneBuilder.Core.Model.ObjectRefValues, or declare it in DeclaredInventory with its reason.");
                }
            }

            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
        }

        // Proves the check is site-granular, not file-granular: a synthetic file labelled with an
        // already-declared path adds (a) a raw match inside a member the real inventory never
        // declares, and (b) an occurrence count beyond what a real declared member allows. Both
        // must be visible to the same scan the real-tree assertion above runs.
        [Fact]
        public void Scan_SyntheticViolation_DetectsUndeclaredMemberAndExtraOccurrenceInDeclaredMember()
        {
            const string relativePath = "SceneBuilder.Core/Lowering/ObjectRefLowering.cs";
            const string synthetic = @"
namespace SceneBuilder.Core.Lowering
{
    using SceneBuilder.Core.Model;

    internal static class ObjectRefLowering
    {
        private static bool Undeclared(ValueNode node) => node is ValueNode.ObjectRef;

        private static ValueNode LowerObjectRef(ValueNode.ObjectRef a, ValueNode.ObjectRef b, ValueNode.ObjectRef c, ValueNode.ObjectRef d) => a;
    }
}
";
            var byMember = RawObjectRefTokens(synthetic, relativePath)
                .GroupBy(t => t.Member)
                .ToDictionary(g => g.Key, g => g.Count());

            Assert.True(
                byMember.TryGetValue("Undeclared", out var undeclaredCount) && undeclaredCount == 1,
                "scan did not find the raw match inside a member the real inventory never declares");

            var declaredCount = DeclaredInventory.Single(s => s.Member == "LowerObjectRef").Count;
            Assert.True(
                byMember.TryGetValue("LowerObjectRef", out var lowerObjectRefCount) && lowerObjectRefCount != declaredCount,
                $"scan did not find the extra occurrence beyond the declared count ({declaredCount})");
        }

        [Fact]
        public void ProductionSource_StaysUnderFileSizeBudget()
        {
            var repoRoot = RepoRoot();
            var over = new List<string>();
            foreach (var relativePath in ProductionFiles(repoRoot))
            {
                var lineCount = File.ReadAllLines(Path.Combine(repoRoot, relativePath)).Length;
                if (lineCount > 1000)
                {
                    over.Add($"{relativePath}: {lineCount} lines");
                }
            }

            Assert.True(over.Count == 0, string.Join("\n", over));
        }
    }
}
