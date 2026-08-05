using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // "An ObjectRef is reached at any depth" — the descent mechanism
    // (ValueWalk.Enumerate + ObjectRefValues) and the sites that route through it: a
    // delete-cascade must not strip a declaration a surviving list/nested ObjectRef still names,
    // snapshot classification must resolve/pend/dangle a list the same way it does a bare
    // ObjectRef, a newly-appended component must pre-render a list field instead of leaving it
    // for the applier's context-free formatter to throw on, and a vanished list element must
    // report a located conflict rather than silently patch to NodeHandle.None.
    public class ObjectRefDescentTests
    {
        private const string DoorOpenerType = "Game.DoorOpener";
        private const string FieldKey = "targets";

        private static Dictionary<string, IReadOnlyDictionary<string, SourceSpan>> FieldSpans(string componentLogicalId, string fieldKey) =>
            new()
            {
                [componentLogicalId] = new Dictionary<string, SourceSpan> { [fieldKey] = new SourceSpan(0, 10) },
            };

        // ---- ValueWalk.Enumerate: path spellings ----

        [Fact]
        public void Enumerate_LeafOnlyValue_YieldsOnlyRootAtEmptyPath()
        {
            var leaf = ValueNode.Primitive.Int(5);

            var result = ValueWalk.Enumerate(leaf).ToList();

            var entry = Assert.Single(result);
            Assert.Equal("", entry.Path);
            Assert.Equal(leaf, entry.Node);
        }

        [Fact]
        public void Enumerate_List_YieldsRootThenEachItemIndexed()
        {
            var item0 = ValueNode.Primitive.Int(1);
            var item1 = ValueNode.Primitive.Int(2);
            var list = new ValueNode.List(new ValueNode[] { item0, item1 });

            var result = ValueWalk.Enumerate(list).ToList();

            Assert.Contains(result, e => e.Path == "" && Equals(e.Node, (ValueNode)list));
            Assert.Contains(result, e => e.Path == "[0]" && Equals(e.Node, item0));
            Assert.Contains(result, e => e.Path == "[1]" && Equals(e.Node, item1));
        }

        [Fact]
        public void Enumerate_Nested_YieldsRootThenDottedMember()
        {
            var inner = ValueNode.Primitive.Int(7);
            var nested = new ValueNode.Nested(
                "Game.Struct", new FieldMap(new[] { new KeyValuePair<string, ValueNode>("inner", inner) }));

            var result = ValueWalk.Enumerate(nested).ToList();

            Assert.Contains(result, e => e.Path == "" && Equals(e.Node, (ValueNode)nested));
            Assert.Contains(result, e => e.Path == ".inner" && Equals(e.Node, inner));
        }

        [Fact]
        public void Enumerate_ListOfNested_CombinesIndexAndMemberPath()
        {
            var inner = ValueNode.Primitive.Int(9);
            var nested = new ValueNode.Nested(
                "Game.Struct", new FieldMap(new[] { new KeyValuePair<string, ValueNode>("inner", inner) }));
            var list = new ValueNode.List(new ValueNode[] { nested });

            var result = ValueWalk.Enumerate(list).ToList();

            Assert.Contains(result, e => e.Path == "[0].inner" && Equals(e.Node, inner));
        }

        // ---- ObjectRefValues: Contains / Targets / Substitute / VanishedTargets ----

        [Fact]
        public void Contains_RefFreeValue_IsFalse()
        {
            Assert.False(ObjectRefValues.Contains(ValueNode.Primitive.Int(3)));
        }

        [Fact]
        public void Contains_RefInsideList_IsTrue()
        {
            var list = new ValueNode.List(new ValueNode[] { ValueNode.Primitive.Int(1), new ValueNode.ObjectRef("door-1") });

            Assert.True(ObjectRefValues.Contains(list));
        }

        [Fact]
        public void Targets_YieldsListAndNestedTargets_PreOrder()
        {
            var nested = new ValueNode.Nested(
                "Game.Struct",
                new FieldMap(new[] { new KeyValuePair<string, ValueNode>("inner", new ValueNode.ObjectRef("other-1")) }));
            var list = new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef("door-1"), nested });

            var targets = ObjectRefValues.Targets(list).ToList();

            Assert.Equal(new[] { "door-1", "other-1" }, targets);
        }

        [Fact]
        public void Substitute_ReplacesEveryRef_AtRootListAndNestedMember_LeavesOtherNodesIdentical()
        {
            var nested = new ValueNode.Nested(
                "Game.Struct", new FieldMap(new[] { new KeyValuePair<string, ValueNode>("inner", new ValueNode.ObjectRef("b")) }));
            var list = new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef("a"), ValueNode.Primitive.Int(1), nested });

            var result = ObjectRefValues.Substitute(list, r => new ValueNode.Unsupported(r.TargetLogicalId ?? "none"));

            var expected = new ValueNode.List(new ValueNode[]
            {
                new ValueNode.Unsupported("a"),
                ValueNode.Primitive.Int(1),
                new ValueNode.Nested(
                    "Game.Struct", new FieldMap(new[] { new KeyValuePair<string, ValueNode>("inner", new ValueNode.Unsupported("b")) })),
            });

            Assert.Equal(expected, result);
        }

        [Fact]
        public void VanishedTargets_BarePair_SourceTargetGoneFromLiveScene_Yields()
        {
            var vanished = ObjectRefValues.VanishedTargets(
                new ValueNode.ObjectRef("door-1"), new ValueNode.ObjectRef(null), new HashSet<string>()).ToList();

            Assert.Equal(new[] { "door-1" }, vanished);
        }

        [Fact]
        public void VanishedTargets_ListPair_OnlyTheVanishedIndexYields()
        {
            var source = new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef("door-1"), new ValueNode.ObjectRef("other-1") });
            var snapshot = new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(null), new ValueNode.ObjectRef("other-1") });

            var vanished = ObjectRefValues.VanishedTargets(source, snapshot, new HashSet<string>()).ToList();

            Assert.Equal(new[] { "door-1" }, vanished);
        }

        [Fact]
        public void VanishedTargets_SnapshotStillNonNull_NoYield()
        {
            Assert.Empty(ObjectRefValues.VanishedTargets(
                new ValueNode.ObjectRef("door-1"), new ValueNode.ObjectRef("other-1"), new HashSet<string>()));
        }

        [Fact]
        public void VanishedTargets_TargetStillLive_NoYield()
        {
            Assert.Empty(ObjectRefValues.VanishedTargets(
                new ValueNode.ObjectRef("door-1"), new ValueNode.ObjectRef(null), new HashSet<string> { "door-1" }));
        }

        // ---- Gate 1: delete-cascade must consult an ObjectRef reachable at any depth ----

        [Fact]
        public void Gate1_ListObjectRefField_KeepsTargetDeclarationAlive_AcrossDeleteCascade()
        {
            const string ownerLogicalId = "owner-1";
            var componentLogicalId = $"{ownerLogicalId}/{DoorOpenerType}#0";
            var fieldValue = new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef("target-1") });

            var target = new GameObjectNode { LogicalId = "target-1", Name = "Target" };
            var owner = new GameObjectNode
            {
                LogicalId = ownerLogicalId,
                Name = "Owner",
                Components = new[]
                {
                    new ComponentData { LogicalId = componentLogicalId, Type = new TypeRef(DoorOpenerType),
                        Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, fieldValue) }) },
                },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { target, owner } };

            // target-1 is deleted from the scene: mapped, but absent from the snapshot roots below.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-owner", Name = "Owner",
                        Components = new[] { new ComponentData { LogicalId = "unused", Type = new TypeRef(DoorOpenerType),
                            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, fieldValue) }) } },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "target-1", GlobalObjectId = "goid-target", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-owner", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.DoesNotContain(result.Patch.Edits, e => e is RemoveStatement rs && rs.Anchor == "target-1");
            Assert.DoesNotContain("target-1", result.RemovedLogicalIds);
        }

        [Fact]
        public void Gate1_NestedObjectRefMember_KeepsTargetDeclarationAlive_AcrossDeleteCascade()
        {
            const string ownerLogicalId = "owner-1";
            var componentLogicalId = $"{ownerLogicalId}/{DoorOpenerType}#0";
            var fieldValue = new ValueNode.Nested(
                "Game.Struct", new FieldMap(new[] { new KeyValuePair<string, ValueNode>("inner", new ValueNode.ObjectRef("target-1")) }));

            var target = new GameObjectNode { LogicalId = "target-1", Name = "Target" };
            var owner = new GameObjectNode
            {
                LogicalId = ownerLogicalId,
                Name = "Owner",
                Components = new[]
                {
                    new ComponentData { LogicalId = componentLogicalId, Type = new TypeRef(DoorOpenerType),
                        Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, fieldValue) }) },
                },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { target, owner } };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-owner", Name = "Owner",
                        Components = new[] { new ComponentData { LogicalId = "unused", Type = new TypeRef(DoorOpenerType),
                            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, fieldValue) }) } },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "target-1", GlobalObjectId = "goid-target", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-owner", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.DoesNotContain(result.Patch.Edits, e => e is RemoveStatement rs && rs.Anchor == "target-1");
            Assert.DoesNotContain("target-1", result.RemovedLogicalIds);
        }

        // Gate 1b: a PrefabInstanceNode's own Overrides/AddedComponents are not GameObjectNode
        // Components, so they need their own inclusion in the same delete-cascade scan.
        [Fact]
        public void Gate1b_PrefabInstanceOverrideAndAddedComponentReferences_KeepTargetDeclarationsAlive()
        {
            const string PrefabGuid = "guid-enemy-prefab";
            var target1 = new GameObjectNode { LogicalId = "target-1", Name = "Target1" };
            var target2 = new GameObjectNode { LogicalId = "target-2", Name = "Target2" };

            var refOverride = new PropertyOverride
            {
                Target = new OverrideTarget { ComponentType = DoorOpenerType },
                PropertyPath = "target",
                ObjectReference = new ValueNode.ObjectRef("target-1"),
            };
            var addedComponent = new AddedComponent
            {
                Target = new OverrideTarget { ComponentType = "Game.KeyHolder" },
                Component = new ComponentData
                {
                    LogicalId = "instance-1/Game.KeyHolder#0",
                    Type = new TypeRef("Game.KeyHolder"),
                    Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("key", new ValueNode.ObjectRef("target-2")) }),
                },
            };

            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = "Assets/Prefabs/Enemy.prefab" },
                Overrides = new[] { refOverride },
                AddedComponents = new[] { addedComponent },
            };

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { target1, target2, instance } };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance-1",
                Name = "Enemy",
                SourcePrefabGuid = PrefabGuid,
                // Identical to the model side -> the instance-override diff itself is a no-op;
                // only the delete-cascade's target-liveness scan is under test here.
                Overrides = new[] { refOverride },
                AddedComponents = new[] { addedComponent },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "target-1", GlobalObjectId = "goid-target-1", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "target-2", GlobalObjectId = "goid-target-2", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = "instance-1", GlobalObjectId = "goid-instance-1", Kind = "PrefabInstance",
                        SourcePrefabGuid = PrefabGuid,
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.DoesNotContain(result.Patch.Edits, e => e is RemoveStatement rs && rs.Anchor == "target-1");
            Assert.DoesNotContain("target-1", result.RemovedLogicalIds);
            Assert.DoesNotContain(result.Patch.Edits, e => e is RemoveStatement rs && rs.Anchor == "target-2");
            Assert.DoesNotContain("target-2", result.RemovedLogicalIds);
        }

        // ---- Gate 2: a snapshot list classifies Resolvable/Pending/Dangling the same way a bare
        // ObjectRef does, instead of falling through to the applier's ObjectRef-less formatter. ----

        [Fact]
        public void Gate2_ListWithUnresolvableTarget_ReportsDanglingReference_NoPatch_NoThrow()
        {
            const string ownerLogicalId = "root-1";
            var componentLogicalId = $"{ownerLogicalId}/{DoorOpenerType}#0";

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = ownerLogicalId, Name = "Root",
                        Components = new[] { new ComponentData { LogicalId = componentLogicalId, Type = new TypeRef(DoorOpenerType),
                            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(null) })) }) } },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" } },
            };

            // "ghost-1" is neither a live scene object, nor in the parsed source model, nor a
            // known handle -- unresolvable by construction.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root",
                        Components = new[] { new ComponentData { LogicalId = "unused", Type = new TypeRef(DoorOpenerType),
                            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef("ghost-1") })) }) } } },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId, FieldKey));

            var conflict = Assert.Single(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Contains("ghost-1", conflict.Reason);
            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
        }

        [Fact]
        public void Gate2_ListWithSameBatchCreateCandidateTarget_DefersNotDangling_NoThrow()
        {
            const string ownerLogicalId = "root-1";
            var componentLogicalId = $"{ownerLogicalId}/{DoorOpenerType}#0";
            const string newTargetGoid = "goid-newtarget";

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = ownerLogicalId, Name = "Root",
                        Components = new[] { new ComponentData { LogicalId = componentLogicalId, Type = new TypeRef(DoorOpenerType),
                            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(null) })) }) } },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" } },
            };

            // newTargetGoid is a genuinely-new, same-batch object: present in the snapshot, but
            // with no IdentityMap entry yet -- resolves on the guaranteed second Sync.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root",
                        Components = new[] { new ComponentData { LogicalId = "unused", Type = new TypeRef(DoorOpenerType),
                            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(newTargetGoid) })) }) } } },
                    new SnapshotNode { GlobalObjectId = newTargetGoid, Name = "NewTarget" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId, FieldKey));

            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
        }

        // ---- Gate 3: a newly-appended component's list field pre-renders instead of throwing. ----

        private const string OwnerAndDoorScene = @"
