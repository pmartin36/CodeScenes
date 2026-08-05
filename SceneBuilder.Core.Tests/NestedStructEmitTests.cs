using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Spec 32 C4: an omitted member of a nested struct/class field means "the type's constructed
    // default". NestedValueEmission is the one module both directions reduce/complete through, so
    // emission carries only what changed and a partial authored value never wipes the rest back to
    // its default. Component/field key spellings below (ColorLike, m_Colors) are deliberately NOT a
    // real Unity type name — Core has no adapter and no compiler, so an arbitrary struct/field
    // shape exercises the same contract as the real UnityEngine.UI.ColorBlock case.
    public class NestedStructEmitTests
    {
        private static ValueNode.Nested Node(string typeName, params (string Key, ValueNode Value)[] fields) =>
            new(typeName, new FieldMap(fields.Select(f => new KeyValuePair<string, ValueNode>(f.Key, f.Value))));

        private static ValueNode.Primitive F(float v) => ValueNode.Primitive.Float(v);

        // ---- NestedValueEmission.Emittable ---------------------------------------------------

        [Fact]
        public void Emittable_KeepsOnlyChangedMember_DropsRestAtDefault()
        {
            var @default = Node("ColorLike", ("a", F(0)), ("b", F(0)), ("c", F(0)));
            var value = Node("ColorLike", ("a", F(1)), ("b", F(0)), ("c", F(0)));

            var emittable = NestedValueEmission.Emittable(value, @default);

            Assert.Equal(Node("ColorLike", ("a", F(1))), emittable);
        }

        [Fact]
        public void Emittable_NestedInNested_RecursesAndDropsAnEmptiedInnerMemberWhole()
        {
            var innerDefault = Node("Inner", ("x", F(0)));
            var @default = Node("Outer", ("y", F(0)), ("inner", innerDefault));
            var value = Node("Outer", ("y", F(1)), ("inner", Node("Inner", ("x", F(0)))));

            var emittable = NestedValueEmission.Emittable(value, @default);

            Assert.Equal(Node("Outer", ("y", F(1))), emittable);
        }

        // Deliverable 5's round trip: the projected node renders through the (unchanged) emitter and
        // parses back through the (unchanged) parser to an equal node.
        [Fact]
        public void Emittable_ProjectedNodeRoundTripsThroughEmitAndParse()
        {
            var @default = Node("UnityEngine.UI.ColorBlock", ("normalColor", F(0)), ("pressedColor", F(0)));
            var value = Node("UnityEngine.UI.ColorBlock", ("normalColor", F(1)), ("pressedColor", F(0)));

            var emittable = NestedValueEmission.Emittable(value, @default);
            var text = SourceExpr.ValueNodeLiteral(emittable);
            var parsedBack = ValueNodeParser.Parse(SyntaxFactory.ParseExpression(text));

            Assert.Equal("new UnityEngine.UI.ColorBlock { normalColor = 1f }", text);
            Assert.Equal(emittable, parsedBack);
        }

        // ---- NestedValueEmission.Project -----------------------------------------------------

        [Fact]
        public void Project_ExcludesUnrepresentableNonDefaultMember_ReportsOneConflict()
        {
            var @default = Node("Nav", ("a", F(0)), ("target", new ValueNode.ObjectRef(null)));
            var value = Node("Nav", ("a", F(1)), ("target", new ValueNode.ObjectRef("some-object")));
            var conflicts = new List<Conflict>();

            var projection = NestedValueEmission.Project(value, @default);
            projection.ReportExclusions("comp-1", "Nav", "m_Nav", conflicts);

            Assert.Equal(Node("Nav", ("a", F(1))), projection.Value);
            var conflict = Assert.Single(conflicts);
            Assert.Equal(ConflictKind.UnrepresentableValue, conflict.Kind);
            Assert.Equal("comp-1", conflict.LogicalId);
            Assert.Contains("m_Nav", conflict.Reason);
            Assert.Contains("target", conflict.Reason);
        }

        [Fact]
        public void Project_UnrepresentableMemberAtDefault_IsSilentlyDroppedNoConflict()
        {
            var @default = Node("Nav", ("a", F(0)), ("target", new ValueNode.ObjectRef(null)));
            var value = Node("Nav", ("a", F(1)), ("target", new ValueNode.ObjectRef(null)));
            var conflicts = new List<Conflict>();

            var projection = NestedValueEmission.Project(value, @default);
            projection.ReportExclusions("comp-1", "Nav", "m_Nav", conflicts);

            Assert.Equal(Node("Nav", ("a", F(1))), projection.Value);
            Assert.Empty(conflicts);
        }

        // A list item is exempt from the representability filter: a list element is not a
        // member-initializer slot, so an item with no compiling struct-member form (an asset
        // reference here) still renders. The same value as a struct MEMBER has no compiling
        // initializer form and is excluded. The exemption stops at the item -- a struct member
        // reached through a list item is filtered again.
        [Fact]
        public void Project_KeepsUnrepresentableListItems_ExcludesTheSameValueAsAStructMember()
        {
            var assetA = new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/a.mat" });
            var assetB = new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/b.mat" });
            var @default = Node("Bag", ("mat", new ValueNode.AssetRef(null)), ("mats", new ValueNode.List(new ValueNode[] { })));
            var value = Node("Bag", ("mat", assetA), ("mats", new ValueNode.List(new ValueNode[] { assetA, assetB })));
            var conflicts = new List<Conflict>();

            var projection = NestedValueEmission.Project(value, @default);
            projection.ReportExclusions("comp-1", "Bag", "m_Bag", conflicts);

            Assert.Equal(Node("Bag", ("mats", new ValueNode.List(new ValueNode[] { assetA, assetB }))), projection.Value);
            Assert.Equal(new[] { "mat" }, projection.ExcludedMembers);
            var conflict = Assert.Single(conflicts);
            Assert.Equal(ConflictKind.UnrepresentableValue, conflict.Kind);
            Assert.Contains("m_Bag", conflict.Reason);
            Assert.Contains("mat", conflict.Reason);
            Assert.Equal(
                "new Bag { mats = new[] { Asset(\"Assets/a.mat\"), Asset(\"Assets/b.mat\") } }",
                SourceExpr.ValueNodeLiteral(projection.Value));
        }

        [Fact]
        public void Project_ResumesExclusionInsideANestedListItem()
        {
            var itemAsset = new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/a.mat" });
            var @default = Node("Bag", ("items", new ValueNode.List(new ValueNode[] { })));
            var value = Node("Bag", ("items", new ValueNode.List(new ValueNode[]
            {
                Node("It", ("n", F(2)), ("mat", itemAsset)),
            })));

            var projection = NestedValueEmission.Project(value, @default);

            Assert.Equal(
                Node("Bag", ("items", new ValueNode.List(new ValueNode[] { Node("It", ("n", F(2))) }))),
                projection.Value);
            Assert.Equal(new[] { "items.mat" }, projection.ExcludedMembers);
        }

        // ---- NestedValueEmission.Complete -----------------------------------------------------

        [Fact]
        public void Complete_FillsOmittedRepresentableMembers_LeavesUnrepresentableAbsent()
        {
            var @default = Node("Nav", ("a", F(0)), ("b", F(0)), ("target", new ValueNode.ObjectRef(null)));
            var value = Node("Nav", ("a", F(1)));

            var completed = NestedValueEmission.Complete(value, @default);

            Assert.Equal(Node("Nav", ("a", F(1)), ("b", F(0))), completed);
        }

        // A legacy/raw-spelling value carrying a key the default does not recognise is returned
        // UNCHANGED — never partially filled from a default it cannot be proven to share a shape
        // with (the data-loss guard: a code->scene build must not overwrite a user's authored
        // value with the type default because the reader now keys by public member instead).
        [Fact]
        public void Complete_ReturnsValueUnchanged_WhenItCarriesAKeyTheDefaultDoesNotKnow()
        {
            var @default = Node("ColorLike", ("a", F(0)), ("b", F(0)));
            var value = Node("ColorLike", ("m_A", F(1)));

            var completed = NestedValueEmission.Complete(value, @default);

            Assert.Equal(value, completed);
        }

        // ---- Reduce / Complete invariants ------------------------------------------------------

        [Fact]
        public void Invariant_EmittableOfCompleted_EqualsEmittableOfOriginal()
        {
            var @default = Node("ColorLike", ("a", F(0)), ("b", F(0)), ("c", F(0)));
            var partial = Node("ColorLike", ("a", F(1)));

            var completed = NestedValueEmission.Complete(partial, @default);

            Assert.Equal(NestedValueEmission.Emittable(partial, @default), NestedValueEmission.Emittable(completed, @default));
        }

        [Fact]
        public void Invariant_CompleteOfReduced_EqualsOriginal_ForAFullyRepresentableValue()
        {
            var @default = Node("ColorLike", ("a", F(0)), ("b", F(0)), ("c", F(0)));
            var full = Node("ColorLike", ("a", F(1)), ("b", F(2)), ("c", F(0)));

            var reduced = NestedValueEmission.Reduce(full, @default);
            var completed = NestedValueEmission.Complete(reduced, @default);

            Assert.Equal(full, completed);
        }

        // Complete's inverse relationship with Reduce must hold one level down too: an inner
        // member reduced away because it sat at ITS default is refilled by Complete, not left
        // absent, so a partially-authored inner struct still round-trips.
        [Fact]
        public void Invariant_CompleteOfReduced_EqualsOriginal_ForANestedInNestedFullyRepresentableValue()
        {
            var innerDefault = Node("Inner", ("x", F(0)), ("y", F(0)));
            var @default = Node("Outer", ("z", F(0)), ("inner", innerDefault));
            var full = Node("Outer", ("z", F(1)), ("inner", Node("Inner", ("x", F(2)), ("y", F(0)))));

            var reduced = NestedValueEmission.Reduce(full, @default);
            var completed = NestedValueEmission.Complete(reduced, @default);

            Assert.Equal(full, completed);
        }

        // Complete recurses into a nested member instead of only filling the OUTER struct's
        // omitted members: an inner member present but partially authored still has its own
        // omitted representable members filled from the inner default.
        [Fact]
        public void Complete_NestedInNested_FillsInnerStructOmittedRepresentableMembers()
        {
            var innerDefault = Node("Inner", ("x", F(0)), ("y", F(0)));
            var @default = Node("Outer", ("z", F(0)), ("inner", innerDefault));
            var value = Node("Outer", ("z", F(1)), ("inner", Node("Inner", ("x", F(2)))));

            var completed = NestedValueEmission.Complete(value, @default);

            Assert.Equal(Node("Outer", ("z", F(1)), ("inner", Node("Inner", ("x", F(2)), ("y", F(0))))), completed);
        }

        // Complete's fill of an ABSENT nested member must apply the same representability filter
        // the fill of an absent TOP-LEVEL member already applies: the inner default's own
        // unrepresentable members (no compiling emission form) are never introduced, at any depth.
        [Fact]
        public void Complete_AbsentNestedMember_FillsOnlyTheRepresentablePartOfItsDefault()
        {
            var innerDefault = Node("Inner", ("z", F(0)), ("font", new ValueNode.AssetRef(null)));
            var @default = Node("Outer", ("y", F(0)), ("inner", innerDefault));
            var value = Node("Outer", ("y", F(1)));

            var completed = NestedValueEmission.Complete(value, @default);

            Assert.Equal(Node("Outer", ("y", F(1)), ("inner", Node("Inner", ("z", F(0))))), completed);
        }

        // When the inner default's filtered fill leaves nothing representable, the member is
        // omitted entirely -- never introduced as an empty initializer.
        [Fact]
        public void Complete_AbsentNestedMemberWhoseDefaultIsEntirelyUnrepresentable_OmitsTheMember()
        {
            var innerDefault = Node("Inner", ("font", new ValueNode.AssetRef(null)));
            var @default = Node("Outer", ("y", F(0)), ("inner", innerDefault));
            var value = Node("Outer", ("y", F(1)));

            var completed = NestedValueEmission.Complete(value, @default);

            Assert.Equal(Node("Outer", ("y", F(1))), completed);
        }

        // ---- Nested member ORDER --------------------------------------------------------------

        // ValueNodeParser records a struct's members in the order the AUTHOR wrote them; the live
        // read reports them in Unity's serialized order. A positional comparison calls those two
        // identical values different forever: the settled scene re-patches every sync and the first
        // sync silently reorders the user's source. Nested-member equality is keyed by member NAME.
        [Fact]
        public void Equality_NestedMembersInADifferentOrder_CompareEqual()
        {
            Assert.Equal(
                Node("ColorLike", ("a", F(1)), ("b", F(2))),
                Node("ColorLike", ("b", F(2)), ("a", F(1))));
        }

        [Fact]
        public void Equality_NestedMembersInADifferentOrder_HashTheSame()
        {
            Assert.Equal(
                Node("ColorLike", ("a", F(1)), ("b", F(2))).GetHashCode(),
                Node("ColorLike", ("b", F(2)), ("a", F(1))).GetHashCode());
        }

        [Fact]
        public void Equality_NestedMembersWithTheSameKeysButDifferentValues_CompareUnequal()
        {
            Assert.NotEqual(
                Node("ColorLike", ("a", F(1)), ("b", F(2))),
                Node("ColorLike", ("b", F(2)), ("a", F(9))));
        }

        [Fact]
        public void Equality_NestedValuesWithDifferentKeySets_CompareUnequal()
        {
            Assert.NotEqual(
                Node("ColorLike", ("a", F(1)), ("b", F(2))),
                Node("ColorLike", ("a", F(1)), ("c", F(2))));
        }

        // Emission order stays the order the value carries — key-insensitive COMPARISON must not
        // become key-sorted RENDERING, or every existing source file gets rewritten once.
        [Fact]
        public void Emit_RendersMembersInTheValuesOwnOrder_NotSorted()
        {
            Assert.Equal(
                "new ColorLike { b = 2f, a = 1f }",
                SourceExpr.ValueNodeLiteral(Node("ColorLike", ("b", F(2)), ("a", F(1)))));
        }

        // ---- Reconciler.Reconcile (scene->code) integration ------------------------------------

        private static (ParseResult Parsed, IdentityMap Map, string ComponentLogicalId) ParseWithMappedRootAndComponent(
            string source, string componentTypeFullName)
        {
            var parsed = BuilderParser.Parse(source);
            var ownerLogicalId = Assert.Single(parsed.Model.Roots).LogicalId;
            var componentLogicalId = $"{ownerLogicalId}/{componentTypeFullName}#0";

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = componentLogicalId,
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = componentTypeFullName,
                        ParentLogicalId = ownerLogicalId,
                    },
                },
            };

            return (parsed, map, componentLogicalId);
        }

        private static ComponentData[] ButtonTemplate(ValueNode colors) => new[]
        {
            new ComponentData
            {
                LogicalId = "template",
                Type = new TypeRef("UnityEngine.UI.Button"),
                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Colors", colors) }),
            },
        };

        // Deliverable 2 (scene->code half): a live edit to ONE member of a nested field patches
        // source with ONLY the members that differ from the type default — never the whole struct.
        [Fact]
        public void Reconcile_LiveEditToOneMember_PatchNamesOnlyNonDefaultMembers()
        {
            const string source = @"
public class NestedEmitScene1 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Component<UnityEngine.UI.Button>(b => b.Set(""m_Colors"", new UnityEngine.UI.ColorBlock { a = 1f }));
    }
}
";
            var (parsed, map, componentLogicalId) = ParseWithMappedRootAndComponent(source, "UnityEngine.UI.Button");
            var @default = Node("UnityEngine.UI.ColorBlock", ("a", F(0)), ("b", F(0)), ("c", F(0)));

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef("UnityEngine.UI.Button"),
                                // Live read is unpruned: every member reported, including the two
                                // still at their default.
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>(
                                        "m_Colors", Node("UnityEngine.UI.ColorBlock", ("a", F(1)), ("b", F(2)), ("c", F(0)))),
                                }),
                            },
                        },
                    },
                },
                ComponentDefaults = ButtonTemplate(@default),
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal(componentLogicalId, patch.Anchor);
            Assert.Equal("new UnityEngine.UI.ColorBlock { a = 1f, b = 2f }", patch.NewExpr);
        }

        // The churn guard: a source-authored partial value that is ALREADY the reduced form of the
        // live value (both equal the default once reduced) is a fixed point — no PatchComponentField,
        // ever. An unreduced field-value comparison (a 1-member source node compared structurally
        // against a 3-member snapshot node) would wrongly see this pair as different.
        [Fact]
        public void Reconcile_PartialSourceAlreadyMatchesReducedLiveValue_EmitsNoEdit()
        {
            const string source = @"
public class NestedEmitScene2 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Component<UnityEngine.UI.Button>(b => b.Set(""m_Colors"", new UnityEngine.UI.ColorBlock { a = 1f }));
    }
}
";
            var (parsed, map, _) = ParseWithMappedRootAndComponent(source, "UnityEngine.UI.Button");
            var @default = Node("UnityEngine.UI.ColorBlock", ("a", F(0)), ("b", F(0)), ("c", F(0)));

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef("UnityEngine.UI.Button"),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>(
                                        "m_Colors", Node("UnityEngine.UI.ColorBlock", ("a", F(1)), ("b", F(0)), ("c", F(0)))),
                                }),
                            },
                        },
                    },
                },
                ComponentDefaults = ButtonTemplate(@default),
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Empty(result.Patch.Edits.OfType<RemoveComponentField>());
        }

        // A settled scene stays settled even when the live value carries a non-default member with
        // no compiling emission form: the comparison must use the SAME projection an emission would
        // use, or a struct like this never converges and re-patches every sync.
        [Fact]
        public void Reconcile_SettledSourceWithNonDefaultUnrepresentableMember_IsFixedPoint()
        {
            const string source = @"
public class NestedEmitScene3 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Component<UnityEngine.UI.Button>(b => b.Set(""m_Colors"", new UnityEngine.UI.ColorBlock { a = 1f }));
    }
}
";
            var (parsed, map, _) = ParseWithMappedRootAndComponent(source, "UnityEngine.UI.Button");
            var @default = Node("UnityEngine.UI.ColorBlock", ("a", F(0)), ("target", new ValueNode.ObjectRef(null)));

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef("UnityEngine.UI.Button"),
                                // The authored member matches; an unrelated member the user never
                                // touched carries a non-default reference no initializer can spell.
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>(
                                        "m_Colors", Node("UnityEngine.UI.ColorBlock", ("a", F(1)), ("target", new ValueNode.ObjectRef("some-object")))),
                                }),
                            },
                        },
                    },
                },
                ComponentDefaults = ButtonTemplate(@default),
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Empty(result.Conflicts);
        }

        // The anti-spam guard: a field never authored in source, whose live value's only member has
        // no compiling emission form and sits at its template value, must emit nothing and report
        // nothing -- a struct that never produces an edit must never produce a conflict either, or
        // a stock component logs on every single sync with no user action at all.
        [Fact]
        public void Reconcile_IntroduceOfAllUnrepresentableFieldAtTemplateValue_ReportsNoConflictAndNoEdit()
        {
            const string source = @"
public class NestedEmitScene5 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Component<UnityEngine.UI.Button>();
    }
}
";
            var (parsed, map, _) = ParseWithMappedRootAndComponent(source, "UnityEngine.UI.Button");
            var call = new ValueNode.Unsupported("no public member for 'm_Call'");
            var @default = Node("UnityEngine.UI.ColorBlock", ("a", F(0)), ("call", call));

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef("UnityEngine.UI.Button"),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>("m_Colors", Node("UnityEngine.UI.ColorBlock", ("a", F(0)), ("call", call))),
                                }),
                            },
                        },
                    },
                },
                ComponentDefaults = ButtonTemplate(@default),
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            Assert.Empty(result.Patch.Edits.OfType<IntroduceComponentField>());
            Assert.Empty(result.Conflicts);
        }

        // A live nested value whose REPRESENTABLE members all returned to their type default is "at
        // default" for the purposes of the C2 removal rule even when an unrelated member with no
        // compiling emission form still differs from ITS default -- the setter must be removed, not
        // patched to a senseless empty initializer.
        [Fact]
        public void Reconcile_RepresentableMembersReturnToDefaultDespiteResidualUnrepresentableMember_RemovesFieldRatherThanPatchingEmptyInitializer()
        {
            const string source = @"
public class NestedEmitScene6 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Component<UnityEngine.UI.Button>(b => b.Set(""m_Colors"", new UnityEngine.UI.ColorBlock { a = 1f }));
    }
}
";
            var (parsed, map, componentLogicalId) = ParseWithMappedRootAndComponent(source, "UnityEngine.UI.Button");
            var @default = Node("UnityEngine.UI.ColorBlock", ("a", F(0)), ("target", new ValueNode.ObjectRef(null)));

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef("UnityEngine.UI.Button"),
                                // "a" returned to its default; the unrelated "target" reference the
                                // user never authored still differs from its own default.
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>(
                                        "m_Colors", Node("UnityEngine.UI.ColorBlock", ("a", F(0)), ("target", new ValueNode.ObjectRef("some-object")))),
                                }),
                            },
                        },
                    },
                },
                ComponentDefaults = ButtonTemplate(@default),
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
            var removal = Assert.Single(result.Patch.Edits.OfType<RemoveComponentField>());
            Assert.Equal(componentLogicalId, removal.Anchor);
            Assert.Equal("m_Colors", removal.FieldKey);
        }

        // A source struct whose members are authored in a DIFFERENT order from Unity's serialized
        // order is the same value: no patch, and the user's authored order is left alone.
        [Fact]
        public void Reconcile_SourceMembersAuthoredInADifferentOrderThanLive_EmitsNoEdit()
        {
            const string source = @"
public class NestedEmitScene7 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Component<UnityEngine.UI.Button>(b => b.Set(""m_Colors"", new UnityEngine.UI.ColorBlock { b = 2f, a = 1f }));
    }
}
";
            var (parsed, map, _) = ParseWithMappedRootAndComponent(source, "UnityEngine.UI.Button");
            var @default = Node("UnityEngine.UI.ColorBlock", ("a", F(0)), ("b", F(0)), ("c", F(0)));

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef("UnityEngine.UI.Button"),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>(
                                        "m_Colors", Node("UnityEngine.UI.ColorBlock", ("a", F(1)), ("b", F(2)), ("c", F(0)))),
                                }),
                            },
                        },
                    },
                },
                ComponentDefaults = ButtonTemplate(@default),
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Empty(result.Patch.Edits.OfType<RemoveComponentField>());
        }

        // The silent-discard guard: a live nested value whose every DIFFERING member has no
        // compiling emission form produces no edit at any site -- so without a report the user's
        // scene value simply vanishes, with no trace. The excluded member is a provable
        // non-default ObjectRef, an instance fact, so it surfaces as a standing note (never a
        // per-pass conflict) naming the object, the field and the members responsible.
        [Fact]
        public void Reconcile_IntroduceOfNestedFieldWhoseEveryDifferingMemberIsUnrepresentable_ReportsInstanceScopedNote()
        {
            const string source = @"
public class NestedEmitScene8 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Component<UnityEngine.UI.Button>();
    }
}
";
            var (parsed, map, componentLogicalId) = ParseWithMappedRootAndComponent(source, "UnityEngine.UI.Button");
            var @default = Node("UnityEngine.UI.ColorBlock", ("a", F(0)), ("target", new ValueNode.ObjectRef(null)));

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef("UnityEngine.UI.Button"),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>(
                                        "m_Colors", Node("UnityEngine.UI.ColorBlock", ("a", F(0)), ("target", new ValueNode.ObjectRef("some-object")))),
                                }),
                            },
                        },
                    },
                },
                ComponentDefaults = ButtonTemplate(@default),
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            Assert.Empty(result.Patch.Edits.OfType<IntroduceComponentField>());
            Assert.Empty(result.Conflicts);
            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);
            Assert.Equal(componentLogicalId, note.LogicalId);
            Assert.Contains("m_Colors", note.Reason);
            Assert.Contains("target", note.Reason);
            Assert.NotNull(note.RecurrenceKey);
            Assert.Contains(componentLogicalId, note.RecurrenceKey!);
        }

        // ---- Differ.Diff (code->scene) integration ---------------------------------------------

        private static ComponentData DesiredButton(ValueNode colors) => new()
        {
            LogicalId = "root-1/UnityEngine.UI.Button#0",
            Type = new TypeRef("UnityEngine.UI.Button"),
            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Colors", colors) }),
        };

        private static SceneModel ModelWithButton(ValueNode colors) => new()
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new GameObjectNode
                {
                    LogicalId = "root-1",
                    Name = "Root",
                    Components = new[] { DesiredButton(colors) },
                },
            },
        };

        private static IdentityMap RootMap() => new()
        {
            Entries = new[] { new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" } },
        };

        [Fact]
        public void Diff_PartialDesiredNestedEqualsActualAfterReduce_EmitsNoSetField()
        {
            var @default = Node("UnityEngine.UI.ColorBlock", ("a", F(0)), ("b", F(0)), ("c", F(0)));
            var model = ModelWithButton(Node("UnityEngine.UI.ColorBlock", ("a", F(1))));

            var actualButton = new ComponentData
            {
                Type = new TypeRef("UnityEngine.UI.Button"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>(
                        "m_Colors", Node("UnityEngine.UI.ColorBlock", ("a", F(1)), ("b", F(0)), ("c", F(0)))),
                }),
            };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualButton } } },
                ComponentDefaults = ButtonTemplate(@default),
            };

            var changeSet = Differ.Diff(model, snapshot, RootMap());

            Assert.Empty(changeSet.Ops.OfType<SetField>());
        }

        [Fact]
        public void Diff_PartialDesiredNestedDiffers_SetFieldValueIsTheCompletedNode()
        {
            var @default = Node("UnityEngine.UI.ColorBlock", ("a", F(0)), ("b", F(0)), ("c", F(0)));
            var model = ModelWithButton(Node("UnityEngine.UI.ColorBlock", ("a", F(1))));

            var actualButton = new ComponentData
            {
                Type = new TypeRef("UnityEngine.UI.Button"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>(
                        "m_Colors", Node("UnityEngine.UI.ColorBlock", ("a", F(2)), ("b", F(0)), ("c", F(0)))),
                }),
            };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualButton } } },
                ComponentDefaults = ButtonTemplate(@default),
            };

            var changeSet = Differ.Diff(model, snapshot, RootMap());

            var setField = Assert.Single(changeSet.Ops.OfType<SetField>());
            Assert.Equal("m_Colors", setField.Path);
            Assert.Equal(Node("UnityEngine.UI.ColorBlock", ("a", F(1)), ("b", F(0)), ("c", F(0))), setField.Value);
        }

        // The code->scene mirror of the fixed-point rule: a desired value already matching the
        // live value's representable members must not emit a SetField just because the live
        // component also carries a non-default reference no initializer can spell.
        [Fact]
        public void Diff_ActualHasNonDefaultUnrepresentableMemberMatchingDesiredProjection_EmitsNoSetField()
        {
            var @default = Node("UnityEngine.UI.ColorBlock", ("a", F(0)), ("target", new ValueNode.ObjectRef(null)));
            var model = ModelWithButton(Node("UnityEngine.UI.ColorBlock", ("a", F(1))));

            var actualButton = new ComponentData
            {
                Type = new TypeRef("UnityEngine.UI.Button"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>(
                        "m_Colors", Node("UnityEngine.UI.ColorBlock", ("a", F(1)), ("target", new ValueNode.ObjectRef("some-object")))),
                }),
            };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualButton } } },
                ComponentDefaults = ButtonTemplate(@default),
            };

            var changeSet = Differ.Diff(model, snapshot, RootMap());

            Assert.Empty(changeSet.Ops.OfType<SetField>());
        }

        // The code->scene mirror of Reconcile_SourceMembersAuthoredInADifferentOrderThanLive_EmitsNoEdit:
        // a desired struct whose members are authored in a different order than the live value is the
        // same value, so a settled scene must not receive a SetField over member order alone.
        [Fact]
        public void Diff_DesiredNestedMembersAuthoredInADifferentOrderThanActual_EmitsNoSetField()
        {
            var @default = Node(
                "UnityEngine.UI.ColorBlock",
                ("m_NormalColor", F(0)), ("m_ColorMultiplier", F(1)), ("m_FadeDuration", F(0)));
            var model = ModelWithButton(Node(
                "UnityEngine.UI.ColorBlock",
                ("m_ColorMultiplier", F(2)), ("m_NormalColor", F(9))));

            var actualButton = new ComponentData
            {
                Type = new TypeRef("UnityEngine.UI.Button"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>(
                        "m_Colors", Node(
                            "UnityEngine.UI.ColorBlock",
                            ("m_NormalColor", F(9)), ("m_ColorMultiplier", F(2)), ("m_FadeDuration", F(0)))),
                }),
            };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualButton } } },
                ComponentDefaults = ButtonTemplate(@default),
            };

            var changeSet = Differ.Diff(model, snapshot, RootMap());

            Assert.Empty(changeSet.Ops.OfType<SetField>());
        }
    }
}
