using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // Append-statement text builders: BuildHandleDeclaration, ParseAppendStatement, IndentOf,
    // BodyIndent, BuildAppendStatementText. Second partial-class file so the existing private helpers
    // on SourcePatchApplier are reused directly — no visibility changes, no duplication.
    public static partial class SourcePatchApplier
    {
        /// <summary>
        /// Rewrites `expr;` into `var handle = expr;`, REUSING the original expression node rather than
        /// re-parsing its text.
        /// </summary>
        /// <remarks>
        /// Reuse is load-bearing, not an optimisation. Other edits in the same batch hold TRACKED nodes
        /// INSIDE this statement — a transform argument, a flag argument on the very object being given
        /// a handle. Re-parsing produces a structurally identical but DIFFERENT tree, whose nodes carry
        /// none of those annotations, so their GetCurrentNode returns null and the apply dies with a
        /// NullReferenceException. Keeping the node keeps every annotation hanging off it.
        /// </remarks>
        private static StatementSyntax BuildHandleDeclaration(ExpressionStatementSyntax statement, string handle)
        {
            var declarator = SyntaxFactory
                .VariableDeclarator(SyntaxFactory.Identifier(handle).WithTrailingTrivia(SyntaxFactory.Space))
                .WithInitializer(SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.Token(SyntaxKind.EqualsToken).WithTrailingTrivia(SyntaxFactory.Space),
                    statement.Expression.WithoutTrivia()));

            return SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.IdentifierName("var").WithTrailingTrivia(SyntaxFactory.Space),
                        SyntaxFactory.SingletonSeparatedList(declarator)))
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

            // Directly after `.Add(name)`, so the id reads as part of the object's declaration rather
            // than trailing a long fluent chain — and so a human/LLM rewriting the statement's data
            // calls keeps it.
            if (edit.ExplicitId != null)
            {
                chain += $".Id({SourceExpr.StringLiteral(edit.ExplicitId)})";
            }

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
    }
}
