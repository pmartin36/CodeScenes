using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Scene->code reconcile of a `[SerializeReference]` field authored via `.SetRef(...)`: a field
    // edit or a concrete-type change re-renders the WHOLE value span from the live model (no
    // member-level patch); a null<->non-null transition swaps the whole span too; a nested
    // instance's change re-renders the whole outer span, recursively; and a live value whose
    // recorded type resolves to no loadable C# type is a located conflict, never a silent null or
    // an unpatched source.
    public class ManagedReferenceReconcileTests
    {
        // Marker sentinel a live `[SerializeReference]` value whose `managedReferenceFullTypename`
        // resolves to no loadable type is reported through. Ridden on the existing `Unsupported`
        // container so no new `ValueNode` inhabitant is required; the payload prefix distinguishes
        // this located, reported case from a plain unrelated `Unsupported` node.
        private const string MissingTypeMarker = "__csManagedRefMissingType:";

        private static string Source(string setRefArg) => $@"
public class SetRefReconcileScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Add(""Enemy"").Component<AiBrain>(c => c.SetRef(x => x.strategy, {setRefArg}));
    }}
}}
";

        private static (ParseResult Parsed, IdentityMap Map, string ComponentLogicalId) ParseWithMappedComponent(
            string setRefArg)
        {
            var parsed = BuilderParser.Parse(Source(setRefArg));
            var ownerLogicalId = Assert.Single(parsed.Model.Roots).LogicalId;
            var componentLogicalId = $"{ownerLogicalId}/AiBrain#0";

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-enemy", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = componentLogicalId,
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = "AiBrain",
                        ParentLogicalId = ownerLogicalId,
                    },
                },
            };

            return (parsed, map, componentLogicalId);
        }

        private static SceneSnapshot SnapshotWithStrategy(ValueNode strategy, ComponentData[]? componentDefaults = null) =>
            new()
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-enemy",
                        Name = "Enemy",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef("AiBrain"),
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("member:strategy", strategy) }),
                            },
                        },
                    },
                },
                ComponentDefaults = componentDefaults ?? System.Array.Empty<ComponentData>(),
            };

        private static readonly ValueNode Aggressive5 = new ValueNode.ManagedReference(
            new TypeRef("Aggressive"), new FieldMap(new[] { new KeyValuePair<string, ValueNode>("range", ValueNode.Primitive.Float(5f)) }));

        // deliverable a: a concrete-type change re-renders the WHOLE value span with the new type
        // and fields, no stale text from the old type.
        [Fact]
        public void Reconcile_ManagedRefConcreteTypeChanged_PatchesWholeSpanWithNewTypeNoStaleFields()
        {
            var (parsed, map, componentLogicalId) = ParseWithMappedComponent("new Aggressive { range = 5f }");
            var flee3 = new ValueNode.ManagedReference(
                new TypeRef("Flee"), new FieldMap(new[] { new KeyValuePair<string, ValueNode>("speed", ValueNode.Primitive.Float(3f)) }));

            var result = Reconciler.Reconcile(
                parsed.Model, SnapshotWithStrategy(flee3), map, parsed.Anchors, null, null,
                parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal(componentLogicalId, patch.Anchor);
            Assert.Equal(parsed.FieldArgumentSpans[componentLogicalId]["member:strategy"], patch.ValueSpan);
            Assert.Equal(SourceExpr.ValueNodeLiteral(flee3), patch.NewExpr);
            Assert.DoesNotContain("Aggressive", patch.NewExpr);
            Assert.DoesNotContain("range", patch.NewExpr);
        }

        // deliverable b: a field-only edit (same concrete type) re-renders the whole value span.
        [Fact]
        public void Reconcile_ManagedRefFieldChanged_PatchesWholeSpanWithNewFieldValue()
        {
            var (parsed, map, componentLogicalId) = ParseWithMappedComponent("new Aggressive { range = 5f }");
            var aggressive9 = new ValueNode.ManagedReference(
                new TypeRef("Aggressive"), new FieldMap(new[] { new KeyValuePair<string, ValueNode>("range", ValueNode.Primitive.Float(9f)) }));

            var result = Reconciler.Reconcile(
                parsed.Model, SnapshotWithStrategy(aggressive9), map, parsed.Anchors, null, null,
                parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal(SourceExpr.ValueNodeLiteral(aggressive9), patch.NewExpr);
        }

        // deliverable c (actual null): a live value that went null patches the whole span to `null`.
        [Fact]
        public void Reconcile_ManagedRefActualWentNull_PatchesSpanToNullLiteral()
        {
            var (parsed, map, _) = ParseWithMappedComponent("new Aggressive { range = 5f }");
            var nullRef = new ValueNode.ManagedReference(null, FieldMap.Empty);

            var result = Reconciler.Reconcile(
                parsed.Model, SnapshotWithStrategy(nullRef), map, parsed.Anchors, null, null,
                parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("null", patch.NewExpr);
        }

        // deliverable c (source was null): a live value that gained a concrete instance adds the
        // assignment.
        [Fact]
        public void Reconcile_ManagedRefSourceWasNull_PatchesSpanWithNewInstance()
        {
            var (parsed, map, _) = ParseWithMappedComponent("null");

            var result = Reconciler.Reconcile(
                parsed.Model, SnapshotWithStrategy(Aggressive5), map, parsed.Anchors, null, null,
                parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal(SourceExpr.ValueNodeLiteral(Aggressive5), patch.NewExpr);
        }

        // deliverable d: a change to a nested managed-ref member re-renders the WHOLE outer span,
        // recursively, including the sibling member that did not change.
        [Fact]
        public void Reconcile_NestedManagedRefMemberChanged_PatchesWholeOuterSpanRecursively()
        {
            var (parsed, map, _) = ParseWithMappedComponent(
                "new Composite { primary = new Aggressive { range = 5f }, fallback = new Flee { speed = 3f } }");

            var primary9 = new ValueNode.ManagedReference(
                new TypeRef("Aggressive"), new FieldMap(new[] { new KeyValuePair<string, ValueNode>("range", ValueNode.Primitive.Float(9f)) }));
            var fallback3 = new ValueNode.ManagedReference(
                new TypeRef("Flee"), new FieldMap(new[] { new KeyValuePair<string, ValueNode>("speed", ValueNode.Primitive.Float(3f)) }));
            var composite = new ValueNode.ManagedReference(
                new TypeRef("Composite"), new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("primary", primary9),
                    new KeyValuePair<string, ValueNode>("fallback", fallback3),
                }));

            var result = Reconciler.Reconcile(
                parsed.Model, SnapshotWithStrategy(composite), map, parsed.Anchors, null, null,
                parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal(SourceExpr.ValueNodeLiteral(composite), patch.NewExpr);
            Assert.Contains("range = 9f", patch.NewExpr);
            Assert.Contains("speed = 3f", patch.NewExpr);
        }

        // Idempotence: an unchanged managed ref emits no patch and no conflict.
        [Fact]
        public void Reconcile_ManagedRefUnchanged_EmitsNoPatchAndNoConflict()
        {
            var (parsed, map, _) = ParseWithMappedComponent("new Aggressive { range = 5f }");

            var result = Reconciler.Reconcile(
                parsed.Model, SnapshotWithStrategy(Aggressive5), map, parsed.Anchors, null, null,
                parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Empty(result.Conflicts);
        }

        // deliverable e: a live value whose recorded type resolves to no loadable C# type is a
        // located, standing note naming the field and the type; the source is NOT patched and the
        // reference is NOT nulled. A standing (recurrence-keyed) report routes to Notes, never
        // Conflicts (ReconcileResult.cs) -- the same Conflict never appears in both arrays.
        [Fact]
        public void Reconcile_ManagedRefUnresolvableType_EmitsLocatedNoteNoPatchNoNull()
        {
            var (parsed, map, componentLogicalId) = ParseWithMappedComponent("new Aggressive { range = 5f }");
            var missingType = new ValueNode.Unsupported(MissingTypeMarker + "Ghost.Strategy, Asm");

            var result = Reconciler.Reconcile(
                parsed.Model, SnapshotWithStrategy(missingType), map, parsed.Anchors, null, null,
                parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.DoesNotContain(result.Patch.Edits, e => e is PatchComponentField p && p.NewExpr == "null");
            Assert.Empty(result.Conflicts);

            var note = Assert.Single(result.Notes);
            Assert.Equal(componentLogicalId, note.LogicalId);
            Assert.Contains("member:strategy", note.Reason);
            Assert.Contains("Ghost.Strategy, Asm", note.Reason);
        }

        // A `[SerializeReference]` field's constructed default is null, same as every other field
        // kind's default-removal candidate. A live-null managed ref must still patch the span to
        // `null`, never fall into the generic default-removal branch — that branch's applier
        // matches only a `.Set(...)` call and has no form for a `.SetRef(...)` statement.
        [Fact]
        public void Reconcile_ManagedRefWentNullAtConstructedDefault_PatchesNullRatherThanRemovesField()
        {
            var (parsed, map, _) = ParseWithMappedComponent("new Aggressive { range = 5f }");
            var nullRef = new ValueNode.ManagedReference(null, FieldMap.Empty);
            var defaultTemplate = new[]
            {
                new ComponentData
                {
                    LogicalId = "default",
                    Type = new TypeRef("AiBrain"),
                    Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("member:strategy", nullRef) }),
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, SnapshotWithStrategy(nullRef, defaultTemplate), map, parsed.Anchors, null, null,
                parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            Assert.Empty(result.Patch.Edits.OfType<RemoveComponentField>());
            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("null", patch.NewExpr);
        }
    }
}
