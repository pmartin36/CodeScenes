using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A site-granular guard on "ONE mechanism owns descending into a ValueNode container": every
    // raw `ValueNode.List` / `ValueNode.Nested` token in production source (SceneBuilder.Core/**,
    // com.codescenes/**) must either be declared here with a reason, or fail the scan. A new
    // hand-rolled switch in a new member fails on being undeclared; an extra occurrence inside an
    // already-declared member fails on its count.
    public class ValueContainerDescentScanTests
    {
        private sealed record Site(string RelativePath, string Member, int Count, string Reason);
        private readonly record struct Match(string RelativePath, string Member, int Line, string Pattern);

        // The declared, legitimate inventory of sites whose container handling is not expressed
        // through SceneBuilder.Core.Model.ValueWalk. A (file, member) pair absent here allows ZERO
        // `ValueNode.List` / `ValueNode.Nested` tokens. Reconciled against a live scan of the tree,
        // so its counts move when production moves.
        private static IReadOnlyList<Site> DeclaredInventory { get; } = new[]
        {
            new Site("com.codescenes/Editor/SerializedEnumNormalizer.cs", "NormalizeNode", 1,
                "the per-node arm of a walk callback: it rewrites THIS Nested node's TypeName spelling and returns it. The members are the walk's business, not this arm's."),
            new Site("com.codescenes/Editor/SerializedFieldBridge.cs", "ContainsUnsupported", 1,
                "List-only representability filter, not a value descent. ValueWalk.Any descends Nested members as well, so routing it through ValueWalk would drop a whole struct field that carries one unrepresentable member, where NestedValueEmission.Project excludes that member and round-trips the rest. Left as it stands."),
            new Site("com.codescenes/Editor/SerializedFieldBridge.cs", "EnterNode", 2,
                "Names both container kinds to do a node's OWN write work, sizing the array before its elements are entered and resolving the struct type that node's members map through. The recursion is ValueWalk.Descend's; no child property is derived here."),
            new Site("com.codescenes/Editor/SerializedFieldBridge.cs", "ReadList", 1,
                "Construct a container node from a live SerializedProperty tree. What is walked is the property tree, not a ValueNode, so ValueWalk has no shape for it."),
            new Site("com.codescenes/Editor/SerializedFieldBridge.cs", "ReadNested", 1,
                "Construct a container node from a live SerializedProperty tree. What is walked is the property tree, not a ValueNode, so ValueWalk has no shape for it."),
            new Site("SceneBuilder.Core/Materialize/Materializer.cs", "EmitFieldOp", 1,
                "emits one plan op per reference-list index at the depth the write boundary supports; the recursion carries a PLAN PATH and appends ops rather than producing a value, a shape no ValueWalk primitive expresses."),
            new Site("SceneBuilder.Core/Materialize/Materializer.cs", "IsReferenceList", 1,
                "classifies a list's items for that one-level per-index lowering, not a descent."),
            new Site("SceneBuilder.Core/Model/ValueNode.cs", "ValueNode", 2,
                "the union's JsonDerivedType registration."),
            new Site("SceneBuilder.Core/Parsing/ValueNodeParser.cs", "ParseNested", 1,
                "construct the node from one parsed C# initializer; what is walked is the syntax tree, not a ValueNode."),
            new Site("SceneBuilder.Core/Parsing/ValueNodeParser.cs", "ParseList", 1,
                "construct the node from one parsed C# initializer; what is walked is the syntax tree, not a ValueNode."),
            new Site("SceneBuilder.Core/Parsing/BuilderParser.SerializeReference.cs", "ParseManagedRefList", 1,
                "construct the node from one parsed C# initializer; what is walked is the syntax tree, not a ValueNode."),
            new Site("SceneBuilder.Core/Reconcile/ComponentReconciler.cs", "AuthoredTextIsCurrent", 4,
                "a PAIRED walk comparing two values position-by-position; every ValueWalk primitive takes ONE node, so none expresses it."),
            new Site("SceneBuilder.Core/Reconcile/ComponentReconciler.cs", "ReconcileComponents", 2,
                "two top-level kind tests choosing the per-MEMBER default rule (NestedValueEmission.Project) over whole-value equality for a Nested field; neither descends."),
            new Site("SceneBuilder.Core/Reconcile/ComponentReconciler.Defaults.cs", "IsAtDefault", 1,
                "one top-level kind test, routing a Nested field to NestedValueEmission.Project; no descent."),
            new Site("SceneBuilder.Core/Reconcile/ComponentReconciler.Defaults.cs", "OmitDefaults", 1,
                "one top-level kind test, routing a Nested field to NestedValueEmission.Project; no descent."),
            new Site("SceneBuilder.Core/Reconcile/ManagedReferenceFieldCompletion.cs", "IsUnwrittenMemberDefault", 1,
                "one top-level kind+emptiness test on a bare member already selected from the instance's own FieldMap; no descent into items."),
            new Site("SceneBuilder.Core/Reconcile/ListValueEmission.cs", "ArrayPrefix", 1,
                "a parameter TYPE, not a match: the emitter is handed the List node whose prefix it names."),
            new Site("SceneBuilder.Core/Reconcile/ListValueEmission.cs", "EmittedTypeToken", 2,
                "names ONE item's rendered C# type; the List arm returns a token and never looks inside."),
            new Site("SceneBuilder.Core/Reconcile/ListValueEmission.cs", "HasUnemittableItem", 1,
                "the per-node predicate INSIDE a ValueWalk.Any descent; the descent is ValueWalk's."),
            new Site("SceneBuilder.Core/Reconcile/NestedValueEmission.cs", "Complete", 7,
                "the one nested pass ValueWalk.Map cannot express: its result's key set is the UNION of the value's and the default's, so it ADDS members rather than mapping the ones present (NestedValueEmission.cs:250-252 states this in the production comment)."),
            new Site("SceneBuilder.Core/Reconcile/NestedValueEmission.cs", "ExcludeUnrepresentable", 1,
                "one top-level kind guard before handing the node to ValueWalk.Map; the descent is Map's."),
            new Site("SceneBuilder.Core/Reconcile/NestedValueEmission.cs", "IsRepresentable", 1,
                "a per-node representability rule applied inside a ValueWalk.Map descent."),
            new Site("SceneBuilder.Core/Reconcile/NestedValueEmission.cs", "Project", 2,
                "two top-level kind tests deciding whether the projected result is an EMPTY nested node; the descent belongs to Reduce and ExcludeUnrepresentable."),
            new Site("SceneBuilder.Core/Reconcile/NestedValueEmission.cs", "Reduce", 5,
                "one top-level guard plus per-node and per-context arms inside ValueWalk.Map's callbacks (paired-default threading); the descent is Map's."),
        };

        private static readonly string[] ScanRoots = { "SceneBuilder.Core", "com.codescenes" };

        // The one site exempt by relative path (not bare filename): its own body legitimately
        // descends the containers it is named for.
        private const string ValueWalkRelativePath = "SceneBuilder.Core/Model/ValueWalk.cs";

        private static readonly HashSet<string> ContainerIdentifiers = new() { "List", "Nested" };

        // Every production `.cs` file under ScanRoots, relative to repoRoot, excluding obj/bin
        // output and SceneBuilder.Core/Model/ValueWalk.cs itself (by relative path, not bare
        // filename — a second file sharing that name elsewhere must not inherit the exemption).
        private static IEnumerable<string> ProductionFiles(string repoRoot) =>
            SourceScanPlumbing.ProductionFiles(repoRoot, ScanRoots, rel => rel == ValueWalkRelativePath);

        // Every `ValueNode.List` / `ValueNode.Nested` qualified-identifier TOKEN in `sourceText`, in
        // ANY syntactic position, with its enclosing member and line. A syntax-only parse: comment
        // and XML-doc trivia carry no tokens, so prose mentioning either name can never match.
        private static IEnumerable<Match> ContainerTokens(string sourceText, string relativePath) =>
            SourceScanPlumbing.QualifiedTokens(sourceText, relativePath, "ValueNode", ContainerIdentifiers)
                .Select(t => new Match(relativePath, t.Member, t.Line, "ValueNode." + t.Identifier));

        // One message per (file, member) whose actual token count differs from its declared count,
        // scoped to the files `matches` actually covers -- a declared site whose file was never
        // fed through ContainerTokens is out of scope for this call, not silently satisfied.
        // Names the file, the member, the matched pattern(s) with line(s), and the remedy.
        private static IReadOnlyList<string> Violations(IEnumerable<Match> matches, IReadOnlyCollection<Site> inventory)
        {
            var byKey = new Dictionary<(string RelativePath, string Member), List<Match>>();
            var scannedPaths = new HashSet<string>();
            foreach (var match in matches)
            {
                scannedPaths.Add(match.RelativePath);
                var key = (match.RelativePath, match.Member);
                if (!byKey.TryGetValue(key, out var list))
                {
                    list = new List<Match>();
                    byKey[key] = list;
                }

                list.Add(match);
            }

            var expected = inventory
                .Where(s => scannedPaths.Contains(s.RelativePath))
                .ToDictionary(s => (s.RelativePath, s.Member), s => s.Count);

            var violations = new List<string>();
            foreach (var key in expected.Keys.Union(byKey.Keys))
            {
                var expectedCount = expected.GetValueOrDefault(key, 0);
                var actual = byKey.GetValueOrDefault(key, new List<Match>());
                if (expectedCount == actual.Count)
                {
                    continue;
                }

                var found = actual.Count == 0
                    ? "none"
                    : string.Join(", ", actual.Select(m => $"{m.Pattern}@{m.Line}"));

                violations.Add(
                    $"{key.RelativePath} :: {key.Member} — expected {expectedCount} container-kind token(s), " +
                    $"found {actual.Count} ({found}). Route this site's container descent through " +
                    "SceneBuilder.Core.Model.ValueWalk, or declare it in DeclaredInventory with its reason.");
            }

            return violations;
        }

        [Fact]
        public void Scan_ProductionSource_MatchesDeclaredInventory_NoUndeclaredDescent()
        {
            var repoRoot = RepoRootLocator.Find();
            var files = ProductionFiles(repoRoot).ToList();
            Assert.True(files.Count > 0, "scan found zero production .cs files -- repo root resolution is broken");

            var matches = new List<Match>();
            foreach (var relativePath in files)
            {
                var fullPath = Path.Combine(repoRoot, relativePath);
                matches.AddRange(ContainerTokens(File.ReadAllText(fullPath), relativePath));
            }

            Assert.True(matches.Count > 0,
                "scan found zero ValueNode.List / ValueNode.Nested token sites -- the token match itself is broken");

            var violations = Violations(matches, DeclaredInventory);
            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        private const string SwitchExpressionSyntheticSource = @"
namespace SceneBuilder.Core.Model
{
    using SceneBuilder.Core.Model;

    internal static class Synthetic
    {
        private static int SwitchArm(ValueNode node) => node switch
        {
            ValueNode.List list => list.Items.Count,
            _ => 0,
        };

        private static int CaseForm(ValueNode node)
        {
            switch (node)
            {
                case ValueNode.Nested n:
                    return 1;
                default:
                    return 0;
            }
        }

        private static bool IsForm(ValueNode node) => node is ValueNode.List;

        private static int ParamForm(ValueNode.Nested node) => 0;

        private static ValueNode NewForm() => new ValueNode.List();

        /// <see cref=""ValueNode.List""/> is only documentation prose, never a real token.
        private static int TriviaOnly() => 0;
    }
}
";

        [Fact]
        public void Scan_SwitchExpressionArm_IsMatched_NotOnlyCaseAndIsForms()
        {
            var byMember = ContainerTokens(SwitchExpressionSyntheticSource, "Synthetic.cs")
                .GroupBy(m => m.Member)
                .ToDictionary(g => g.Key, g => g.ToList());

            Assert.True(
                byMember.TryGetValue("SwitchArm", out var switchArm) && switchArm.Count == 1 && switchArm[0].Pattern == "ValueNode.List",
                "switch-expression arm 'ValueNode.List list =>' was not matched as 'ValueNode.List'");
            Assert.True(
                byMember.TryGetValue("CaseForm", out var caseForm) && caseForm.Count == 1 && caseForm[0].Pattern == "ValueNode.Nested",
                "'case ValueNode.Nested n:' was not matched as 'ValueNode.Nested'");
            Assert.True(
                byMember.TryGetValue("IsForm", out var isForm) && isForm.Count == 1 && isForm[0].Pattern == "ValueNode.List",
                "'node is ValueNode.List' was not matched as 'ValueNode.List'");
            Assert.True(
                byMember.TryGetValue("ParamForm", out var paramForm) && paramForm.Count == 1 && paramForm[0].Pattern == "ValueNode.Nested",
                "parameter type 'ValueNode.Nested node' was not matched as 'ValueNode.Nested'");
            Assert.True(
                byMember.TryGetValue("NewForm", out var newForm) && newForm.Count == 1 && newForm[0].Pattern == "ValueNode.List",
                "'new ValueNode.List(...)' was not matched as 'ValueNode.List'");
            Assert.False(
                byMember.ContainsKey("TriviaOnly"),
                "an XML doc comment mentioning ValueNode.List must not be matched -- trivia carries no tokens");
        }

        private const string ReintroducedSwitchSource = @"
namespace SceneBuilder.Core.Lowering
{
    using SceneBuilder.Core.Model;

    internal static class AssetRefLowering
    {
        private static ValueNode LowerNode(ValueNode node)
        {
            switch (node)
            {
                case ValueNode.List list:
                    return list;
                case ValueNode.Nested nested:
                    return nested;
                default:
                    return node;
            }
        }
    }
}
";

        [Fact]
        public void Scan_ReintroducedHandRolledSwitch_FailsNamingFileAndPatternAndRemedy()
        {
            const string relativePath = "SceneBuilder.Core/Lowering/AssetRefLowering.cs";
            var matches = ContainerTokens(ReintroducedSwitchSource, relativePath).ToList();
            var violations = Violations(matches, DeclaredInventory);

            Assert.True(violations.Count == 1,
                $"expected exactly one violation for a reintroduced hand-rolled switch, found {violations.Count}: {string.Join(" | ", violations)}");

            var message = violations[0];
            Assert.Contains(relativePath, message);
            Assert.Contains("LowerNode", message);
            Assert.Contains("ValueNode.List", message);
            Assert.Contains("ValueNode.Nested", message);
            Assert.Contains("ValueWalk", message);
        }

        [Fact]
        public void DeclaredInventory_EveryEntryCarriesAWrittenReason()
        {
            foreach (var site in DeclaredInventory)
            {
                Assert.True(!string.IsNullOrWhiteSpace(site.Reason),
                    $"{site.RelativePath} :: {site.Member} declares no written reason");
                Assert.True(site.Count > 0,
                    $"{site.RelativePath} :: {site.Member} declares Count <= 0, which allows zero tokens and does not belong in the inventory");
            }
        }

        // A count-free, PER-FILE guard on `ValueNode.UnityEventListeners`: unlike the count-keyed
        // List/Nested inventory above, a listener consumer's row cannot be pre-seeded with an exact
        // count before that consumer's own member exists. A file is permitted to name the kind iff
        // it OWNS the kind by name (every UnityEvent-specific file this feature adds is
        // `*UnityEvent*.cs`), or its relative path is on the allowlist below -- seeded once with
        // every consumer this feature creates and never edited again. An allowlisted file with ZERO
        // occurrences is fine: the allowlist grants PERMISSION, not obligation, which is what makes
        // its rows pre-seedable ahead of source not yet written.
        private static readonly string[] UnityEventListenersTokenAllowlist =
        {
            "SceneBuilder.Core/Model/ValueNode.cs",
            "SceneBuilder.Core/Model/ValueWalk.cs",
            "SceneBuilder.Core/Diff/ChangeOp.cs",
            "SceneBuilder.Core/Diff/Differ.cs",
            "SceneBuilder.Core/Materialize/Materializer.cs",
            "SceneBuilder.Core/Plan/PlanOp.cs",
            "SceneBuilder.Core/Plan/PlanOps.cs",
            "SceneBuilder.Core/Parsing/BuilderParser.cs",
            "SceneBuilder.Core/Reconcile/Reconciler.cs",
            "SceneBuilder.Core/Reconcile/ComponentReconciler.cs",
            "SceneBuilder.Core/Reconcile/SourcePatchApplier.cs",
            "SceneBuilder.Core/Reconcile/SourceExpr.cs",
            "SceneBuilder.Core/Reconcile/ComponentPatchApplier.cs",
            "com.codescenes/Editor/SerializedFieldBridge.cs",
            "com.codescenes/Editor/PlanExecutor.cs",
        };

        private static bool IsUnityEventListenersTokenPermitted(string relativePath) =>
            Path.GetFileName(relativePath).Contains("UnityEvent", StringComparison.Ordinal)
            || UnityEventListenersTokenAllowlist.Contains(relativePath);

        private static readonly HashSet<string> UnityEventListenersIdentifiers = new() { "UnityEventListeners" };

        // A sibling of ContainerTokens matching the third container kind: the same syntax-only
        // qualifier-dot-identifier shape, kept as its own scanner so a newly permitted file's
        // tokens never perturb the count-keyed List/Nested inventory above.
        private static IEnumerable<Match> UnityEventListenersTokens(string sourceText, string relativePath) =>
            SourceScanPlumbing.QualifiedTokens(sourceText, relativePath, "ValueNode", UnityEventListenersIdentifiers)
                .Select(t => new Match(relativePath, t.Member, t.Line, "ValueNode.UnityEventListeners"));

        private static IReadOnlyList<string> UnityEventListenersViolations(
            IEnumerable<string> relativePaths, Func<string, string> readText)
        {
            var violations = new List<string>();
            foreach (var relativePath in relativePaths)
            {
                if (IsUnityEventListenersTokenPermitted(relativePath))
                {
                    continue;
                }

                var matches = UnityEventListenersTokens(readText(relativePath), relativePath).ToList();
                if (matches.Count == 0)
                {
                    continue;
                }

                violations.Add(
                    $"{relativePath} names ValueNode.UnityEventListeners at " +
                    string.Join(", ", matches.Select(m => $"{m.Member}@{m.Line}")) +
                    " but is neither a *UnityEvent*.cs file nor on the allowlist. Route this site's " +
                    "container descent through SceneBuilder.Core.Model.ValueWalk, or add the file to the allowlist.");
            }

            return violations;
        }

        [Fact]
        public void Scan_ProductionSource_UnityEventListenersTokenOnlyInPermittedFiles()
        {
            var repoRoot = RepoRootLocator.Find();
            var files = ProductionFiles(repoRoot).ToList();
            Assert.True(files.Count > 0, "scan found zero production .cs files -- repo root resolution is broken");

            var violations = UnityEventListenersViolations(
                files, relativePath => File.ReadAllText(Path.Combine(repoRoot, relativePath)));

            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        [Fact]
        public void Scan_UndeclaredFileNamingUnityEventListeners_FailsNamingFileAndKindAndRemedy()
        {
            const string relativePath = "com.codescenes/Editor/SomePass.cs";
            const string synthetic = @"
namespace SceneBuilder.Core.Whatever
{
    using SceneBuilder.Core.Model;

    internal static class SomePass
    {
        private static bool IsListeners(ValueNode node) => node is ValueNode.UnityEventListeners;
    }
}
";
            var violations = UnityEventListenersViolations(new[] { relativePath }, _ => synthetic);

            Assert.True(violations.Count == 1,
                $"expected exactly one violation for an undeclared file, found {violations.Count}: {string.Join(" | ", violations)}");

            var message = violations[0];
            Assert.Contains(relativePath, message);
            Assert.Contains("UnityEventListeners", message);
            Assert.Contains("ValueWalk", message);
        }

        [Fact]
        public void Scan_AllowlistedFileWithNoTokens_IsPermitted()
        {
            const string relativePath = "SceneBuilder.Core/Materialize/Materializer.cs";
            Assert.Contains(relativePath, UnityEventListenersTokenAllowlist);

            var violations = UnityEventListenersViolations(
                new[] { relativePath },
                _ => "namespace SceneBuilder.Core.Materialize { internal static class Materializer { } }");

            Assert.Empty(violations);
        }

        // A sibling of the UnityEventListeners guard above, matching `ValueNode.ManagedReference`.
        // Permitted by filename (any file naming "ManagedRef" or "SerializeReference" — this
        // feature's own partials) or by the seeded allowlist below, which is grown ONCE for every
        // production consumer this feature (b1+b2) is known to add and never edited again; an
        // allowlisted file with zero occurrences is fine, since the allowlist grants permission, not
        // obligation.
        private static readonly string[] ManagedReferenceTokenAllowlist =
        {
            "SceneBuilder.Core/Model/ValueNode.cs",
            "SceneBuilder.Core/Model/ValueWalk.cs",
            "SceneBuilder.Core/Diff/ChangeOp.cs",
            "SceneBuilder.Core/Diff/Differ.cs",
            "SceneBuilder.Core/Plan/PlanOp.cs",
            "SceneBuilder.Core/Plan/PlanOps.cs",
            "SceneBuilder.Core/Materialize/Materializer.cs",
            "SceneBuilder.Core/Reconcile/SourceExpr.cs",
            "SceneBuilder.Core/Reconcile/Reconciler.cs",
            "SceneBuilder.Core/Reconcile/ComponentReconciler.cs",
            "SceneBuilder.Core/Reconcile/ComponentPatchApplier.cs",
            "SceneBuilder.Core/Reconcile/NestedValueEmission.cs",
            "SceneBuilder.Core/Reconcile/ListValueEmission.cs",
            "SceneBuilder.Core/Lowering/AssetRefLowering.cs",
            "SceneBuilder.Core/Model/ObjectRefValues.cs",
            "SceneBuilder.Core/Parsing/BuilderParser.cs",
            "SceneBuilder.Core/Parsing/ValueNodeParser.cs",
            "SceneBuilder.Core/Validation/PlanningValidator.cs",
            "com.codescenes/Editor/SerializedFieldBridge.cs",
            "com.codescenes/Editor/PlanExecutor.cs",
        };

        private static bool IsManagedReferenceTokenPermitted(string relativePath) =>
            Path.GetFileName(relativePath).Contains("ManagedRef", StringComparison.Ordinal)
            || Path.GetFileName(relativePath).Contains("SerializeReference", StringComparison.Ordinal)
            || ManagedReferenceTokenAllowlist.Contains(relativePath);

        private static readonly HashSet<string> ManagedReferenceIdentifiers = new() { "ManagedReference" };

        private static IEnumerable<Match> ManagedReferenceTokens(string sourceText, string relativePath) =>
            SourceScanPlumbing.QualifiedTokens(sourceText, relativePath, "ValueNode", ManagedReferenceIdentifiers)
                .Select(t => new Match(relativePath, t.Member, t.Line, "ValueNode.ManagedReference"));

        private static IReadOnlyList<string> ManagedReferenceViolations(
            IEnumerable<string> relativePaths, Func<string, string> readText)
        {
            var violations = new List<string>();
            foreach (var relativePath in relativePaths)
            {
                if (IsManagedReferenceTokenPermitted(relativePath))
                {
                    continue;
                }

                var matches = ManagedReferenceTokens(readText(relativePath), relativePath).ToList();
                if (matches.Count == 0)
                {
                    continue;
                }

                violations.Add(
                    $"{relativePath} names ValueNode.ManagedReference at " +
                    string.Join(", ", matches.Select(m => $"{m.Member}@{m.Line}")) +
                    " but is neither a *ManagedRef*/*SerializeReference*.cs file nor on the allowlist. " +
                    "Route this site's container descent through SceneBuilder.Core.Model.ValueWalk, or add the file to the allowlist.");
            }

            return violations;
        }

        [Fact]
        public void Scan_ProductionSource_ManagedReferenceTokenOnlyInPermittedFiles()
        {
            var repoRoot = RepoRootLocator.Find();
            var files = ProductionFiles(repoRoot).ToList();
            Assert.True(files.Count > 0, "scan found zero production .cs files -- repo root resolution is broken");

            var violations = ManagedReferenceViolations(
                files, relativePath => File.ReadAllText(Path.Combine(repoRoot, relativePath)));

            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        [Fact]
        public void Scan_UndeclaredFileNamingManagedReference_FailsNamingFileAndKindAndRemedy()
        {
            const string relativePath = "com.codescenes/Editor/SomePass.cs";
            const string synthetic = @"
namespace SceneBuilder.Core.Whatever
{
    using SceneBuilder.Core.Model;

    internal static class SomePass
    {
        private static bool IsManagedRef(ValueNode node) => node is ValueNode.ManagedReference;
    }
}
";
            var violations = ManagedReferenceViolations(new[] { relativePath }, _ => synthetic);

            Assert.True(violations.Count == 1,
                $"expected exactly one violation for an undeclared file, found {violations.Count}: {string.Join(" | ", violations)}");

            var message = violations[0];
            Assert.Contains(relativePath, message);
            Assert.Contains("ManagedReference", message);
            Assert.Contains("ValueWalk", message);
        }

        [Fact]
        public void Scan_AllowlistedFileWithNoTokens_IsPermitted_ManagedReference()
        {
            const string relativePath = "SceneBuilder.Core/Materialize/Materializer.cs";
            Assert.Contains(relativePath, ManagedReferenceTokenAllowlist);

            var violations = ManagedReferenceViolations(
                new[] { relativePath },
                _ => "namespace SceneBuilder.Core.Materialize { internal static class Materializer { } }");

            Assert.Empty(violations);
        }
    }
}
