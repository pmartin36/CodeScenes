using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Facades
{
    // Formatting-preserving Roslyn edit that rewrites renamed typed-facade identifier segments in
    // place, keyed on durable ids (prefab Guid for Prefabs.X; child LocalId for .On selector
    // segments). Byte-identical output when nothing renamed. A chain whose durable target vanished
    // is left untouched (safety net — never guessed; it later fails the compile check). Mirrors
    // IdCollisionHealer.Heal's shape (ParseText -> replacement set -> Replace*/ToFullString), using
    // ReplaceTokens (identifier-token swap) instead of ReplaceNodes. Does NOT invoke BuilderParser —
    // must tolerate arbitrary builder source. See
    // .agent_handoffs/typed-prefab-facades/b4-t3/research.md.
    public static class FacadeReferencePatcher
    {
        public static string Patch(string builderSource, FacadeCatalog oldCatalog, FacadeCatalog newCatalog)
        {
            var tree = CSharpSyntaxTree.ParseText(builderSource);
            var root = (CompilationUnitSyntax)tree.GetRoot();

            var guidByHandle = BuildHandleGuidMap(root, oldCatalog);
            var replacements = new Dictionary<SyntaxToken, SyntaxToken>();

            // Pass 1: `Prefabs.X` accessors, keyed on the prefab Guid.
            foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (memberAccess.Expression is not IdentifierNameSyntax { Identifier.Text: "Prefabs" })
                {
                    continue;
                }

                var nameToken = memberAccess.Name.Identifier;
                if (!oldCatalog.TryGetGuid(nameToken.Text, out var guid))
                {
                    continue; // unknown to the old catalog — leave untouched.
                }

                if (!newCatalog.TryGetPropertyName(guid, out var newName))
                {
                    continue; // prefab vanished from the new catalog — safety net.
                }

                if (newName != nameToken.Text && !replacements.ContainsKey(nameToken))
                {
                    replacements[nameToken] = SyntaxFactory.Identifier(nameToken.LeadingTrivia, newName, nameToken.TrailingTrivia);
                }
            }

            // Pass 2: typed `.On(t => t.A.B, ...)` selector segments, keyed on child LocalId.
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!IsTypedOnInvocation(invocation, out var receiver, out var lambda))
                {
                    continue;
                }

                if (!TryGetInstanceGuid(receiver, oldCatalog, guidByHandle, out var prefabGuid))
                {
                    continue; // string-form instance or unresolvable receiver — conservative.
                }

                if (!ReadSegments(lambda, out var segments))
                {
                    continue;
                }

                if (!TryResolveLocalIds(oldCatalog, prefabGuid, segments, out var localIds))
                {
                    continue; // segment unmatched against the old catalog — can't derive a durable key.
                }

                if (!TryResolveNewNames(newCatalog, prefabGuid, localIds, out var newNames))
                {
                    continue; // durable target vanished at some depth — abandon the WHOLE selector.
                }

                for (var i = 0; i < segments.Count; i++)
                {
                    var (token, name) = segments[i];
                    var newName = newNames[i];
                    if (newName != name && !replacements.ContainsKey(token))
                    {
                        replacements[token] = SyntaxFactory.Identifier(token.LeadingTrivia, newName, token.TrailingTrivia);
                    }
                }
            }

            if (replacements.Count == 0)
            {
                return builderSource;
            }

            var newRoot = root.ReplaceTokens(replacements.Keys, (orig, _) => replacements[orig]);
            return newRoot.ToFullString();
        }

        private static bool IsTypedOnInvocation(InvocationExpressionSyntax invocation, out ExpressionSyntax receiver, out SimpleLambdaExpressionSyntax lambda)
        {
            receiver = null!;
            lambda = null!;

            if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "On" } memberAccess)
            {
                return false;
            }

            if (invocation.ArgumentList.Arguments.Count == 0)
            {
                return false;
            }

            if (invocation.ArgumentList.Arguments[0].Expression is not SimpleLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax } lambdaExpr)
            {
                return false; // string `.On("A/B", ...)` — not a compiler-checked identifier, out of scope.
            }

            receiver = memberAccess.Expression;
            lambda = lambdaExpr;
            return true;
        }

        // Walks the selector body's MemberAccess spine root->leaf, pairing each segment's
        // identifier token with its authored text. Mirrors BuilderParser.Facade.cs's
        // TryReadSelectorSegments (typed arm).
        private static bool ReadSegments(SimpleLambdaExpressionSyntax lambda, out List<(SyntaxToken Token, string Name)> segments)
        {
            segments = new List<(SyntaxToken, string)>();

            if (lambda.Body is not ExpressionSyntax current)
            {
                return false;
            }

            while (current is MemberAccessExpressionSyntax memberAccess)
            {
                segments.Insert(0, (memberAccess.Name.Identifier, memberAccess.Name.Identifier.Text));
                current = memberAccess.Expression;
            }

            return segments.Count > 0;
        }

        // Walks DOWN the `.On` receiver spine through chained invocations until reaching either
        // an `Instance(Prefabs.X)` call (Guid via oldCatalog) or a bare identifier handle
        // resolved via guidByHandle. Conservative: any other shape (string-form Instance,
        // unresolvable receiver) reports no guid.
        private static bool TryGetInstanceGuid(ExpressionSyntax receiver, FacadeCatalog oldCatalog, IReadOnlyDictionary<string, string> guidByHandle, out string guid)
        {
            var current = receiver;

            while (true)
            {
                if (current is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Instance" } } instanceInvocation)
                {
                    var args = instanceInvocation.ArgumentList.Arguments;
                    if (args.Count > 0 &&
                        args[0].Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Prefabs" } } prefabsAccess)
                    {
                        return oldCatalog.TryGetGuid(prefabsAccess.Name.Identifier.Text, out guid);
                    }

                    guid = "";
                    return false; // string-form `Instance("path")` — not a durable façade Guid.
                }

                if (current is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax chainedMember } chainedInvocation)
                {
                    current = chainedMember.Expression;
                    continue;
                }

                if (current is IdentifierNameSyntax identifier)
                {
                    return guidByHandle.TryGetValue(identifier.Identifier.Text, out guid!);
                }

                guid = "";
                return false;
            }
        }

        // Pre-pass: `var h = ...Instance(Prefabs.X)...;` declarations -> handle name -> Guid, so
        // a detached `h.On(...)` can still resolve its enclosing instance.
        private static Dictionary<string, string> BuildHandleGuidMap(CompilationUnitSyntax root, FacadeCatalog oldCatalog)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (declarator.Initializer?.Value is ExpressionSyntax value &&
                    TryGetInstanceGuid(value, oldCatalog, map, out var guid))
                {
                    map[declarator.Identifier.Text] = guid;
                }
            }

            return map;
        }

        // OLD resolve (by PropertyName): walk oldCatalog's entry for prefabGuid, matching each
        // segment's authored identifier against FacadeChild.PropertyName; records each matched
        // child's durable LocalId. Any unmatched segment aborts (can't derive a durable key).
        private static bool TryResolveLocalIds(FacadeCatalog oldCatalog, string prefabGuid, List<(SyntaxToken Token, string Name)> segments, out List<long> localIds)
        {
            localIds = new List<long>(segments.Count);

            if (!oldCatalog.TryGetEntryByGuid(prefabGuid, out var entry))
            {
                return false;
            }

            var node = entry.Root;
            foreach (var (_, name) in segments)
            {
                FacadeChild? matched = null;
                foreach (var child in node.Children)
                {
                    if (string.Equals(child.PropertyName, name, StringComparison.Ordinal))
                    {
                        matched = child;
                        break;
                    }
                }

                if (matched == null)
                {
                    return false;
                }

                localIds.Add(matched.LocalId);
                node = matched.Node;
            }

            return true;
        }

        // NEW resolve (by LocalId): re-walks newCatalog's entry for prefabGuid using the recorded
        // durable LocalId sequence. If EVERY recorded LocalId is found, returns the corresponding
        // (possibly renamed) PropertyNames. Any missing LocalId at any depth means the durable
        // target vanished — returns false so the caller abandons the WHOLE selector.
        private static bool TryResolveNewNames(FacadeCatalog newCatalog, string prefabGuid, List<long> localIds, out List<string> newNames)
        {
            newNames = new List<string>(localIds.Count);

            if (!newCatalog.TryGetEntryByGuid(prefabGuid, out var entry))
            {
                return false;
            }

            var node = entry.Root;
            foreach (var localId in localIds)
            {
                FacadeChild? matched = null;
                foreach (var child in node.Children)
                {
                    if (child.LocalId == localId)
                    {
                        matched = child;
                        break;
                    }
                }

                if (matched == null)
                {
                    return false;
                }

                newNames.Add(matched.PropertyName);
                node = matched.Node;
            }

            return true;
        }
    }
}
