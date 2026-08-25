using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Validation;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // PlanningValidator.Validate's asset walk (WalkAssetValue) descends List/Nested containers:
    // an AssetRef reachable only through a container is diagnosed exactly like a top-level one,
    // located at the enclosing TOP-LEVEL field's span regardless of how deep the ref sits, visited
    // in pre-order, and a Deferred/Resolved ref inside a container yields no diagnostic.
    public class PlanningValidatorDescentTests
    {
        private const string ComponentContainerAssetFieldScene = @"
public class ComponentContainerAssetFieldScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Player"").Component<UnityEngine.MeshRenderer>(mr => mr.Set(""payload"", new Game.Payload { mats = new[] { Asset(""Assets/Materials/Missing.mat"") } }));
    }
}
";

        private const string ComponentMultiContainerAssetFieldScene = @"
public class ComponentMultiContainerAssetFieldScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Player"").Component<UnityEngine.MeshRenderer>(mr => {
            mr.Set(""payload"", new Game.Payload { mats = new[] { Asset(""Assets/A.mat""), Asset(""Assets/B.mat"", ""Sub"") }, one = Builtin(""Cube""), deep = new Game.Inner { two = Builtin(""Sphere"", ""Mesh"") } });
            mr.Set(""plain"", Asset(""Assets/Top.mat""));
        });
    }
}
";

        private sealed class StubResolutionProvider : IResolutionProvider
        {
            public Func<TypeRef, IReadOnlyList<string>, TypeResolution> OnResolveComponentType { get; set; } =
                (_, _) => new TypeResolution.Deferred();

            public Func<string, string?, AssetResolution> OnResolveAssetPath { get; set; } =
                (_, _) => new AssetResolution.Deferred();

            public Func<string, string?, AssetResolution> OnResolveBuiltin { get; set; } =
                (_, _) => new AssetResolution.Deferred();

            public TypeResolution ResolveComponentType(TypeRef type, IReadOnlyList<string> usings) =>
                OnResolveComponentType(type, usings);

            public AssetResolution ResolveAssetPath(string displayPath, string? subAsset, AssetFieldContext? field = null) =>
                OnResolveAssetPath(displayPath, subAsset);

            public AssetResolution ResolveBuiltin(string name, string? typeHint) =>
                OnResolveBuiltin(name, typeHint);
        }

        // Copy of SceneBuilderBuild.LineOf/ColumnOf: expectations are computed with the same
        // offset->line/col algorithm the SUT uses, rather than hard-coded numbers.
        private static int LineOf(string source, int offset)
        {
            var line = 1;
            for (var i = 0; i < offset && i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        private static int ColumnOf(string source, int offset)
        {
            var lineStart = source.LastIndexOf('\n', Math.Min(offset, source.Length - 1));
            return offset - lineStart;
        }

        [Fact]
        public void Validate_AssetRefReachableOnlyThroughContainers_YieldsLocatedDiagnostic()
        {
            var source = ComponentContainerAssetFieldScene;
            var parse = BuilderParser.Parse(source);
            var stub = new StubResolutionProvider
            {
                OnResolveComponentType = (_, _) => new TypeResolution.Resolved("UnityEngine.MeshRenderer"),
                OnResolveAssetPath = (_, _) => new AssetResolution.Unresolved(Array.Empty<string>()),
            };

            var result = PlanningValidator.Validate(parse, source, stub);

            Assert.False(result.Ok);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.AssetPathNotFound, diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal("Asset path 'Assets/Materials/Missing.mat' not found (no .meta on disk).", diagnostic.Message);

            var node = Assert.Single(parse.Model.Roots);
            var component = Assert.Single(node.Components);
            var span = parse.FieldArgumentSpans[component.LogicalId]["payload"];
            Assert.Equal(LineOf(source, span.Start), diagnostic.Line);
            Assert.Equal(ColumnOf(source, span.Start), diagnostic.Col);
        }

        [Fact]
        public void Validate_MultipleRefsUnderOneField_ReportedInPreOrderUnderTheTopLevelKey()
        {
            var source = ComponentMultiContainerAssetFieldScene;
            var parse = BuilderParser.Parse(source);
            var assetPathCalls = new List<(string path, string? sub)>();
            var builtinCalls = new List<(string name, string? typeHint)>();
            var stub = new StubResolutionProvider
            {
                OnResolveComponentType = (_, _) => new TypeResolution.Resolved("UnityEngine.MeshRenderer"),
                OnResolveAssetPath = (path, sub) =>
                {
                    assetPathCalls.Add((path, sub));
                    return new AssetResolution.Unresolved(Array.Empty<string>());
                },
                OnResolveBuiltin = (name, typeHint) =>
                {
                    builtinCalls.Add((name, typeHint));
                    return new AssetResolution.Unresolved(Array.Empty<string>());
                },
            };

            var result = PlanningValidator.Validate(parse, source, stub);

            Assert.Equal(
                new[]
                {
                    "Asset path 'Assets/A.mat' not found (no .meta on disk).",
                    "Asset path 'Assets/B.mat' not found (no .meta on disk).",
                    "Asset path 'Cube' not found (no .meta on disk).",
                    "Asset path 'Sphere' not found (no .meta on disk).",
                    "Asset path 'Assets/Top.mat' not found (no .meta on disk).",
                },
                result.Diagnostics.Select(d => d.Message).ToArray());
            Assert.Equal(
                new[] { ("Assets/A.mat", (string?)null), ("Assets/B.mat", (string?)"Sub"), ("Assets/Top.mat", (string?)null) },
                assetPathCalls);
            Assert.Equal(new[] { ("Cube", (string?)""), ("Sphere", (string?)"Mesh") }, builtinCalls);

            var node = Assert.Single(parse.Model.Roots);
            var component = Assert.Single(node.Components);
            var payloadSpan = parse.FieldArgumentSpans[component.LogicalId]["payload"];
            var plainSpan = parse.FieldArgumentSpans[component.LogicalId]["plain"];
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(LineOf(source, payloadSpan.Start), result.Diagnostics[i].Line);
                Assert.Equal(ColumnOf(source, payloadSpan.Start), result.Diagnostics[i].Col);
            }

            Assert.Equal(LineOf(source, plainSpan.Start), result.Diagnostics[4].Line);
            Assert.Equal(ColumnOf(source, plainSpan.Start), result.Diagnostics[4].Col);
        }

        [Fact]
        public void Validate_DeferredResolutionInsideContainers_YieldsNoDiagnostic()
        {
            var source = ComponentContainerAssetFieldScene;
            var parse = BuilderParser.Parse(source);
            var stub = new StubResolutionProvider
            {
                OnResolveComponentType = (_, _) => new TypeResolution.Resolved("UnityEngine.MeshRenderer"),
            };

            var result = PlanningValidator.Validate(parse, source, stub);

            Assert.True(result.Ok);
            Assert.Empty(result.Diagnostics);
        }
    }
}
