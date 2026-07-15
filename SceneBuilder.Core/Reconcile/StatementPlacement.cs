using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Core.Reconcile
{
    // THE statement-placement path. Every edit that positions a statement in a builder block —
    // AppendStatement, MoveStatement, ReorderStatement — routes through PlaceNewStatement /
    // PlaceExistingStatement here. Placement is not per-edit-kind business: the two rules below are
    // properties of the emitted LANGUAGE, so every current and future placing edit must obey them,
    // and the only way to guarantee that is for there to be one path that cannot be bypassed.
    //
    // RULE 1 — a sibling index is a SCENE-GRAPH position, not a C# block index.
    //   BuilderParser derives a node's sibling index from the ORDER of the statements that Add to
    //   the same receiver (ProcessAddChain: `siblingIndex = targetList.Count`). A statement must
    //   therefore be seated relative to its PEERS — the statements feeding the same receiver's
    //   child list — never at a raw block offset and never "right after the parent". Getting this
    //   wrong does not throw: it emits a correct tree in the wrong ORDER, the emission re-parses to
    //   a different sibling index, and the NEXT sync silently re-Reorders it. The user's file churns
    //   twice for one edit and sync never converges.
    //
    // RULE 2 — C# requires a local to be declared before it is used.
    //   A statement may not be seated above the declaration of the handle it CALLS (the FLOOR), and
    //   a statement that DECLARES a handle may not be seated below the statements that call it (the
    //   CEILING). The ceiling is why a relocation moves the statement's dependent GROUP rather than
    //   being clamped: clamping would satisfy the compiler by emitting the wrong sibling order,
    //   trading a CS0841 for a silent convergence failure. Moving the group satisfies both, because
    //   dependents are called on the moved node's OWN handle and so take no part in any other
    //   node's peer ordering — relocating them changes nobody's sibling index.
    public static partial class SourcePatchApplier
    {
        // What a statement contributes to its receiver's ordered child list. `gamma.Add("Delta")`
        // feeds gamma's CHILDREN; `gamma.Component<T>()` feeds gamma's COMPONENTS. The two lists are
        // ordered independently, so a sibling index is only meaningful against peers of the same kind.
        private enum PeerKind
        {
            Child,
            Component,
        }

        // ---- Chain shape ------------------------------------------------------------------------

        private static ExpressionSyntax? ChainExpressionOrNull(StatementSyntax statement) => statement switch
        {
            ExpressionStatementSyntax expressionStatement => expressionStatement.Expression,
            LocalDeclarationStatementSyntax localDeclaration when localDeclaration.Declaration.Variables.Count == 1
                => localDeclaration.Declaration.Variables[0].Initializer?.Value,
            _ => null,
        };

        /// <summary>
        /// The leftmost identifier of a statement's fluent chain and the method invoked directly on it
        /// — (`gamma`, `Add`) for `gamma.Add("Delta").Transform(...)`. Both null when the statement is
        /// not a chain rooted in a plain identifier.
        /// </summary>
        private static (string? Receiver, string? FirstCall) ChainRoot(StatementSyntax statement)
        {
            var expression = ChainExpressionOrNull(statement);
            string? firstCall = null;

            while (true)
            {
                switch (expression)
                {
                    case InvocationExpressionSyntax invocation
                        when invocation.Expression is MemberAccessExpressionSyntax memberAccess:
                        firstCall = memberAccess.Name.Identifier.Text;
                        expression = memberAccess.Expression;
                        break;
                    case IdentifierNameSyntax identifier:
                        return (identifier.Identifier.Text, firstCall);
                    default:
                        return (null, null);
                }
            }
        }

        /// <summary>
        /// The leftmost identifier of a statement's fluent chain — the handle it is called on
        /// (`alpha` in `alpha.Add("Beta").Transform(...)`). Null when the statement is not a chain
        /// rooted in a plain identifier.
        /// </summary>
        private static string? RootReceiverName(StatementSyntax statement) => ChainRoot(statement).Receiver;

        /// <summary>The handle a statement declares (`delta` in `var delta = ...;`), or null.</summary>
        private static string? DeclaredHandle(StatementSyntax statement) =>
            statement is LocalDeclarationStatementSyntax local && local.Declaration.Variables.Count == 1
                ? local.Declaration.Variables[0].Identifier.Text
                : null;

        private static PeerKind PeerKindOf(StatementSyntax statement) =>
            ChainRoot(statement).FirstCall == "Component" ? PeerKind.Component : PeerKind.Child;

        private static bool IsPeer(StatementSyntax statement, string receiver, PeerKind kind)
        {
            var (statementReceiver, firstCall) = ChainRoot(statement);
            if (!string.Equals(statementReceiver, receiver, StringComparison.Ordinal))
            {
                return false;
            }

            return kind == PeerKind.Component ? firstCall == "Component" : firstCall == "Add";
        }

        // ---- Declaration order ------------------------------------------------------------------

        private static int DeclarationIndexOf(SyntaxList<StatementSyntax> statements, string handle)
        {
            for (var i = 0; i < statements.Count; i++)
            {
                if (statements[i] is LocalDeclarationStatementSyntax local
                    && local.Declaration.Variables.Any(v => v.Identifier.Text == handle))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// The FLOOR: the earliest block index at which a statement calling <paramref name="receiver"/>
        /// may legally sit — one past that handle's declaration, when it is declared in this block.
        /// Returns 0 when the receiver is not a local declared here (a lambda parameter, the `scene`
        /// root, or a handle from an enclosing scope) — nothing to outrun.
        /// </summary>
        private static int MinIndexAfterReceiverDeclaration(SyntaxList<StatementSyntax> statements, string? receiver)
        {
            if (receiver == null)
            {
                return 0;
            }

            var declaration = DeclarationIndexOf(statements, receiver);
            return declaration >= 0 ? declaration + 1 : 0;
        }

        /// <summary>
        /// The statement plus every statement that transitively CALLS a handle it declares, in block
        /// order — the unit a relocation must move (see RULE 2). A single forward pass suffices: in
        /// source that compiles, a declaration always precedes its users.
        /// </summary>
        private static List<StatementSyntax> DependentGroup(
            SyntaxList<StatementSyntax> statements,
            StatementSyntax statement)
        {
            var group = new List<StatementSyntax> { statement };

            var seed = DeclaredHandle(statement);
            if (seed == null)
            {
                return group;
            }

            var handles = new HashSet<string>(StringComparer.Ordinal) { seed };

            for (var i = statements.IndexOf(statement) + 1; i < statements.Count; i++)
            {
                var candidate = statements[i];
                var receiver = RootReceiverName(candidate);
                if (receiver == null || !handles.Contains(receiver))
                {
                    continue;
                }

                group.Add(candidate);

                var declared = DeclaredHandle(candidate);
                if (declared != null)
                {
                    handles.Add(declared);
                }
            }

            return group;
        }

        /// <summary>One past the last statement of the group headed by <c>statements[index]</c>.</summary>
        private static int EndOfGroupIndex(SyntaxList<StatementSyntax> statements, int index)
        {
            var last = index;
            foreach (var member in DependentGroup(statements, statements[index]))
            {
                var memberIndex = statements.IndexOf(member);
                if (memberIndex > last)
                {
                    last = memberIndex;
                }
            }

            return last + 1;
        }

        // ---- Placement --------------------------------------------------------------------------

        /// <summary>
        /// The block index at which a statement feeding <paramref name="receiver"/>'s
        /// <paramref name="kind"/> list must be inserted for BuilderParser to re-read it at
        /// <paramref name="siblingIndex"/>. <paramref name="statements"/> must NOT contain the
        /// statement(s) being placed.
        /// </summary>
        private static int PlacementIndex(
            SyntaxList<StatementSyntax> statements,
            string receiver,
            PeerKind kind,
            int siblingIndex)
        {
            var peers = new List<int>();
            for (var i = 0; i < statements.Count; i++)
            {
                if (IsPeer(statements[i], receiver, kind))
                {
                    peers.Add(i);
                }
            }

            int target;
            if (peers.Count == 0)
            {
                // No peer to seat against. Immediately after the receiver's own declaration is the
                // only spot that is both index 0 among its (empty) peer list and legal C#.
                var declaration = DeclarationIndexOf(statements, receiver);
                target = declaration >= 0 ? declaration + 1 : statements.Count;
            }
            else if (siblingIndex < peers.Count)
            {
                // Take the seat of the peer currently holding this index; it and everything after
                // shift down, which is exactly the scene's ordering.
                target = peers[siblingIndex];
            }
            else
            {
                // Past the last peer — and past that peer's own dependents, so each subtree stays a
                // contiguous, readable block instead of being split from its children.
                target = EndOfGroupIndex(statements, peers[peers.Count - 1]);
            }

            return Math.Max(target, MinIndexAfterReceiverDeclaration(statements, receiver));
        }

        /// <summary>
        /// Inserts a BRAND-NEW statement into <paramref name="trackedBlock"/> at the position that makes
        /// it land at <paramref name="siblingIndex"/> under <paramref name="receiver"/>.
        /// </summary>
        private static SyntaxNode PlaceNewStatement(
            SyntaxNode currentRoot,
            BlockSyntax trackedBlock,
            StatementSyntax newStatement,
            string receiver,
            PeerKind kind,
            int siblingIndex)
        {
            var block = (BlockSyntax)currentRoot.GetCurrentNode(trackedBlock)!;
            var index = PlacementIndex(block.Statements, receiver, kind, siblingIndex);
            return currentRoot.ReplaceNode(block, block.WithStatements(block.Statements.Insert(index, newStatement)));
        }

        /// <summary>
        /// Relocates an EXISTING statement — with its dependent group (RULE 2) — into
        /// <paramref name="trackedTargetBlock"/> at the position that makes it land at
        /// <paramref name="siblingIndex"/> under <paramref name="receiver"/>. Handles a cross-block
        /// move (the new parent lives in a different block than the moved statement).
        /// </summary>
        /// <remarks>
        /// The removed node INSTANCES are re-inserted verbatim, so the tracking annotations of any
        /// other edit targeting something inside the moved statement survive the relocation.
        /// </remarks>
        private static SyntaxNode PlaceExistingStatement(
            SyntaxNode currentRoot,
            BlockSyntax trackedTargetBlock,
            StatementSyntax trackedStatement,
            string receiver,
            PeerKind kind,
            int siblingIndex)
        {
            var statement = currentRoot.GetCurrentNode(trackedStatement)!;

            var group = statement.Parent is BlockSyntax sourceBlock
                ? DependentGroup(sourceBlock.Statements, statement)
                : new List<StatementSyntax> { statement };

            currentRoot = currentRoot.RemoveNodes(group, SyntaxRemoveOptions.KeepNoTrivia)!;

            var targetBlock = (BlockSyntax)currentRoot.GetCurrentNode(trackedTargetBlock)!;
            var index = PlacementIndex(targetBlock.Statements, receiver, kind, siblingIndex);
            var placed = targetBlock.Statements.InsertRange(index, group);

            return currentRoot.ReplaceNode(targetBlock, targetBlock.WithStatements(placed));
        }

        /// <summary>
        /// The block a statement is placed into: its own enclosing block. Null for the (unsupported)
        /// expression-bodied-lambda shape, where there is no statement list to seat anything in.
        /// </summary>
        private static BlockSyntax? EnclosingBlock(StatementSyntax statement) => statement.Parent as BlockSyntax;
    }
}
