using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;
using static SceneBuilder.Core.Tests.SourcePatchTestHelpers;

namespace SceneBuilder.Core.Tests
{
    public class SourcePatchEmittedUsingsTests
    {
        // ---- Emitted code must COMPILE, not merely parse -------------------------------------

        // A real user's builder: it references NO asset, so it has no reason to import the Asset
        // factory. Sync introducing an Asset(...) here is what shipped CS0103.
        private const string NoAssetUsingFixture = @"
using SceneBuilder.Authoring;
public class NoAssetUsingScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var surface = scene.Add(""Surface"");
        surface.Component<UnityEngine.MeshRenderer>();
    }
}
";

        private static SourcePatch AssetFieldPatch(string componentAnchor) => new SourcePatch
        {
            Edits = new SourceEdit[]
            {
                new IntroduceComponentField
                {
                    Anchor = componentAnchor,
                    FieldKey = "m_Materials",
                    Value = new ValueNode.AssetRef(new AssetRef
                    {
                        Guid = "abc123",
                        FileId = 0,
                        DisplayPath = "Assets/Materials/Red.mat",
                    }),
                },
            },
        };

        // b1-t4: both `Asset(...)` and `Builtin(...)` are factories on the SAME `AssetRefs` static
        // class, so the SAME single using directive must cover a patch that only introduces `Builtin(...)`.
        private static SourcePatch BuiltinFieldPatch(string componentAnchor) => new SourcePatch
        {
            Edits = new SourceEdit[]
            {
                new IntroduceComponentField
                {
                    Anchor = componentAnchor,
                    FieldKey = "m_Mesh",
                    Value = new ValueNode.AssetRef(new AssetRef
                    {
                        IsBuiltin = true,
                        DisplayPath = "Cube",
                        TypeHint = "",
                    }),
                },
            },
        };

        [Fact]
        public void Apply_IntroducingAssetCall_AddsStaticAssetRefsUsing()
        {
            var parsed = BuilderParser.Parse(NoAssetUsingFixture);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);

            var result = SourcePatchApplier.Apply(NoAssetUsingFixture, AssetFieldPatch(compId), MergeAnchors(parsed));

            // The short, readable call is kept — and the import that makes it compile is emitted.
            Assert.Contains("Asset(\"Assets/Materials/Red.mat\")", result);
            Assert.Contains("using static SceneBuilder.Authoring.AssetRefs;", result);

            // The directive is well-formed: keyword spacing intact, and it precedes the type.
            Assert.DoesNotContain("usingstatic", result);
            Assert.True(
                result.IndexOf("using static SceneBuilder.Authoring.AssetRefs;", StringComparison.Ordinal)
                    < result.IndexOf("public class NoAssetUsingScene", StringComparison.Ordinal),
                "The using directive must precede the type declaration.");

            // The pre-existing using survives.
            Assert.Contains("using SceneBuilder.Authoring;", result);
        }

        // A file that already imports the factory must not collect a second copy (CS0105 — and only
        // a WARNING, so a compile assertion alone would never catch the duplication).
        [Fact]
        public void Apply_AssetCallWhenUsingAlreadyPresent_DoesNotDuplicateIt()
        {
            var source = NoAssetUsingFixture.Replace(
                "using SceneBuilder.Authoring;",
                "using SceneBuilder.Authoring;\nusing static SceneBuilder.Authoring.AssetRefs;");
            var parsed = BuilderParser.Parse(source);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);

            var result = SourcePatchApplier.Apply(source, AssetFieldPatch(compId), MergeAnchors(parsed));

            var occurrences = result.Split(new[] { "using static SceneBuilder.Authoring.AssetRefs;" }, StringSplitOptions.None).Length - 1;
            Assert.Equal(1, occurrences);
        }

        // A patch that touches no asset must not gain the import out of nowhere.
        [Fact]
        public void Apply_PatchWithoutAssetCall_DoesNotAddAssetRefsUsing()
        {
            var parsed = BuilderParser.Parse(NoAssetUsingFixture);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new IntroduceComponentField
                    {
                        Anchor = compId,
                        FieldKey = "m_Enabled",
                        Value = ValueNode.Primitive.Bool(false),
                    },
                },
            };

            var result = SourcePatchApplier.Apply(NoAssetUsingFixture, patch, MergeAnchors(parsed));

            Assert.DoesNotContain("AssetRefs", result);
        }

        // b1-t4: the FIRST `Builtin(...)` in a file with neither an `Asset(...)` call nor the using
        // must gain the SAME single directive — `Builtin` is not a second factory to guard separately.
        [Fact]
        public void Apply_IntroducingBuiltinCall_AddsStaticAssetRefsUsing()
        {
            var parsed = BuilderParser.Parse(NoAssetUsingFixture);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);

            var result = SourcePatchApplier.Apply(NoAssetUsingFixture, BuiltinFieldPatch(compId), MergeAnchors(parsed));

            Assert.Contains("Builtin(\"Cube\")", result);
            Assert.Contains("using static SceneBuilder.Authoring.AssetRefs;", result);
            Assert.DoesNotContain("usingstatic", result);
            Assert.True(
                result.IndexOf("using static SceneBuilder.Authoring.AssetRefs;", StringComparison.Ordinal)
                    < result.IndexOf("public class NoAssetUsingScene", StringComparison.Ordinal),
                "The using directive must precede the type declaration.");
            Assert.Contains("using SceneBuilder.Authoring;", result);
        }

        // A file already importing the factory (however it got there) must not collect a second copy
        // when the NEW call introduced by the patch is `Builtin(...)` rather than `Asset(...)`.
        [Fact]
        public void Apply_BuiltinCallWhenUsingAlreadyPresent_DoesNotDuplicateIt()
        {
            var source = NoAssetUsingFixture.Replace(
                "using SceneBuilder.Authoring;",
                "using SceneBuilder.Authoring;\nusing static SceneBuilder.Authoring.AssetRefs;");
            var parsed = BuilderParser.Parse(source);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);

            var result = SourcePatchApplier.Apply(source, BuiltinFieldPatch(compId), MergeAnchors(parsed));

            var occurrences = result.Split(new[] { "using static SceneBuilder.Authoring.AssetRefs;" }, StringSplitOptions.None).Length - 1;
            Assert.Equal(1, occurrences);
        }
    }
}
