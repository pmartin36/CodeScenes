using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Parsing
{
    // `scene.Instance("path")` / `handle.Instance("path")` parse arms, split out of
    // BuilderParser.cs for file-size discipline (BuilderParser.cs is near its 1000-line budget).
    // Mirrors ProcessAddChain's receiver-resolution + handle-registration shape (b2-t2); the
    // instance's `name`/LogicalId derives from the prefab-path STEM ONLY (never a live prefab
    // root name — that lands at snapshot-read, b5-t2).
    public static partial class BuilderParser
    {
        private static void ProcessInstanceChain(IdentifierNameSyntax receiver, List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> calls, string? handleName, ParserContext ctx)
        {
            NodeBuilder? parentNode = null;
            List<NodeBuilder> targetList;

            if (receiver.Identifier.Text == ctx.SceneParamName)
            {
                targetList = ctx.Roots;
            }
            else if (ctx.Handles.TryGetValue(receiver.Identifier.Text, out parentNode))
            {
                targetList = parentNode.Children;
            }
            else
            {
                throw Fail(receiver, $"Unknown receiver '{receiver.Identifier.Text}'");
            }

            var instanceArgs = calls[0].Args.Arguments;
            if (instanceArgs.Count == 0)
            {
                throw Fail(calls[0].Args, "Instance requires a prefab-path argument");
            }

            var path = EvalStringLiteral(instanceArgs[0].Expression);
            var stem = Path.GetFileNameWithoutExtension(path);

            var node = new NodeBuilder { Name = stem, IsInstance = true, SourcePrefabPath = path };
            node.AnchorSpan = new SourceSpan(calls[0].Invocation.Span.Start, calls[0].Invocation.Span.Length);

            var explicitId = ApplyChainedCalls(node, calls.Skip(1).ToList());

            var siblingIndex = targetList.Count;
            var parentLogicalId = parentNode?.LogicalId;
            node.LogicalId = ctx.Resolver.Resolve(handleName, explicitId, parentLogicalId, stem, siblingIndex);

            targetList.Add(node);
            if (handleName != null)
            {
                ctx.Handles[handleName] = node;
                node.Handle = handleName;
            }
        }

        // Mirrors BuildPlainNode's base-field copy, but emits a PrefabInstanceNode: Components
        // stays empty (v1 — whole-instance only, no per-component authoring) and SourcePrefab
        // carries the unresolved DisplayPath (GUID lowering is b2-t3's job, not parse's).
        private static PrefabInstanceNode BuildInstanceNode(NodeBuilder builder) => new()
        {
            LogicalId = builder.LogicalId,
            Name = builder.Name,
            Tag = builder.Tag,
            Layer = builder.Layer,
            Active = builder.Active,
            IsStatic = builder.IsStatic,
            Transform = new TransformData
            {
                Position = builder.Position ?? Vec3.Zero,
                Rotation = builder.Rotation ?? Quat.Identity,
                Scale = builder.Scale ?? Vec3.One,
                DrivenChannels = builder.DrivenChannels,
            },
            Components = System.Array.Empty<ComponentData>(),
            Children = builder.Children.Select(BuildNode).ToArray(),
            SourcePrefab = new AssetRef { DisplayPath = builder.SourcePrefabPath ?? "" },
            OpaqueOverrides = null,
        };

        // Mirrors the plain-GameObject IdentityMapEntry construction in CollectIdentityEntries,
        // but with Kind="PrefabInstance". PrefabKey/SourcePrefabGuid are unresolved at parse time
        // (filled in by b2-t3 lowering / b5-t3 build) UNLESS `existingEntry` (this LogicalId's entry
        // in the map passed into BuilderParser.Parse) already carries them — a re-parse after a
        // structural move (b4-t2) rebuilds every entry from scratch, so those fields must be
        // re-fetched from the prior map the same way GlobalObjectId already is, or a moved-but-not-
        // renamed instance loses its prefab identity on every syncback pass.
        private static IdentityMapEntry BuildInstanceIdentityEntry(NodeBuilder node, string? parentLogicalId, int siblingIndex, Dictionary<string, string> globalObjectIdByLogicalId, IdentityMapEntry? existingEntry) => new()
        {
            LogicalId = node.LogicalId,
            GlobalObjectId = globalObjectIdByLogicalId.TryGetValue(node.LogicalId, out var globalObjectId) ? globalObjectId : "",
            Kind = "PrefabInstance",
            ComponentType = null,
            ParentLogicalId = parentLogicalId,
            Name = node.Name,
            SiblingIndex = siblingIndex,
            PrefabKey = existingEntry?.PrefabKey,
            SourcePrefabGuid = existingEntry?.SourcePrefabGuid,
        };
    }
}
