using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Parsing
{
    // `.On(selector, closure)` scoped-override resolution — typed member chain (`t => t.A.B`) or
    // string path (`"A/B"`) resolved against the enclosing instance's FacadeCatalog entry
    // (`node.SourcePrefabGuid`, set by b4-t1's `Instance(Prefabs.X)` arm) to a real child path
    // (RealName-joined) + the leaf's durable LocalId. The closure (arg1) is not read here —
    // nested-override materialize is spec 24. A miss (unmatched segment / no catalog / instance
    // has no resolved prefab guid) is a located Conflict, mirroring b4-t1's UnknownFacadeReference
    // channel — never a throw, never a silent drop. See research.md.
    public static partial class BuilderParser
    {
        private static void ApplyScopedOn(NodeBuilder node, ArgumentListSyntax args, ParserContext ctx)
        {
            var arg0 = args.Arguments[0].Expression;
            TryReadSelectorSegments(arg0, out var segments, out var byPropertyName);

            if (ctx.FacadeCatalog != null &&
                ctx.FacadeCatalog.TryResolveSelector(node.SourcePrefabGuid ?? "", segments, byPropertyName, out var childPath, out var localId))
            {
                node.ScopedOverrides.Add(new ScopedOverride { ChildPath = childPath, LocalId = localId });
                return;
            }

            var argSpan = new SourceSpan(arg0.Span.Start, arg0.Span.Length);
            ctx.FacadeConflicts.Add(new Conflict
            {
                Kind = ConflictKind.UnknownFacadeReference,
                Reason = $"Unknown facade reference '{string.Join("/", segments)}'.",
                Location = argSpan,
            });
        }

        // arg0 is either a typed member-chain selector (`t => t.A.B`, recognizer-guaranteed shape:
        // SimpleLambda whose body is a MemberAccess spine) or a string path (`"A/B"`) — the
        // recognizer's shape check (FlatShapeRecognizer.Instance.cs) already rejects anything
        // else before this ever runs. Typed: walk the MemberAccess spine outer->inner (the
        // innermost Expression is the lambda parameter, not a segment) and reverse to get
        // root-to-leaf order; segments are PropertyName identifiers. String: split on '/';
        // segments are RealName literals.
        private static void TryReadSelectorSegments(ExpressionSyntax arg0, out List<string> segments, out bool byPropertyName)
        {
            if (arg0 is SimpleLambdaExpressionSyntax { Body: ExpressionSyntax body })
            {
                byPropertyName = true;
                segments = new List<string>();

                var current = body;
                while (current is MemberAccessExpressionSyntax memberAccess)
                {
                    segments.Insert(0, memberAccess.Name.Identifier.Text);
                    current = memberAccess.Expression;
                }

                return;
            }

            byPropertyName = false;
            var path = EvalStringLiteral(arg0);
            segments = new List<string>(path.Split('/'));
        }
    }
}
