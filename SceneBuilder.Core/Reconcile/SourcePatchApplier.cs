using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // Applies a SourcePatch's SourceEdits to builder .cs source via Roslyn syntax-node
    // replacement, preserving all unrelated trivia (comments, blank lines, formatting).
    public static partial class SourcePatchApplier
    {
        private static readonly string[] TransformPositionalArgs = { "pos", "rot", "scale" };

        public static string Apply(
            string source,
            SourcePatch patch,
            IReadOnlyDictionary<string, SourceSpan> anchors)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = (CompilationUnitSyntax)tree.GetRoot();

            // Resolve every edit's target node(s) against the ORIGINAL (unmutated) tree first,
            // then compose all edits via TrackNodes so earlier edits don't invalidate later targets.
            var allTargets = new List<SyntaxNode>();
            var appliers = new List<Func<SyntaxNode, SyntaxNode>>();

            // One annotation per AppendStatement in this batch, keyed by NewLogicalId, so a
            // same-batch child can locate its parent's freshly-inserted statement even though the
            // Applier otherwise resolves every edit against the original unmutated tree.
            var appendAnnotations = new Dictionary<string, SyntaxAnnotation>();
            foreach (var appendEdit in patch.Edits.OfType<AppendStatement>())
            {
                appendAnnotations[appendEdit.NewLogicalId] = new SyntaxAnnotation();
            }

            // Tracks the most-recently-resolved same-batch sibling under each fresh parent, so
            // multiple children appended under one new-subtree parent preserve emission order.
            var lastSiblingByParent = new Dictionary<string, SyntaxAnnotation>();

            // A pos/rot/scale PatchArgument needs a `.Transform(...)` call to patch. When the
            // authored statement has NONE — the everyday case: the user drags an object authored as
            // a plain `scene.Add("X")` — the call must be INTRODUCED, exactly as an absent flag call
            // is (IntroduceFlagCall). Resolved HERE, once, before the main loop, because all of one
            // anchor's transform args must become a SINGLE `.Transform(pos: ..., rot: ...)` call; a
            // per-edit introduction would chain three separate `.Transform(...)` calls for a
            // pos+rot+scale batch, each clobbering the last.
            var introducedTransformEdits = ResolveTransformIntroductions(root, anchors, patch, allTargets, appliers);

            foreach (var edit in patch.Edits)
            {
                switch (edit)
                {
                    case PatchArgument patchArgument:
                        // Already folded into an introduced `.Transform(...)` call above.
                        if (introducedTransformEdits.Contains(patchArgument))
                        {
                            break;
                        }

                        ResolvePatchArgument(root, anchors, patchArgument, allTargets, appliers);
                        break;
                    case MoveStatement moveStatement:
                        ResolveMoveStatement(root, anchors, moveStatement, allTargets, appliers);
                        break;
                    case ReorderStatement reorderStatement:
                        ResolveReorderStatement(root, anchors, reorderStatement, allTargets, appliers);
                        break;
                    case RemoveStatement removeStatement:
                        ResolveRemoveStatement(root, anchors, removeStatement, allTargets, appliers);
                        break;
                    case AppendStatement appendStatement:
                        ResolveAppendStatement(root, anchors, appendStatement, appendAnnotations, lastSiblingByParent, allTargets, appliers);
                        break;
                    case AppendComponentStatement appendComponentStatement:
                        ResolveAppendComponentStatement(root, anchors, appendComponentStatement, appendAnnotations, lastSiblingByParent, allTargets, appliers);
                        break;
                    case PatchFlagArgument patchFlagArgument:
                        ResolvePatchFlagArgument(root, anchors, patchFlagArgument, allTargets, appliers);
                        break;
                    case IntroduceFlagCall introduceFlagCall:
                        ResolveIntroduceFlagCall(root, anchors, introduceFlagCall, allTargets, appliers);
                        break;
                    case RemoveFlagCall removeFlagCall:
                        ResolveRemoveFlagCall(root, anchors, removeFlagCall, allTargets, appliers);
                        break;
                    case PatchComponentField patchComponentField:
                        ResolvePatchComponentField(root, anchors, patchComponentField, allTargets, appliers);
                        break;
                    case IntroduceComponentField introduceComponentField:
                        ResolveIntroduceComponentField(root, anchors, introduceComponentField, allTargets, appliers);
                        break;
                    default:
                        throw Fail(root, $"Unsupported SourceEdit kind '{edit.GetType().Name}'.");
                }
            }

            SyntaxNode currentRoot = root.TrackNodes(allTargets.Distinct());

            foreach (var apply in appliers)
            {
                currentRoot = apply(currentRoot);
            }

            // Self-consistency pass over the FINAL root, so every edit kind inherits it by default:
            // the patched file must not merely parse, it must COMPILE — it lives in Assets/ and Unity
            // builds it. Any edit that emitted a short `Asset(...)` call needs its using directive.
            if (currentRoot is CompilationUnitSyntax patchedUnit)
            {
                currentRoot = EnsureAssetRefsUsing(patchedUnit);
            }

            return currentRoot.ToFullString();
        }

        // ---- Emitted-code self-consistency ------------------------------------------------------

        private const string AssetRefsTypeName = "SceneBuilder.Authoring.AssetRefs";

        /// <summary>
        /// Guarantees the compilation unit imports the <c>Asset(...)</c> factory whenever the patched
        /// source calls it in the SHORT form. Emission keeps the short, readable `Asset("path")` call —
        /// readability is the product's point — so the file must carry
        /// <c>using static SceneBuilder.Authoring.AssetRefs;</c> or it fails with CS0103. Idempotent:
        /// a file that already imports it (however it got there) is returned untouched.
        /// </summary>
        private static CompilationUnitSyntax EnsureAssetRefsUsing(CompilationUnitSyntax root)
        {
            var callsShortAssetFactory = root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(inv => inv.Expression is IdentifierNameSyntax identifier
                    && identifier.Identifier.Text == "Asset");

            if (!callsShortAssetFactory)
            {
                return root;
            }

            // Guard against duplicates anywhere in the file (compilation-unit OR namespace scope).
            var alreadyImported = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Any(u => u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
                    && u.Name?.ToString() == AssetRefsTypeName);

            if (alreadyImported)
            {
                return root;
            }

            // Spacing is explicit: bare SyntaxFactory tokens carry no trivia, which renders the
            // directive as `usingstaticSceneBuilder.Authoring.AssetRefs;` — itself uncompilable.
            var directive = SyntaxFactory
                .UsingDirective(SyntaxFactory.ParseName(AssetRefsTypeName))
                .WithUsingKeyword(SyntaxFactory.Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                .WithStaticKeyword(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));

            return root.AddUsings(directive);
        }

        // ---- PatchArgument ------------------------------------------------------------------

        // ---- IntroduceTransformCall -------------------------------------------------------------

        /// <summary>
        /// Finds every transform <see cref="PatchArgument"/> whose target statement carries NO
        /// <c>.Transform(...)</c> call and introduces one per anchor, folding all of that anchor's
        /// pos/rot/scale args into a single call. Returns the edits it consumed so the main loop
        /// skips them.
        /// </summary>
        /// <remarks>
        /// Absent a Transform call the applier used to THROW ("No .Transform(...) call found"),
        /// which made the most ordinary edit in the product — nudging an object that was authored
        /// without an explicit transform — a hard sync failure. Introducing the call is the same
        /// treatment an absent flag call already gets.
        /// </remarks>
        private static HashSet<PatchArgument> ResolveTransformIntroductions(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            SourcePatch patch,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var consumed = new HashSet<PatchArgument>();
            var byAnchor = new Dictionary<string, List<PatchArgument>>();

            foreach (var edit in patch.Edits.OfType<PatchArgument>())
            {
                // `name` patches the Add("...") argument, not the transform chain. Anything outside
                // pos/rot/scale is left to ResolvePatchArgument so it reports the precise failure.
                if (Array.IndexOf(TransformPositionalArgs, edit.ArgName) < 0)
                {
                    continue;
                }

                var statement = FindAnchorInvocation(root, anchors, edit.Anchor).FirstAncestorOrSelf<StatementSyntax>();
                if (statement == null || FindTransformInvocation(statement) != null)
                {
                    continue;
                }

                if (!byAnchor.TryGetValue(edit.Anchor, out var group))
                {
                    group = new List<PatchArgument>();
                    byAnchor[edit.Anchor] = group;
                }

                group.Add(edit);
                consumed.Add(edit);
            }

            foreach (var (anchor, group) in byAnchor)
            {
                ResolveIntroduceTransformCall(root, anchors, anchor, group, allTargets, appliers);
            }

            return consumed;
        }

        private static void ResolveIntroduceTransformCall(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            string anchor,
            List<PatchArgument> args,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, anchor);
            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{anchor}' is not inside a statement.");

            var chainExpr = GetChainExpression(statement);

            // Canonical pos/rot/scale order with named arguments, so the emission is indistinguishable
            // from hand-authored `.Transform(pos: (...), scale: (...))` — including when only the
            // later args are present, where positional syntax would be wrong.
            var ordered = TransformPositionalArgs
                .Select(name => args.FirstOrDefault(a => a.ArgName == name))
                .Where(a => a != null)
                .ToList();

            allTargets.Add(chainExpr);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(chainExpr)!;

                var argList = SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(ordered.Select(a =>
                        SyntaxFactory.Argument(SyntaxFactory.ParseExpression(a!.NewExpr))
                            .WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(a.ArgName))))));

                var newCall = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            current.WithoutTrailingTrivia(),
                            SyntaxFactory.IdentifierName("Transform")),
                        argList)
                    .WithTrailingTrivia(current.GetTrailingTrivia());

                return currentRoot.ReplaceNode(current, newCall);
            });
        }

        private static void ResolvePatchArgument(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            PatchArgument edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);

            if (edit.ArgName == "name")
            {
                var argExpr = invocation.ArgumentList.Arguments[0].Expression;
                allTargets.Add(argExpr);
                appliers.Add(currentRoot =>
                {
                    var current = currentRoot.GetCurrentNode(argExpr)!;
                    var replacement = SyntaxFactory.ParseExpression(edit.NewExpr).WithTriviaFrom(current);
                    return currentRoot.ReplaceNode(current, replacement);
                });
                return;
            }

            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            var transformInvocation = FindTransformInvocation(statement);
            if (transformInvocation == null)
            {
                throw Fail(statement, $"No .Transform(...) call found for anchor '{edit.Anchor}'.");
            }

            var (existingArg, _) = FindTransformArgument(transformInvocation.ArgumentList, edit.ArgName);
            if (existingArg != null)
            {
                var oldExpr = existingArg.Expression;
                allTargets.Add(oldExpr);
                appliers.Add(currentRoot =>
                {
                    var current = currentRoot.GetCurrentNode(oldExpr)!;
                    var replacement = SyntaxFactory.ParseExpression(edit.NewExpr).WithTriviaFrom(current);
                    return currentRoot.ReplaceNode(current, replacement);
                });
            }
            else
            {
                var argList = transformInvocation.ArgumentList;
                allTargets.Add(argList);
                appliers.Add(currentRoot =>
                {
                    var current = currentRoot.GetCurrentNode(argList)!;
                    var replacement = InsertTransformArgument(current, edit.ArgName, edit.NewExpr);
                    return currentRoot.ReplaceNode(current, replacement);
                });
            }
        }

        private static InvocationExpressionSyntax? FindTransformInvocation(StatementSyntax statement)
        {
            return FindFlagInvocation(statement, "Transform");
        }

        private static (ArgumentSyntax? Argument, int Index) FindTransformArgument(ArgumentListSyntax argList, string argName)
        {
            for (var i = 0; i < argList.Arguments.Count; i++)
            {
                var arg = argList.Arguments[i];
                var name = arg.NameColon != null
                    ? arg.NameColon.Name.Identifier.Text
                    : (i < TransformPositionalArgs.Length ? TransformPositionalArgs[i] : null);

                if (name == argName)
                {
                    return (arg, i);
                }
            }

            return (null, -1);
        }

        private static ArgumentListSyntax InsertTransformArgument(ArgumentListSyntax argList, string argName, string newExpr)
        {
            var canonicalIndex = Array.IndexOf(TransformPositionalArgs, argName);
            var newArgument = SyntaxFactory.Argument(
                SyntaxFactory.NameColon(argName),
                default,
                SyntaxFactory.ParseExpression(newExpr));

            var arguments = argList.Arguments;
            var insertAt = arguments.Count;
            for (var i = 0; i < arguments.Count; i++)
            {
                var existingName = arguments[i].NameColon != null
                    ? arguments[i].NameColon!.Name.Identifier.Text
                    : (i < TransformPositionalArgs.Length ? TransformPositionalArgs[i] : null);
                var existingCanonical = existingName != null ? Array.IndexOf(TransformPositionalArgs, existingName) : int.MaxValue;

                if (existingCanonical > canonicalIndex)
                {
                    insertAt = i;
                    break;
                }
            }

            var newArguments = arguments.Insert(insertAt, newArgument);
            return argList.WithArguments(newArguments);
        }

        // ---- MoveStatement --------------------------------------------------------------------

        private static void ResolveMoveStatement(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            MoveStatement edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var movedStatement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
                || memberAccess.Expression is not IdentifierNameSyntax receiverIdentifier)
            {
                throw Fail(invocation, $"Cannot rewrite receiver for anchor '{edit.Anchor}'.");
            }

            string newHandleName;
            StatementSyntax? parentStatement = null;

            if (edit.NewParentAnchor != null)
            {
                var parentInvocation = FindAnchorInvocation(root, anchors, edit.NewParentAnchor);
                parentStatement = parentInvocation.FirstAncestorOrSelf<StatementSyntax>()
                    ?? throw Fail(parentInvocation, $"Anchor '{edit.NewParentAnchor}' is not inside a statement.");

                if (parentStatement is not LocalDeclarationStatementSyntax parentLocal
                    || parentLocal.Declaration.Variables.Count != 1)
                {
                    throw Fail(parentStatement, $"New parent anchor '{edit.NewParentAnchor}' has no handle variable; reparent is not expressible.");
                }

                newHandleName = parentLocal.Declaration.Variables[0].Identifier.Text;
            }
            else
            {
                var (_, sceneParamName) = FindBuildMethod(root);
                newHandleName = sceneParamName;
            }

            allTargets.Add(movedStatement);
            allTargets.Add(receiverIdentifier);
            if (parentStatement != null)
            {
                allTargets.Add(parentStatement);
            }

            appliers.Add(currentRoot =>
            {
                var currentReceiver = currentRoot.GetCurrentNode(receiverIdentifier)!;
                var newReceiver = SyntaxFactory.IdentifierName(newHandleName).WithTriviaFrom(currentReceiver);
                currentRoot = currentRoot.ReplaceNode(currentReceiver, newReceiver);

                var currentMoved = currentRoot.GetCurrentNode(movedStatement)!;
                currentRoot = currentRoot.RemoveNode(currentMoved, SyntaxRemoveOptions.KeepNoTrivia)!;

                if (parentStatement != null)
                {
                    var currentParent = currentRoot.GetCurrentNode(parentStatement)!;
                    currentRoot = currentRoot.InsertNodesAfter(currentParent, new[] { currentMoved });
                }
                else
                {
                    var (buildMethod, _) = FindBuildMethod(currentRoot);
                    var body = buildMethod.Body!;
                    var newBody = body.AddStatements(currentMoved);
                    currentRoot = currentRoot.ReplaceNode(body, newBody);
                }

                return currentRoot;
            });
        }

        // ---- ReorderStatement -----------------------------------------------------------------

        private static void ResolveReorderStatement(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            ReorderStatement edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            if (statement.Parent is not BlockSyntax block)
            {
                throw Fail(statement, $"Anchor '{edit.Anchor}' statement is not inside a block.");
            }

            allTargets.Add(block);
            allTargets.Add(statement);

            appliers.Add(currentRoot =>
            {
                var currentBlock = currentRoot.GetCurrentNode(block)!;
                var currentStatement = currentRoot.GetCurrentNode(statement)!;

                var index = currentBlock.Statements.IndexOf(currentStatement);
                var withoutStatement = currentBlock.Statements.RemoveAt(index);
                var newIndex = Math.Min(edit.NewSiblingIndex, withoutStatement.Count);

                // A sibling index is a SCENE-GRAPH position; a block index is a C# ordering with
                // declaration rules. Re-seating a statement ahead of the declaration of the handle it
                // calls emits `alpha.Add("Beta"); var alpha = ...;` — CS0841, use before declaration.
                // Floor the target at the receiver's declaration so the reorder cannot outrun it.
                newIndex = Math.Max(newIndex, MinIndexAfterReceiverDeclaration(withoutStatement, currentStatement));

                var newStatements = withoutStatement.Insert(newIndex, currentStatement);
                var newBlock = currentBlock.WithStatements(newStatements);

                return currentRoot.ReplaceNode(currentBlock, newBlock);
            });
        }

        /// <summary>
        /// The earliest block index at which <paramref name="statement"/> may legally sit: one past the
        /// local declaration of the handle it calls, when that handle is declared in this same block.
        /// Returns 0 when the receiver is not a local declared here (a lambda parameter, the `scene`
        /// root, or a handle from an enclosing scope) — nothing to outrun.
        /// </summary>
        private static int MinIndexAfterReceiverDeclaration(
            SyntaxList<StatementSyntax> statements,
            StatementSyntax statement)
        {
            var receiver = RootReceiverName(statement);
            if (receiver == null)
            {
                return 0;
            }

            for (var i = 0; i < statements.Count; i++)
            {
                if (statements[i] is LocalDeclarationStatementSyntax local
                    && local.Declaration.Variables.Any(v => v.Identifier.Text == receiver))
                {
                    return i + 1;
                }
            }

            return 0;
        }

        /// <summary>
        /// The leftmost identifier of a statement's fluent chain — the handle it is called on
        /// (`alpha` in `alpha.Add("Beta").Transform(...)`). Null when the statement is not a chain
        /// rooted in a plain identifier.
        /// </summary>
        private static string? RootReceiverName(StatementSyntax statement)
        {
            ExpressionSyntax? expression = statement switch
            {
                ExpressionStatementSyntax expressionStatement => expressionStatement.Expression,
                LocalDeclarationStatementSyntax localDeclaration when localDeclaration.Declaration.Variables.Count == 1
                    => localDeclaration.Declaration.Variables[0].Initializer?.Value,
                _ => null,
            };

            while (true)
            {
                switch (expression)
                {
                    case InvocationExpressionSyntax invocation:
                        expression = invocation.Expression;
                        break;
                    case MemberAccessExpressionSyntax memberAccess:
                        expression = memberAccess.Expression;
                        break;
                    case IdentifierNameSyntax identifier:
                        return identifier.Identifier.Text;
                    default:
                        return null;
                }
            }
        }

        // ---- RemoveStatement --------------------------------------------------------------------

        private static void ResolveRemoveStatement(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            RemoveStatement edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            allTargets.Add(statement);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(statement)!;
                return currentRoot.RemoveNode(current, SyntaxRemoveOptions.KeepNoTrivia)!;
            });
        }

        // ---- AppendStatement ------------------------------------------------------------------

        private static void ResolveAppendStatement(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            AppendStatement edit,
            Dictionary<string, SyntaxAnnotation> appendAnnotations,
            Dictionary<string, SyntaxAnnotation> lastSiblingByParent,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var ownAnnotation = appendAnnotations[edit.NewLogicalId];

            if (edit.ParentAnchor == null)
            {
                var (buildMethod, sceneParamName) = FindBuildMethod(root);
                var body = buildMethod.Body!;
                var indent = BodyIndent(root);
                var newStmt = ParseAppendStatement(edit, sceneParamName, indent)
                    .WithAdditionalAnnotations(ownAnnotation);

                allTargets.Add(body);
                appliers.Add(currentRoot =>
                {
                    var currentBody = (BlockSyntax)currentRoot.GetCurrentNode(body)!;
                    var newBody = currentBody.AddStatements(newStmt);
                    return currentRoot.ReplaceNode(currentBody, newBody);
                });
            }
            else if (appendAnnotations.ContainsKey(edit.ParentAnchor))
            {
                // Parent is appended in THIS SAME batch (e.g. a b2-t3 new-subtree: parent+child
                // both AppendStatements), so it has no anchor in the ORIGINAL source yet. Relay
                // placement via the parent's (or previous same-batch sibling's) annotation instead
                // of FindAnchorInvocation.
                var receiver = edit.ParentHandle
                    ?? throw Fail(root, $"AppendStatement '{edit.NewLogicalId}' targets same-batch parent '{edit.ParentAnchor}' but has no ParentHandle.");

                var anchorAnnotation = lastSiblingByParent.TryGetValue(edit.ParentAnchor, out var siblingAnnotation)
                    ? siblingAnnotation
                    : appendAnnotations[edit.ParentAnchor];
                lastSiblingByParent[edit.ParentAnchor] = ownAnnotation;

                var indent = BodyIndent(root);
                var newStmt = ParseAppendStatement(edit, receiver, indent)
                    .WithAdditionalAnnotations(ownAnnotation);

                appliers.Add(currentRoot =>
                {
                    var parentNode = currentRoot.GetAnnotatedNodes(anchorAnnotation).Single();
                    return currentRoot.InsertNodesAfter(parentNode, new[] { newStmt });
                });
            }
            else
            {
                var parentInvocation = FindAnchorInvocation(root, anchors, edit.ParentAnchor);
                var parentStatement = parentInvocation.FirstAncestorOrSelf<StatementSyntax>()
                    ?? throw Fail(parentInvocation, $"Anchor '{edit.ParentAnchor}' is not inside a statement.");

                var receiver = edit.ParentHandle
                    ?? throw Fail(parentStatement, $"Anchor '{edit.ParentAnchor}' has no parent handle to append under.");

                if (edit.IntroduceParentHandle)
                {
                    if (parentStatement is not ExpressionStatementSyntax parentExprStatement)
                    {
                        throw Fail(parentStatement, $"Anchor '{edit.ParentAnchor}' already declares a handle; cannot introduce '{receiver}' again.");
                    }

                    var declaration = BuildHandleDeclaration(parentExprStatement, receiver);
                    var newStmt = ParseAppendStatement(edit, receiver, IndentOf(parentStatement))
                        .WithAdditionalAnnotations(ownAnnotation);
                    var annotation = new SyntaxAnnotation();
                    var annotatedDeclaration = declaration.WithAdditionalAnnotations(annotation);

                    allTargets.Add(parentStatement);
                    appliers.Add(currentRoot =>
                    {
                        var currentParent = currentRoot.GetCurrentNode(parentStatement)!;
                        currentRoot = currentRoot.ReplaceNode(currentParent, annotatedDeclaration);
                        var declaredNode = currentRoot.GetAnnotatedNodes(annotation).Single();
                        return currentRoot.InsertNodesAfter(declaredNode, new[] { newStmt });
                    });
                }
                else
                {
                    var newStmt = ParseAppendStatement(edit, receiver, IndentOf(parentStatement))
                        .WithAdditionalAnnotations(ownAnnotation);

                    allTargets.Add(parentStatement);
                    appliers.Add(currentRoot =>
                    {
                        var currentParent = currentRoot.GetCurrentNode(parentStatement)!;
                        return currentRoot.InsertNodesAfter(currentParent, new[] { newStmt });
                    });
                }
            }
        }

        private static StatementSyntax BuildHandleDeclaration(ExpressionStatementSyntax statement, string handle)
        {
            var text = $"var {handle} = {statement.Expression.WithoutTrivia().ToFullString()};";
            return SyntaxFactory.ParseStatement(text)
                .WithLeadingTrivia(statement.GetLeadingTrivia())
                .WithTrailingTrivia(statement.GetTrailingTrivia());
        }

        private static StatementSyntax ParseAppendStatement(AppendStatement edit, string receiver, string indent)
        {
            var text = BuildAppendStatementText(edit, receiver);

            return SyntaxFactory.ParseStatement(text)
                .WithLeadingTrivia(SyntaxFactory.Whitespace(indent))
                .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));
        }

        private static string IndentOf(SyntaxNode node)
        {
            return node.GetLeadingTrivia()
                .LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia))
                .ToString();
        }

        private static string BodyIndent(SyntaxNode root)
        {
            var (buildMethod, _) = FindBuildMethod(root);
            var body = buildMethod.Body!;

            return body.Statements.Count > 0
                ? IndentOf(body.Statements[0])
                : IndentOf(buildMethod) + "    ";
        }

        private static string BuildAppendStatementText(AppendStatement edit, string receiver)
        {
            var nameLiteral = SyntaxFactory.Literal(edit.Name).ToString();
            var chain = $"{receiver}.Add({nameLiteral})";

            if (edit.Transform != null)
            {
                var transform = edit.Transform;
                var parts = new List<string>();

                if (transform.Position != Vec3.Zero)
                {
                    parts.Add("pos: " + SourceExpr.Vec3Literal(transform.Position));
                }

                if (transform.Rotation != Quat.Identity)
                {
                    parts.Add("rot: " + SourceExpr.Vec3Literal(Rotation.QuatToEuler(transform.Rotation)));
                }

                if (transform.Scale != Vec3.One)
                {
                    parts.Add("scale: " + SourceExpr.Vec3Literal(transform.Scale));
                }

                if (parts.Count > 0)
                {
                    chain += ".Transform(" + string.Join(", ", parts) + ")";
                }
            }

            if (edit.Tag != null)
            {
                chain += $".Tag({SourceExpr.StringLiteral(edit.Tag)})";
            }

            if (edit.Layer != null)
            {
                chain += $".Layer({SourceExpr.IntLiteral(edit.Layer.Value)})";
            }

            if (edit.Active != null)
            {
                chain += $".Active({(edit.Active.Value ? "true" : "false")})";
            }

            if (edit.IsStatic == true)
            {
                chain += ".Static()";
            }

            return edit.Handle != null
                ? $"var {edit.Handle} = {chain};"
                : $"{chain};";
        }

        // ---- PatchFlagArgument ------------------------------------------------------------------

        private static void ResolvePatchFlagArgument(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            PatchFlagArgument edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            var flagName = FlagName(edit.Flag);
            var flagInvocation = FindFlagInvocation(statement, flagName)
                ?? throw Fail(statement, $"No .{flagName}(...) call found for anchor '{edit.Anchor}'.");

            if (flagInvocation.ArgumentList.Arguments.Count < 1)
            {
                throw Fail(flagInvocation, $".{flagName}(...) call for anchor '{edit.Anchor}' has no argument to patch.");
            }

            var argExpr = flagInvocation.ArgumentList.Arguments[0].Expression;
            allTargets.Add(argExpr);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(argExpr)!;
                var replacement = SyntaxFactory.ParseExpression(edit.NewExpr).WithTriviaFrom(current);
                return currentRoot.ReplaceNode(current, replacement);
            });
        }

        // ---- IntroduceFlagCall ------------------------------------------------------------------

        private static void ResolveIntroduceFlagCall(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            IntroduceFlagCall edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            var chainExpr = GetChainExpression(statement);
            var flagName = FlagName(edit.Flag);

            allTargets.Add(chainExpr);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(chainExpr)!;

                var argList = edit.ArgExpr == null
                    ? SyntaxFactory.ArgumentList()
                    : SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(SyntaxFactory.ParseExpression(edit.ArgExpr))));

                var newCall = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            current.WithoutTrailingTrivia(),
                            SyntaxFactory.IdentifierName(flagName)),
                        argList)
                    .WithTrailingTrivia(current.GetTrailingTrivia());

                return currentRoot.ReplaceNode(current, newCall);
            });
        }

        // ---- RemoveFlagCall ---------------------------------------------------------------------

        private static void ResolveRemoveFlagCall(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            RemoveFlagCall edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            var flagName = FlagName(edit.Flag);
            var flagInvocation = FindFlagInvocation(statement, flagName)
                ?? throw Fail(statement, $"No .{flagName}(...) call found for anchor '{edit.Anchor}'.");

            allTargets.Add(flagInvocation);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(flagInvocation)!;
                var member = (MemberAccessExpressionSyntax)current.Expression;
                var replacement = member.Expression.WithTrailingTrivia(current.GetTrailingTrivia());
                return currentRoot.ReplaceNode(current, replacement);
            });
        }

        // ---- Flag helpers -----------------------------------------------------------------------

        private static InvocationExpressionSyntax? FindFlagInvocation(StatementSyntax statement, string flagName)
        {
            return statement.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(inv => inv.Expression is MemberAccessExpressionSyntax member
                    && member.Name.Identifier.Text == flagName);
        }

        private static ExpressionSyntax GetChainExpression(StatementSyntax statement)
        {
            return statement switch
            {
                ExpressionStatementSyntax exprStatement => exprStatement.Expression,
                LocalDeclarationStatementSyntax localDeclaration
                    => localDeclaration.Declaration.Variables[0].Initializer!.Value,
                _ => throw Fail(statement, $"Statement is not a fluent chain expression or declaration."),
            };
        }

        private static string FlagName(FlagKind flag) => flag switch
        {
            FlagKind.Tag => "Tag",
            FlagKind.Layer => "Layer",
            FlagKind.Active => "Active",
            FlagKind.Static => "Static",
            _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "Unknown FlagKind."),
        };

        // ---- Anchor resolution ------------------------------------------------------------------

        private static InvocationExpressionSyntax FindAnchorInvocation(
            SyntaxNode root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            string anchorId)
        {
            if (!anchors.TryGetValue(anchorId, out var span))
            {
                throw Fail(root, $"No anchor found for logical id '{anchorId}'.");
            }

            var textSpan = TextSpan.FromBounds(span.Start, span.Start + span.Length);
            var node = root.FindNode(textSpan, getInnermostNodeForTie: true);

            // Gate 1: GameObject anchors, whose span starts at the invocation's own start
            // (e.g. `scene.Add(...)`). Gate 2 (tried only when gate 1 misses): component
            // anchors, whose span starts mid-statement at the `.Component` member-access dot
            // (BuilderParser.cs — anchorStart = memberAccess.OperatorToken.SpanStart), so no
            // invocation begins at span.Start; match on the operator token instead.
            var invocation =
                node.FirstAncestorOrSelf<InvocationExpressionSyntax>(inv => inv.Span.Start == span.Start)
                ?? node.FirstAncestorOrSelf<InvocationExpressionSyntax>(inv =>
                    inv.Expression is MemberAccessExpressionSyntax ma && ma.OperatorToken.SpanStart == span.Start);

            if (invocation == null)
            {
                throw Fail(root, $"Could not locate anchor node for logical id '{anchorId}'.");
            }

            return invocation;
        }

        private static (MethodDeclarationSyntax Method, string SceneParamName) FindBuildMethod(SyntaxNode root)
        {
            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .First(m => m.Identifier.Text == "Build");
            var paramName = method.ParameterList.Parameters[0].Identifier.Text;
            return (method, paramName);
        }

        // ---- Fail-loud helper -------------------------------------------------------------------

        private static PatchException Fail(SyntaxNode node, string message)
        {
            var position = node.GetLocation().GetLineSpan().StartLinePosition;
            var line = position.Line + 1;
            var column = position.Character + 1;
            return new PatchException($"{message} at line {line}, column {column}.", line, column);
        }
    }
}
