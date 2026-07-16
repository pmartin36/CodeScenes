using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Bucket b2-t1: whole-component set diff (add / remove / reorder) on MAPPED owners.
    // Spec tests 10, 11, 11b + §13-adjacent 2nd-Sync convergence guard.
    public class ComponentReconcileTests
    {
        private static IdentityMap MapWithRoot() => new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
            },
        };

        [Fact]
        public void Reconcile_AddedComponentInScene_AppendsComponentStatementAndAddsEntry()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "root-1", Name = "Root" } },
            };

            var fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)) });
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
                            new ComponentData { LogicalId = "unused", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = fields },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, MapWithRoot());

            var append = Assert.Single(result.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.Equal("root-1", append.Anchor);
            Assert.Equal("root-1/UnityEngine.Rigidbody#0", append.ComponentLogicalId);
            Assert.Equal("UnityEngine.Rigidbody", append.TypeFullName);
            Assert.Equal(fields, append.Fields);

            var addedEntry = Assert.Single(result.AddedEntries);
            Assert.Equal("root-1/UnityEngine.Rigidbody#0", addedEntry.LogicalId);
            Assert.Equal("Component", addedEntry.Kind);
            Assert.Equal("UnityEngine.Rigidbody", addedEntry.ComponentType);
            Assert.Equal("root-1", addedEntry.ParentLogicalId);
            Assert.Equal("", addedEntry.GlobalObjectId);
        }

        [Fact]
        public void Reconcile_RemovedComponentFromScene_EmitsRemoveStatementAndRemovedLogicalId()
        {
            var fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)) });
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "root-1",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "root-1/UnityEngine.Rigidbody#0", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = fields },
                        },
                    },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root" } },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = "root-1/UnityEngine.Rigidbody#0",
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = "UnityEngine.Rigidbody",
                        ParentLogicalId = "root-1",
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var remove = Assert.Single(result.Patch.Edits.OfType<RemoveStatement>());
            Assert.Equal("root-1/UnityEngine.Rigidbody#0", remove.Anchor);
            Assert.Contains("root-1/UnityEngine.Rigidbody#0", result.RemovedLogicalIds);
        }

        [Fact]
        public void Reconcile_ComponentReorderedInScene_ReordersStatements()
        {
            var rbFields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)) });
            var bcFields = FieldMap.Empty;

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "root-1",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "root-1/UnityEngine.Rigidbody#0", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = rbFields },
                            new ComponentData { LogicalId = "root-1/UnityEngine.BoxCollider#0", Type = new TypeRef("UnityEngine.BoxCollider"), Fields = bcFields },
                        },
                    },
                },
            };

            // Scene order is the reverse: BoxCollider then Rigidbody.
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
                            new ComponentData { LogicalId = "unused-bc", Type = new TypeRef("UnityEngine.BoxCollider"), Fields = bcFields },
                            new ComponentData { LogicalId = "unused-rb", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = rbFields },
                        },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = "root-1/UnityEngine.Rigidbody#0",
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = "UnityEngine.Rigidbody",
                        ParentLogicalId = "root-1",
                    },
                    new IdentityMapEntry
                    {
                        LogicalId = "root-1/UnityEngine.BoxCollider#0",
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = "UnityEngine.BoxCollider",
                        ParentLogicalId = "root-1",
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.Empty(result.Patch.Edits.OfType<RemoveStatement>());

            var reorders = result.Patch.Edits.OfType<ReorderStatement>().ToArray();
            Assert.Equal(2, reorders.Length);
            Assert.Contains(reorders, r => r.Anchor == "root-1/UnityEngine.BoxCollider#0" && r.NewSiblingIndex == 0);
            Assert.Contains(reorders, r => r.Anchor == "root-1/UnityEngine.Rigidbody#0" && r.NewSiblingIndex == 1);
        }

        [Fact]
        public void Reconcile_AddedComponentThenReSync_ConvergesNoFurtherAppend()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "root-1", Name = "Root" } },
            };

            var fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)) });
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
                            new ComponentData { LogicalId = "unused", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = fields },
                        },
                    },
                },
            };

            var map = MapWithRoot();

            var firstResult = Reconciler.Reconcile(model, snapshot, map);
            var addedEntry = Assert.Single(firstResult.AddedEntries);

            var secondMap = new IdentityMap { Entries = map.Entries.Append(addedEntry).ToArray() };
            var secondResult = Reconciler.Reconcile(model, snapshot, secondMap);

            Assert.Empty(secondResult.Patch.Edits.OfType<AppendComponentStatement>());
        }

        // ---- b2-t2: field-value diff on matched components (patch / introduce / Unsupported-skip) ----

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

        // spec test 9: m_Mass 5f -> 8f, a single PatchComponentField over only the value span.
        [Fact]
        public void Reconcile_ComponentFieldValueChanged_EmitsSinglePatchComponentField()
        {
            const string source = @"
public class Scene1 : ISceneDefinition
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
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(8f)) }),
                            },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal(componentLogicalId, patch.Anchor);
            Assert.Equal(parsed.FieldArgumentSpans[componentLogicalId]["m_Mass"], patch.ValueSpan);
            Assert.Equal(SourceExpr.ValueNodeLiteral(ValueNode.Primitive.Float(8f)), patch.NewExpr);

            Assert.Empty(result.Patch.Edits.OfType<IntroduceComponentField>());
            Assert.Empty(result.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.Empty(result.Patch.Edits.OfType<RemoveStatement>());
            Assert.Empty(result.Skipped);
        }

        // spec test 12: a [SerializeField] private key like `_health` is preserved verbatim
        // through parse -> diff -> patch (no m_/accessibility mangling).
        [Fact]
        public void Reconcile_PrivateSerializeFieldKeyPreserved_PatchesWithoutMangling()
        {
            const string source = @"
public class Scene2 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Component<Game.Health>(h => h.Set(""_health"", 100));
    }
}
";
            var (parsed, map) = ParseWithMappedRootAndComponent(source, "Game.Health");
            var componentLogicalId = "root/Game.Health#0";

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
                                Type = new TypeRef("Game.Health"),
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("_health", ValueNode.Primitive.Int(150)) }),
                            },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal(componentLogicalId, patch.Anchor);
            Assert.Equal(parsed.FieldArgumentSpans[componentLogicalId]["_health"], patch.ValueSpan);
            Assert.Equal(SourceExpr.ValueNodeLiteral(ValueNode.Primitive.Int(150)), patch.NewExpr);
        }

        // spec test 13 (Reconcile half): an Unsupported snapshot field value emits NO patch/introduce
        // (source token untouched) and is flagged once in ReconcileResult.Skipped.
        [Fact]
        public void Reconcile_UnsupportedSnapshotField_EmitsNoEditAndFlagsSkipped()
        {
            const string source = @"
public class Scene3 : ISceneDefinition
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
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)),
                                    new KeyValuePair<string, ValueNode>("m_CenterOfMass", new ValueNode.Unsupported("SomeUnrepresentableToken()")),
                                }),
                            },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Empty(result.Patch.Edits.OfType<IntroduceComponentField>());

            var skipped = Assert.Single(result.Skipped);
            Assert.Equal(componentLogicalId, skipped.LogicalId);
            Assert.Equal("m_CenterOfMass", skipped.Path);
            Assert.Equal("Unsupported", skipped.Reason);
        }

        // b1-t2: no-regression guard — a matched component (source has Component<UnityEngine.Rigidbody>,
        // snapshot FullName + field values equal) is left byte-identical: no patch, no append, no
        // remove. Pins the anti-churn invariant b2's normalization chokepoint relies on: once a short
        // authored name is rewritten to this qualified form, a matched component must never be re-emitted.
        [Fact]
        public void Reconcile_MatchedComponent_LeavesStatementTextByteIdentical()
        {
            const string source = @"
public class Scene6 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Component<UnityEngine.Rigidbody>(rb => rb.Set(""m_Mass"", 5f));
    }
}
";
            var (parsed, map) = ParseWithMappedRootAndComponent(source, "UnityEngine.Rigidbody");

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
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)) }),
                            },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            Assert.Empty(result.Patch.Edits);
            Assert.Empty(result.Skipped);
        }

        // A snapshot-only field key (present in scene, absent from source) is newly-detected ->
        // IntroduceComponentField carrying the snapshot value, not a PatchComponentField.
        [Fact]
        public void Reconcile_NewlyDetectedField_EmitsIntroduceComponentField()
        {
            const string source = @"
public class Scene4 : ISceneDefinition
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
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)),
                                    new KeyValuePair<string, ValueNode>("m_Drag", ValueNode.Primitive.Float(0.5f)),
                                }),
                            },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            var introduce = Assert.Single(result.Patch.Edits.OfType<IntroduceComponentField>());
            Assert.Equal(componentLogicalId, introduce.Anchor);
            Assert.Equal("m_Drag", introduce.FieldKey);
            Assert.Equal(ValueNode.Primitive.Float(0.5f), introduce.Value);

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Empty(result.Skipped);
        }

        // ---- b2-t3: non-localizable component edit -> Conflict (spec line ~159) ----

        // Primary case: a managed Component entry is being removed (absent from the snapshot),
        // but the supplied componentAnchors does NOT contain its LogicalId (source drifted /
        // component no longer resolvable to a single statement) -> exactly one Conflict
        // (MissingSourceAnchor), zero RemoveStatement, and the id is NOT in RemovedLogicalIds.
        [Fact]
        public void Reconcile_ComponentRemoveWithNoSourceAnchor_SurfacesConflictNoRemove()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "root-1",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "root-1/UnityEngine.BoxCollider#0", Type = new TypeRef("UnityEngine.BoxCollider"), Fields = FieldMap.Empty },
                        },
                    },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root" } },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = "root-1/UnityEngine.BoxCollider#0",
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = "UnityEngine.BoxCollider",
                        ParentLogicalId = "root-1",
                    },
                },
            };

            // componentAnchors is non-null (localizable-patch mode) but deliberately omits the
            // BoxCollider component's LogicalId.
            var componentAnchors = new Dictionary<string, SourceSpan>();

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, componentAnchors, null);

            var conflict = Assert.Single(result.Conflicts);
            Assert.Equal(ConflictKind.MissingSourceAnchor, conflict.Kind);
            Assert.Equal("root-1/UnityEngine.BoxCollider#0", conflict.LogicalId);

            Assert.Empty(result.Patch.Edits.OfType<RemoveStatement>());
            Assert.DoesNotContain("root-1/UnityEngine.BoxCollider#0", result.RemovedLogicalIds);
        }

        // Plugs b2-t2's deferred span-absent hole: a field value differs, the component itself
        // has a resolvable anchor, but the specific value span is missing from fieldArgumentSpans
        // -> one Conflict, zero PatchComponentField.
        [Fact]
        public void Reconcile_ComponentFieldValueChangeWithNoValueSpan_SurfacesConflictNoPatch()
        {
            const string source = @"
public class Scene5 : ISceneDefinition
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
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(8f)) }),
                            },
                        },
                    },
                },
            };

            // fieldArgumentSpans is non-null but deliberately omits the m_Mass value span for
            // this component.
            var fieldArgumentSpans = new Dictionary<string, IReadOnlyDictionary<string, SourceSpan>>
            {
                [componentLogicalId] = new Dictionary<string, SourceSpan>(),
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, fieldArgumentSpans);

            var conflict = Assert.Single(result.Conflicts);
            Assert.Equal(ConflictKind.MissingSourceAnchor, conflict.Kind);
            Assert.Equal(componentLogicalId, conflict.LogicalId);

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
        }

        // Guards the null-dict legacy branch: with componentAnchors == null (editor did not
        // supply localizable-patch data at all), the same removed-component scenario still
        // emits a RemoveStatement and NO conflict — the gate must not fire in legacy mode.
        [Fact]
        public void Reconcile_NoAnchorsSupplied_ComponentRemoveStillEmitsNoConflict()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "root-1",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "root-1/UnityEngine.BoxCollider#0", Type = new TypeRef("UnityEngine.BoxCollider"), Fields = FieldMap.Empty },
                        },
                    },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root" } },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = "root-1/UnityEngine.BoxCollider#0",
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = "UnityEngine.BoxCollider",
                        ParentLogicalId = "root-1",
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Conflicts);
            var remove = Assert.Single(result.Patch.Edits.OfType<RemoveStatement>());
            Assert.Equal("root-1/UnityEngine.BoxCollider#0", remove.Anchor);
            Assert.Contains("root-1/UnityEngine.BoxCollider#0", result.RemovedLogicalIds);
        }

        // Spec §13/13b (create-with-payload). A newly editor-created GameObject that carries a
        // component must attach it in the SAME Reconcile pass as the owner append - never a
        // report-and-defer conflict, never silently dropped. Pass 1 emits owner append (with a
        // Handle) + component attach + both AddedEntries in one shot; pass 2 of the unchanged
        // scene (with pass 1's AddedEntries in the map and owner+component now in source) is a
        // no-op (converges).
        [Fact]
        public void Reconcile_ComponentOnNewlyCreatedObject_ConvergesInOnePass()
        {
            var rbFields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)) });

            // The editor-created object + its component, identical across every pass.
            SnapshotNode Created() => new SnapshotNode
            {
                GlobalObjectId = "goid-new",
                Name = "NewThing",
                Components = new[]
                {
                    new ComponentData { LogicalId = "unused", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = rbFields },
                },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { Created() } };

            // ---- Pass 1: object not in source or map. Owner append + component attach, together. ----
            var emptyModel = new SceneModel { SchemaVersion = 1, Roots = System.Array.Empty<GameObjectNode>() };
            var pass1 = Reconciler.Reconcile(emptyModel, snapshot, new IdentityMap { Entries = System.Array.Empty<IdentityMapEntry>() });

            var append = Assert.Single(pass1.Patch.Edits.OfType<AppendStatement>());
            Assert.NotNull(append.Handle);

            var attach = Assert.Single(pass1.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.Equal(append.NewLogicalId, attach.Anchor);
            Assert.Equal(append.Handle, attach.OwnerHandle);
            Assert.Equal("UnityEngine.Rigidbody", attach.TypeFullName);
            Assert.Equal(rbFields, attach.Fields);

            var objEntry = Assert.Single(pass1.AddedEntries, e => e.Kind == "GameObject" && e.GlobalObjectId == "goid-new");
            Assert.Equal(append.NewLogicalId, objEntry.LogicalId);

            var compEntry = Assert.Single(pass1.AddedEntries, e => e.Kind == "Component");
            Assert.Equal($"{append.NewLogicalId}/UnityEngine.Rigidbody#0", compEntry.LogicalId);
            Assert.Equal(append.NewLogicalId, compEntry.ParentLogicalId);

            // The retired report-only path never fires for a representable component.
            Assert.Empty(pass1.Conflicts);

            // ---- Pass 2: apply pass 1 -> owner + component now mapped and present in source. ----
            // Unchanged scene must converge: no further append, no re-attach.
            var newLogicalId = objEntry.LogicalId;
            var modelAfterPass1 = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = newLogicalId,
                        Name = "NewThing",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = compEntry.LogicalId, Type = new TypeRef("UnityEngine.Rigidbody"), Fields = rbFields },
                        },
                    },
                },
            };
            var mapAfterPass1 = new IdentityMap { Entries = pass1.AddedEntries };
            var pass2 = Reconciler.Reconcile(modelAfterPass1, snapshot, mapAfterPass1);

            Assert.Empty(pass2.Patch.Edits);
            Assert.Empty(pass2.Conflicts);
        }
    }
}
