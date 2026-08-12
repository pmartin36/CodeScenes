using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Grammar
{
    // The `.SetRef(x => x.field, new T{...} | null)` managed-reference authoring form: a pure
    // arg-SHAPE check (selector lambda + `new T{}`/array/`null`), mirroring CheckSetCall. Unlike
    // `.Set`, `.SetRef` accepts ONLY the selector key form -- the string-key form `.Set` also
    // allows is not a valid `.SetRef` key.
    public static partial class FlatShapeRecognizer
    {
        private static void CheckSetRefCall(InvocationExpressionSyntax invocation, RecognizerContext ctx)
        {
            var args = invocation.ArgumentList.Arguments;
            if (args.Count != 2)
            {
                Report(ctx, invocation, SB1003, "SetRef(...) requires exactly two arguments");
                return;
            }

            var keyExpr = args[0].Expression;
            if (keyExpr is not SimpleLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax })
            {
                Report(ctx, keyExpr, SB1003, "Unsupported SetRef(...) key form (expected `r => r.member`)");
                return;
            }

            var valueExpr = args[1].Expression;
            if (IsNullLiteral(valueExpr) ||
                valueExpr is ObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax)
            {
                return;
            }

            Report(ctx, valueExpr, SB1003, "Unsupported SetRef(...) value form (expected `new T {...}`, an array of `new T {...}`, or `null`)");
        }

        private static bool IsNullLiteral(ExpressionSyntax expression) =>
            expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NullLiteralExpression);
    }
}
