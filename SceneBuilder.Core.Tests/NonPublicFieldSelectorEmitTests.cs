using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;
using static SceneBuilder.Core.Tests.SourcePatchTestHelpers;

namespace SceneBuilder.Core.Tests
{
    // A component member-set whose typed selector names a serialized field with no compiling public
    // spelling (a private/[SerializeField]-private field with no public property setter) must be
    // rewritten to the string-key form at the one place a value patch touches it — a typed selector
    // over such a field never compiles (CS0122).
    public class NonPublicFieldSelectorEmitTests
    {
        private const string InaccessibleSelectorFixture = @"
public class InaccessibleSelectorScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var crate = scene.Add(""Crate"");
        crate.Component<Game.SecretHolder>(rb => rb.Set(r => r.m_Secret, 5f));
    }
}
";

        [Fact]
        public void Apply_PatchComponentField_InaccessibleTypedSelector_DowngradesToStringForm()
        {
            var source = InaccessibleSelectorFixture;
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);
            var valueSpan = parsed.FieldArgumentSpans[compId]["member:m_Secret"];

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchComponentField
                    {
                        Anchor = compId,
                        ValueSpan = valueSpan,
                        NewExpr = SourceExpr.ValueNodeLiteral(ValueNode.Primitive.Float(8f)),
                    },
                },
            };

            var inaccessible = InaccessibleMemberIndex.Build(new[]
            {
                new InaccessibleMember { Type = new TypeRef("Game.SecretHolder"), MemberName = "m_Secret" },
            });

            var result = SourcePatchApplier.Apply(source, patch, anchors, inaccessibleMembers: inaccessible);

            // The rewritten selector must be the string-key form the value patches, and the source
            // must carry no typed selector for the inaccessible member anywhere -- a standing guard
            // against a non-compiling selector re-appearing.
            Assert.Contains("rb.Set(\"m_Secret\", 8f)", result);
            Assert.DoesNotContain("r.m_Secret", result);
        }

        [Fact]
        public void Apply_PatchComponentField_DifferentMemberMarkedInaccessible_PreservesTypedSelector()
        {
            const string source = @"
public class UnrelatedInaccessibleMarkScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var crate = scene.Add(""Crate"");
        crate.Component<Game.SecretHolder>(rb => rb.Set(r => r.publicField, 5f));
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);
            var valueSpan = parsed.FieldArgumentSpans[compId]["member:publicField"];

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchComponentField
                    {
                        Anchor = compId,
                        ValueSpan = valueSpan,
                        NewExpr = SourceExpr.ValueNodeLiteral(ValueNode.Primitive.Float(8f)),
                    },
                },
            };

            // The index carries an entry for a DIFFERENT member on the same type — the patched
            // member itself is not marked, so its typed selector must survive untouched.
            var inaccessible = InaccessibleMemberIndex.Build(new[]
            {
                new InaccessibleMember { Type = new TypeRef("Game.SecretHolder"), MemberName = "m_Secret" },
            });

            var result = SourcePatchApplier.Apply(source, patch, anchors, inaccessibleMembers: inaccessible);

            Assert.Contains("rb.Set(r => r.publicField, 8f)", result);
        }

        [Fact]
        public void Apply_PatchComponentField_AlreadyStringFormWithInaccessibleMark_StaysByteIdenticalSelector()
        {
            const string source = @"
public class AlreadyStringFormScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var crate = scene.Add(""Crate"");
        crate.Component<Game.SecretHolder>(rb => rb.Set(""m_Secret"", 5f));
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var anchors = MergeAnchors(parsed);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);
            var valueSpan = parsed.FieldArgumentSpans[compId]["m_Secret"];

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchComponentField
                    {
                        Anchor = compId,
                        ValueSpan = valueSpan,
                        NewExpr = SourceExpr.ValueNodeLiteral(ValueNode.Primitive.Float(8f)),
                    },
                },
            };

            var inaccessible = InaccessibleMemberIndex.Build(new[]
            {
                new InaccessibleMember { Type = new TypeRef("Game.SecretHolder"), MemberName = "m_Secret" },
            });

            var result = SourcePatchApplier.Apply(source, patch, anchors, inaccessibleMembers: inaccessible);

            // No lambda selector is ever introduced for an already-string-form key; only the value
            // literal changes.
            Assert.Equal(source.Replace("5f", "8f"), result);
        }
    }
}
