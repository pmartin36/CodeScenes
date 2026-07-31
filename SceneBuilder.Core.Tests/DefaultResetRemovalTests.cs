using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Scene->code half of the C2 contract. A source-authored field whose LIVE value has
    // returned to the type's constructed default must
    // REMOVE the `.Set(...)` call from source, never patch it to the default literal (today's
    // behavior). Mirrors ComponentReconcileTests' real-parse harness.
    public class DefaultResetRemovalTests
    {
        private static (ParseResult Parsed, IdentityMap Map) ParseWithMappedRootAndComponent(
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

            return (parsed, map);
        }

        private static ComponentData[] RigidbodyTemplate(ValueNode mass) => new[]
        {
            new ComponentData
            {
                LogicalId = "template",
                Type = new TypeRef("UnityEngine.Rigidbody"),
                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", mass) }),
            },
        };

        // A field present in BOTH source and snapshot, whose snapshot value
        // equals the type default (via ComponentDefaults), emits exactly one RemoveComponentField
        // and NO PatchComponentField -- never a patch to the default literal.
        [Fact]
        public void Reconcile_LiveValueReturnedToTypeDefault_EmitsRemovalNotPatch()
        {
            const string source = @"
public class DefaultResetScene1 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Component<UnityEngine.Rigidbody>(rb => rb.Set(""m_Mass"", 5f));
    }
}
";
            var (parsed, map) = ParseWithMappedRootAndComponent(source, "UnityEngine.Rigidbody");
            var componentLogicalId = "root/UnityEngine.Rigidbody#0";

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
                                Type = new TypeRef("UnityEngine.Rigidbody"),
                                // Live value regressed to the constructed default (1f).
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(1f)) }),
                            },
                        },
                    },
                },
                ComponentDefaults = RigidbodyTemplate(ValueNode.Primitive.Float(1f)),
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());

            var removal = Assert.Single(result.Patch.Edits.OfType<RemoveComponentField>());
            Assert.Equal(componentLogicalId, removal.Anchor);
            Assert.Equal("m_Mass", removal.FieldKey);
            Assert.Equal(parsed.FieldArgumentSpans[componentLogicalId]["m_Mass"], removal.ValueSpan);
        }

        // Closed-grammar guard: a FitSize/SurfaceSnap field returning to its
        // default has NO representable removal (the fluent call throws on an empty argument list),
        // so the reconciler must report a located conflict and emit NEITHER a PatchComponentField
        // NOR a RemoveComponentField -- never the "vertical: " empty-argument splice today's patch
        // path produces.
        [Fact]
        public void Reconcile_ClosedGrammarFieldReturnedToDefault_ReportsConflictAndEmitsNoEdit()
        {
            const string source = @"
public class DefaultResetScene2 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var crate = scene.Add(""Crate"");
        crate.SurfaceSnap(down: true);
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var crateNode = Assert.Single(parsed.Model.Roots);
            var component = Assert.Single(crateNode.Components);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = crateNode.LogicalId, GlobalObjectId = "goid-crate", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = component.LogicalId,
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = SpatialComponents.SurfaceSnapTypeName,
                        ParentLogicalId = crateNode.LogicalId,
                    },
                },
            };

            // Live axis reset to None -- the type default -- while source still authors "down".
            var noneEnum = new ValueNode.Enum(
                SpatialComponents.SurfaceSnapEnums.VerticalTypeName, new[] { SpatialComponents.SurfaceSnapEnums.None }, false);

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-crate",
                        Name = "Crate",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef(SpatialComponents.SurfaceSnapTypeName),
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(SpatialComponents.SurfaceSnapFields.Vertical, noneEnum) }),
                            },
                        },
                    },
                },
                ComponentDefaults = new[]
                {
                    new ComponentData
                    {
                        LogicalId = "template",
                        Type = new TypeRef(SpatialComponents.SurfaceSnapTypeName),
                        Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(SpatialComponents.SurfaceSnapFields.Vertical, noneEnum) }),
                    },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, parsed.FlagPresence, parsed.ComponentAnchors, parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Empty(result.Patch.Edits.OfType<RemoveComponentField>());
            Assert.Contains(result.Conflicts, c => c.LogicalId == component.LogicalId);
        }

        // ---- Applier: RemoveComponentField deletes the `.Set(...)` call -------------------------

        // Applying a RemoveComponentField deletes the whole `.Set(...)` call;
        // when it was the closure's sole setter the statement collapses to a bare `.Component<T>()`.
        [Fact]
        public void Apply_RemovesSoleSetCall_CollapsesToBareComponentCall()
        {
            const string source = @"
public class DefaultResetScene3 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Component<UnityEngine.Rigidbody>(rb => rb.Set(""m_Mass"", 5f));
    }
}
";
            var (parsed, _) = ParseWithMappedRootAndComponent(source, "UnityEngine.Rigidbody");
            var componentLogicalId = "root/UnityEngine.Rigidbody#0";

            var edit = new RemoveComponentField
            {
                Anchor = componentLogicalId,
                FieldKey = "m_Mass",
                ValueSpan = parsed.FieldArgumentSpans[componentLogicalId]["m_Mass"],
            };

            var patched = SourcePatchApplier.Apply(
                source, new SourcePatch { Edits = new SourceEdit[] { edit } }, SourcePatchTestHelpers.MergeAnchors(parsed));

            Assert.Contains(".Component<UnityEngine.Rigidbody>()", patched);
            Assert.DoesNotContain("(c =>", patched);
            Assert.DoesNotContain(".Set(", patched);
        }

        // Removing one of two `.Set(...)` calls in a block closure keeps
        // the other setter intact.
        [Fact]
        public void Apply_RemovesOneOfTwoSetCalls_KeepsTheOther()
        {
            const string source = @"
public class DefaultResetScene4 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Component<UnityEngine.Rigidbody>(rb =>
        {
            rb.Set(""m_Mass"", 5f);
            rb.Set(""m_Drag"", 0.5f);
        });
    }
}
";
            var (parsed, _) = ParseWithMappedRootAndComponent(source, "UnityEngine.Rigidbody");
            var componentLogicalId = "root/UnityEngine.Rigidbody#0";

            var edit = new RemoveComponentField
            {
                Anchor = componentLogicalId,
                FieldKey = "m_Mass",
                ValueSpan = parsed.FieldArgumentSpans[componentLogicalId]["m_Mass"],
            };

            var patched = SourcePatchApplier.Apply(
                source, new SourcePatch { Edits = new SourceEdit[] { edit } }, SourcePatchTestHelpers.MergeAnchors(parsed));

            Assert.DoesNotContain("m_Mass", patched);
            Assert.Contains(".Set(\"m_Drag\", 0.5f)", patched);
        }

        // A removal and an introduce on the SAME component in one batch
        // compose in EITHER applier order -- the shape must be decided at APPLY
        // time from the CURRENT root, never at resolve time against the original tree.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Apply_RemovalAndIntroduceOnSameComponent_ComposeInEitherOrder(bool removalFirst)
        {
            const string source = @"
public class DefaultResetScene5 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Component<UnityEngine.Rigidbody>(rb => rb.Set(""m_Mass"", 5f));
    }
}
";
            var (parsed, _) = ParseWithMappedRootAndComponent(source, "UnityEngine.Rigidbody");
            var componentLogicalId = "root/UnityEngine.Rigidbody#0";

            var removal = new RemoveComponentField
            {
                Anchor = componentLogicalId,
                FieldKey = "m_Mass",
                ValueSpan = parsed.FieldArgumentSpans[componentLogicalId]["m_Mass"],
            };
            var introduce = new IntroduceComponentField
            {
                Anchor = componentLogicalId,
                FieldKey = "m_Drag",
                Value = ValueNode.Primitive.Float(0.5f),
            };

            SourceEdit[] edits = removalFirst
                ? new SourceEdit[] { removal, introduce }
                : new SourceEdit[] { introduce, removal };

            var patched = SourcePatchApplier.Apply(
                source, new SourcePatch { Edits = edits }, SourcePatchTestHelpers.MergeAnchors(parsed));

            Assert.DoesNotContain("m_Mass", patched);
            Assert.Contains(".Set(\"m_Drag\", 0.5f)", patched);
        }
    }
}
