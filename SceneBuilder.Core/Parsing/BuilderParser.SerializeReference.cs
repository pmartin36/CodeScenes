using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Parsing
{
    // The `.SetRef(x => x.field, new T{...} | null)` managed-reference authoring form: the CALL
    // (not the syntax) decides ManagedReference vs Nested, since both render `new T{...}` — only
    // the SetRef entry path below produces ManagedReference. By the time this runs, RecognizeOrThrow
    // has already proven the call shape valid (FlatShapeRecognizer.SerializeReference.cs's mirror),
    // so ParseManagedReference stays total and never throws.
    public static partial class BuilderParser
    {
        // Mirrors ParseSetCall: key via the shared MemberFieldKey (the ONE `member:<name>` key
        // composer), value via the recursive ParseManagedReference below.
        private static (string Key, ValueNode Value, SourceSpan ValueSpan) ParseSetRefCall(InvocationExpressionSyntax setRefInvocation, ParserContext ctx)
        {
            var args = setRefInvocation.ArgumentList.Arguments;
            if (args.Count != 2 || args[0].Expression is not SimpleLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax memberAccess })
            {
                throw Unreachable();
            }

            var key = MemberFieldKey(memberAccess);
            var valueExpr = args[1].Expression;
            var value = ParseManagedReference(valueExpr, ctx);
            var valueSpan = new SourceSpan(valueExpr.SpanStart, valueExpr.Span.Length);
            return (key, value, valueSpan);
        }

        // Total recursive descent over a `.SetRef(...)` value: `null` -> a null reference,
        // `new T { ... }` -> a recursively-parsed instance, an array/collection of `new T{...}` ->
        // a List of the recursed elements, anything else is a genuine leaf delegated to the
        // existing (total) ValueNodeParser.Parse.
        private static ValueNode ParseManagedReference(ExpressionSyntax expr, ParserContext ctx)
        {
            if (expr is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NullLiteralExpression))
            {
                return new ValueNode.ManagedReference(null, FieldMap.Empty);
            }

            if (expr is ObjectCreationExpressionSyntax objectCreation)
            {
                var initializerKind = objectCreation.Initializer?.Kind();
                if (initializerKind == SyntaxKind.ObjectInitializerExpression)
                {
                    return ParseManagedInstance(objectCreation, ctx);
                }

                if (initializerKind == SyntaxKind.CollectionInitializerExpression)
                {
                    return ParseManagedRefList(objectCreation.Initializer!, ctx);
                }
            }

            if (expr is ArrayCreationExpressionSyntax { Initializer: { } arrayInitializer })
            {
                return ParseManagedRefList(arrayInitializer, ctx);
            }

            if (expr is ImplicitArrayCreationExpressionSyntax implicitArray)
            {
                return ParseManagedRefList(implicitArray.Initializer, ctx);
            }

            // A genuine leaf (incl. `new Vector3(...)`'s constructor-arg form) — delegated to the
            // existing, total ValueNodeParser.Parse; `.SetRef` never constructs its own leaf tokens.
            // UnwrapTypedRef strips a member's own `.As<T>()`/`.As()` emission cast first (the same
            // authoring-only typing scaffold UnityEvent static args use), recovering the underlying
            // handle/asset-factory expression the ordinary leaf parse already knows how to read.
            return ValueNodeParser.Parse(UnwrapTypedRef(expr), ctx.AssetCatalog, ctx.FacadeConflicts);
        }

        // A ManagedReference-flavored copy of ValueNodeParser.ParseNested's member loop: the
        // written type text (namespace preserved, matching ParseNested's own capture) so
        // ConcreteType.FullName re-emits byte-for-byte. A malformed member (not a plain
        // `name = value` assignment) falls to Unsupported for the WHOLE instance, mirroring
        // ParseNested's total contract — the recognizer validates only the top-level `.SetRef`
        // value shape, not initializer members, so this must stay total on its own.
        private static ValueNode ParseManagedInstance(ObjectCreationExpressionSyntax objectCreation, ParserContext ctx)
        {
            var fields = new List<KeyValuePair<string, ValueNode>>();

            foreach (var element in objectCreation.Initializer!.Expressions)
            {
                if (element is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax ident } assignment)
                {
                    return new ValueNode.Unsupported(objectCreation.ToString());
                }

                fields.Add(new KeyValuePair<string, ValueNode>(ident.Identifier.Text, ParseManagedReference(assignment.Right, ctx)));
            }

            var typeRef = new TypeRef(objectCreation.Type.ToString().Trim());
            return new ValueNode.ManagedReference(typeRef, new FieldMap(fields));
        }

        private static ValueNode ParseManagedRefList(InitializerExpressionSyntax initializer, ParserContext ctx) =>
            new ValueNode.List(initializer.Expressions.Select(e => ParseManagedReference(e, ctx)).ToArray());
    }
}