public class OwnerAndDoorScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
    }
}
";

        [Fact]
        public void Gate3_NewComponent_ListObjectRefField_PreRendersHandleArray_NoThrow()
        {
            var parsed = BuilderParser.Parse(OwnerAndDoorScene);
            var doorLogicalId = Assert.Single(parsed.Model.Roots).LogicalId;

            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = doorLogicalId, GlobalObjectId = "goid-door", Kind = "GameObject" } },
            };

            var openerFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    FieldKey, new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLogicalId), new ValueNode.ObjectRef(null) })),
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-door", Name = "Door" },
                    new SnapshotNode { GlobalObjectId = "goid-opener", Name = "Opener",
                        Components = new[] { new ComponentData { Type = new TypeRef(DoorOpenerType), Fields = openerFields } } },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, handles: parsed.Handles);

            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(OwnerAndDoorScene, result.Patch, parsed.Anchors);

            Assert.Contains(".Set(\"targets\", new[] { door, NodeHandle.None })", patched);
        }

        // ---- Gate 4: a list element whose target vanished from the scene must report a located
        // conflict for THAT element, never silently patch the whole array with NodeHandle.None. ----

        [Fact]
        public void Gate4_ListElementTargetDeletedFromScene_ReportsDanglingReference_NeverSilentPatch()
        {
            const string ownerLogicalId = "root-1";
            var componentLogicalId = $"{ownerLogicalId}/{DoorOpenerType}#0";

            var doorRoot = new GameObjectNode { LogicalId = "door-1", Name = "Door" };
            var otherRoot = new GameObjectNode { LogicalId = "other-1", Name = "Other" };
            var owner = new GameObjectNode
            {
                LogicalId = ownerLogicalId, Name = "Root",
                Components = new[]
                {
                    new ComponentData
                    {
                        LogicalId = componentLogicalId, Type = new TypeRef(DoorOpenerType),
                        Fields = new FieldMap(new[]
                        {
                            new KeyValuePair<string, ValueNode>(
                                FieldKey, new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef("door-1"), new ValueNode.ObjectRef("other-1") })),
                        }),
                    },
                },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { doorRoot, otherRoot, owner } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" },
                    // door-1 is mapped but absent from the snapshot below -- deleted from the scene.
                    new IdentityMapEntry { LogicalId = "door-1", GlobalObjectId = "goid-door", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "other-1", GlobalObjectId = "goid-other", Kind = "GameObject" },
                },
            };

            // Unity nulls the field slot whose target it deleted; the surviving element is
            // unchanged.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root",
                        Components = new[] { new ComponentData { LogicalId = "unused", Type = new TypeRef(DoorOpenerType),
                            Fields = new FieldMap(new[]
                            {
                                new KeyValuePair<string, ValueNode>(
                                    FieldKey, new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(null), new ValueNode.ObjectRef("other-1") })),
                            }) } } },
                    new SnapshotNode { GlobalObjectId = "goid-other", Name = "Other" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId, FieldKey));

            var conflict = Assert.Single(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Contains("door-1", conflict.Reason);
            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
        }
    }
}
