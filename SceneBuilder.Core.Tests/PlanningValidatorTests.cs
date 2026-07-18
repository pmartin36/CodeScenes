using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Tests.Fixtures;
using SceneBuilder.Core.Validation;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b2-t1: PlanningValidator.Validate(parse, source, resolver, file) — the ONE shared
    // collect-all planning walk both the editor Build (b3) and the headless validator (b4)
    // drive. See research.md Blueprint/DATA_FLOW for the exact order (ambiguities -> types ->
    // assets) and the offset->line/col conversion (copied from SceneBuilderBuild.LineOf/ColumnOf
    // so both callers agree byte-for-byte — reproduced locally here to compute expectations).
    public class PlanningValidatorTests
    {
        // A resolver whose four call sites are independently configurable per test. Defaults to
        // Deferred everywhere so an unconfigured call site never accidentally yields an error.
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

            public AssetResolution ResolveAssetPath(string displayPath, string? subAsset) =>
                OnResolveAssetPath(displayPath, subAsset);

            public AssetResolution ResolveBuiltin(string name, string? typeHint) =>
                OnResolveBuiltin(name, typeHint);
        }

        // Proves step 1 (ambiguities) never consults the resolver: any call is a test failure.
        private sealed class ThrowingResolutionProvider : IResolutionProvider
        {
            public TypeResolution ResolveComponentType(TypeRef type, IReadOnlyList<string> usings) =>
                throw new InvalidOperationException("resolver must not be consulted for structural ambiguities");

            public AssetResolution ResolveAssetPath(string displayPath, string? subAsset) =>
                throw new InvalidOperationException("resolver must not be consulted for structural ambiguities");

            public AssetResolution ResolveBuiltin(string name, string? typeHint) =>
                throw new InvalidOperationException("resolver must not be consulted for structural ambiguities");
        }

        // Verbatim copy of SceneBuilderBuild.LineOf/ColumnOf (SceneBuilderBuild.cs:176-194) —
        // the walk must convert offsets the SAME way, so tests compute expectations with the
        // same algorithm rather than hard-coding numbers.
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
        public void Validate_TypeResolvesGivenUsing_NoDiagnostic()
        {
            var source = BuilderFixtures.ComponentWithRawField;
            var parse = BuilderParser.Parse(source);
            var stub = new StubResolutionProvider
            {
                OnResolveComponentType = (_, _) => new TypeResolution.Resolved("UnityEngine.Rigidbody"),
            };

            var result = PlanningValidator.Validate(parse, source, stub);

            Assert.True(result.Ok);
            Assert.Empty(result.Diagnostics);
        }

        [Fact]
        public void Validate_UnqualifiedUnknownType_YieldsLocatedDiagnosticWithSuggestion()
        {
            var source = BuilderFixtures.ComponentWithRawField;
            var parse = BuilderParser.Parse(source);
            var stub = new StubResolutionProvider
            {
                OnResolveComponentType = (_, _) => new TypeResolution.Unresolved(new[] { "UnityEngine.Rigidbody" }),
            };

            var result = PlanningValidator.Validate(parse, source, stub);

            Assert.False(result.Ok);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.UnresolvedType, diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.False(string.IsNullOrEmpty(diagnostic.Suggestion));

            var node = Assert.Single(parse.Model.Roots);
            var component = Assert.Single(node.Components);
            var anchor = parse.ComponentAnchors[component.LogicalId];
            Assert.Equal(LineOf(source, anchor.Start), diagnostic.Line);
            Assert.Equal(ColumnOf(source, anchor.Start), diagnostic.Col);
        }

        [Fact]
        public void Validate_AmbiguousType_YieldsAmbiguousDiagnosticListingCandidates()
        {
            var source = BuilderFixtures.ComponentWithRawField;
            var parse = BuilderParser.Parse(source);
            var stub = new StubResolutionProvider
            {
                OnResolveComponentType = (_, _) =>
                    new TypeResolution.Ambiguous(new[] { "UnityEngine.Rigidbody", "MyGame.Physics.Rigidbody" }),
            };

            var result = PlanningValidator.Validate(parse, source, stub);

            Assert.False(result.Ok);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.AmbiguousType, diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains("UnityEngine.Rigidbody", diagnostic.Message);
            Assert.Contains("MyGame.Physics.Rigidbody", diagnostic.Message);
        }

        [Fact]
        public void Validate_BadAssetPath_YieldsLocatedDiagnostic()
        {
            var source = BuilderFixtures.ComponentWithAssetField;
            var parse = BuilderParser.Parse(source);
            var stub = new StubResolutionProvider
            {
                OnResolveComponentType = (_, _) => new TypeResolution.Resolved("UnityEngine.MeshRenderer"),
                OnResolveAssetPath = (_, _) => new AssetResolution.Unresolved(new[] { "Assets/Materials/Red.mat" }),
            };

            var result = PlanningValidator.Validate(parse, source, stub);

            Assert.False(result.Ok);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.AssetPathNotFound, diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);

            var node = Assert.Single(parse.Model.Roots);
            var component = Assert.Single(node.Components);
            var span = parse.FieldArgumentSpans[component.LogicalId]["sharedMaterial"];
            Assert.Equal(LineOf(source, span.Start), diagnostic.Line);
            Assert.Equal(ColumnOf(source, span.Start), diagnostic.Col);
        }

        [Fact]
        public void Validate_SubAssetRef_PassesSubAssetNameToResolver()
        {
            // b3-t4: WalkAssetValue must forward reference.SubAsset (not hardcode null) so the
            // resolver can scan sub-objects by name.
            var source = BuilderFixtures.ComponentWithSubAssetField;
            var parse = BuilderParser.Parse(source);
            string? capturedSubAsset = "not-called";
            var stub = new StubResolutionProvider
            {
                OnResolveComponentType = (_, _) => new TypeResolution.Resolved("UnityEngine.MeshFilter"),
                OnResolveAssetPath = (_, subAsset) =>
                {
                    capturedSubAsset = subAsset;
                    return new AssetResolution.Deferred();
                },
            };

            PlanningValidator.Validate(parse, source, stub);

            Assert.Equal("BarrelMesh", capturedSubAsset);
        }

        [Fact]
        public void Validate_SubAssetUnresolved_YieldsLocatedDiagnosticWithAvailableNames()
        {
            // b3-t4: a SubAssetUnresolved resolution (path exists, named sub-object doesn't) must
            // yield an SB2101 diagnostic listing the available sub-object names — never a silent
            // main-asset collapse.
            var source = BuilderFixtures.ComponentWithSubAssetField;
            var parse = BuilderParser.Parse(source);
            var stub = new StubResolutionProvider
            {
                OnResolveComponentType = (_, _) => new TypeResolution.Resolved("UnityEngine.MeshFilter"),
                OnResolveAssetPath = (_, _) =>
                    new AssetResolution.SubAssetUnresolved("BarrelMesh", new[] { "BarrelMain", "BarrelHull" }),
            };

            var result = PlanningValidator.Validate(parse, source, stub);

            Assert.False(result.Ok);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.AssetPathNotFound, diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains("BarrelMain", diagnostic.Suggestion);
            Assert.Contains("BarrelHull", diagnostic.Suggestion);

            var node = Assert.Single(parse.Model.Roots);
            var component = Assert.Single(node.Components);
            var span = parse.FieldArgumentSpans[component.LogicalId]["m_Mesh"];
            Assert.Equal(LineOf(source, span.Start), diagnostic.Line);
            Assert.Equal(ColumnOf(source, span.Start), diagnostic.Col);
        }

        [Fact]
        public void Validate_DuplicateSiblings_YieldsAmbiguityDiagnostic()
        {
            var source = BuilderFixtures.TwoPositionalSameNamedSiblings;
            var parse = BuilderParser.Parse(source);
            // Resolver would throw if step 1 consulted it — proves ambiguities are structural.
            var throwing = new ThrowingResolutionProvider();

            var result = PlanningValidator.Validate(parse, source, throwing);

            Assert.False(result.Ok);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.AmbiguousDuplicateSibling, diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);

            var conflict = Assert.Single(parse.Ambiguities);
            Assert.Equal(conflict.Reason, diagnostic.Message);
            Assert.NotNull(conflict.Location);
            Assert.Equal(LineOf(source, conflict.Location!.Value.Start), diagnostic.Line);
            Assert.Equal(ColumnOf(source, conflict.Location!.Value.Start), diagnostic.Col);
        }

        [Fact]
        public void Validate_CleanBuilder_YieldsZeroDiagnostics()
        {
            var source = BuilderFixtures.BareAdd;
            var parse = BuilderParser.Parse(source);
            var stub = new StubResolutionProvider();

            var result = PlanningValidator.Validate(parse, source, stub);

            Assert.True(result.Ok);
            Assert.Empty(result.Diagnostics);
        }

        [Fact]
        public void Validate_MultipleErrors_AllReportedInOnePass()
        {
            var source = BuilderFixtures.PlanningWalkMultiErrorScene;
            var parse = BuilderParser.Parse(source);
            var stub = new StubResolutionProvider
            {
                OnResolveComponentType = (_, _) => new TypeResolution.Unresolved(Array.Empty<string>()),
                OnResolveAssetPath = (_, _) => new AssetResolution.Unresolved(Array.Empty<string>()),
            };

            var result = PlanningValidator.Validate(parse, source, stub);

            Assert.False(result.Ok);
            var codes = result.Diagnostics.Select(d => d.Code).ToArray();
            Assert.Equal(
                new[]
                {
                    DiagnosticCodes.AmbiguousDuplicateSibling,
                    DiagnosticCodes.UnresolvedType,
                    DiagnosticCodes.AssetPathNotFound,
                },
                codes);
        }

        [Fact]
        public void Validate_DeferredResolution_IsNotAnError()
        {
            var typeSource = BuilderFixtures.ComponentWithRawField;
            var typeParse = BuilderParser.Parse(typeSource);
            var deferredType = new StubResolutionProvider
            {
                OnResolveComponentType = (_, _) => new TypeResolution.Deferred(),
            };

            var typeResult = PlanningValidator.Validate(typeParse, typeSource, deferredType);

            Assert.True(typeResult.Ok);
            Assert.Empty(typeResult.Diagnostics);

            var builtinSource = BuilderFixtures.ComponentWithBuiltinField;
            var builtinParse = BuilderParser.Parse(builtinSource);
            var deferredBuiltin = new StubResolutionProvider
            {
                OnResolveComponentType = (_, _) => new TypeResolution.Resolved("UnityEngine.MeshFilter"),
                OnResolveBuiltin = (_, _) => new AssetResolution.Deferred(),
            };

            var builtinResult = PlanningValidator.Validate(builtinParse, builtinSource, deferredBuiltin);

            Assert.True(builtinResult.Ok);
            Assert.Empty(builtinResult.Diagnostics);
        }

        [Fact]
        public void Validate_NeverThrows_OnAnyProviderOutcome()
        {
            var source = BuilderFixtures.PlanningWalkAllVariantsScene;
            var parse = BuilderParser.Parse(source);

            TypeResolution[] typeVariants =
            {
                new TypeResolution.Resolved("UnityEngine.MeshRenderer"),
                new TypeResolution.Unresolved(Array.Empty<string>()),
                new TypeResolution.Ambiguous(new[] { "A", "B" }),
                new TypeResolution.Deferred(),
            };

            AssetResolution[] assetVariants =
            {
                new AssetResolution.Resolved("guid-1", 0, ""),
                new AssetResolution.Unresolved(Array.Empty<string>()),
                new AssetResolution.Ambiguous(new[] { "a", "b" }),
                new AssetResolution.Deferred(),
            };

            foreach (var typeVariant in typeVariants)
            {
                foreach (var assetVariant in assetVariants)
                {
                    var stub = new StubResolutionProvider
                    {
                        OnResolveComponentType = (_, _) => typeVariant,
                        OnResolveAssetPath = (_, _) => assetVariant,
                        OnResolveBuiltin = (_, _) => assetVariant,
                    };

                    var exception = Record.Exception(() => PlanningValidator.Validate(parse, source, stub));

                    Assert.Null(exception);
                }
            }
        }
    }
}
