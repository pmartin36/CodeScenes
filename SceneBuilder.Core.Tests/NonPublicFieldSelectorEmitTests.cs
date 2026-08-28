using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;
using static SceneBuilder.Core.Tests.SourcePatchTestHelpers;

namespace SceneBuilder.Core.Tests
{
    // A component member-set whose typed selector names a member the adapter has NOT proven is a
    // safe public selectable member must be rewritten to the string-key form at the one place a
    // value patch touches it -- a typed selector over such a member never compiles (CS0122/CS1061).
    // Safety is a positive-proof whitelist: absence from SafeMemberIndex means NOT proven safe.
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
        public void Apply_PatchComponentField_NotSafeTypedSelector_DowngradesToStringForm()
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

            // A SafeMemberIndex that does NOT contain "m_Secret" for Game.SecretHolder -- the
            // member is not proven safe, so the selector must downgrade.
            var safe = SafeMemberIndex.Build(new[]
            {
                new SafeMember { Type = new TypeRef("Game.SecretHolder"), MemberName = "publicField" },
            });

            var result = SourcePatchApplier.Apply(source, patch, anchors, safeMembers: safe);

            // The rewritten selector must be the string-key form the value patches, and the source
            // must carry no typed selector for the not-safe member anywhere -- a standing guard
            // against a non-compiling selector re-appearing.
            Assert.Contains("rb.Set(\"m_Secret\", 8f)", result);
            Assert.DoesNotContain("r.m_Secret", result);
        }

        [Fact]
        public void Apply_PatchComponentField_MemberIsInSafeSet_PreservesTypedSelector()
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

            // The index proves the PATCHED member itself safe (a public field) -- its typed
            // selector must survive untouched (no over-downgrade).
            var safe = SafeMemberIndex.Build(new[]
            {
                new SafeMember { Type = new TypeRef("Game.SecretHolder"), MemberName = "publicField" },
            });

            var result = SourcePatchApplier.Apply(source, patch, anchors, safeMembers: safe);

            Assert.Contains("rb.Set(r => r.publicField, 8f)", result);
        }

        [Fact]
        public void Apply_PatchComponentField_AlreadyStringFormNotInSafeSet_StaysByteIdenticalSelector()
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

            var safe = SafeMemberIndex.Build(new[]
            {
                new SafeMember { Type = new TypeRef("Game.SecretHolder"), MemberName = "publicField" },
            });

            var result = SourcePatchApplier.Apply(source, patch, anchors, safeMembers: safe);

            // No lambda selector is ever introduced for an already-string-form key; only the value
            // literal changes.
            Assert.Equal(source.Replace("5f", "8f"), result);
        }
    }
}
