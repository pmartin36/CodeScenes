using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // AppendInstanceAddChild.FieldExpressions is the added-child twin of
    // AppendInstanceAddComponent.FieldExpressions: a pre-rendered handle for a component field the
    // apply-time renderer cannot format context-free (an ObjectRef handle argument). Without it wired
    // through both the emit side (ReconcileAddedGameObjects) and the apply side
    // (SourcePatchApplier.Instances.RenderAddChildClosure), an added child's component carrying a
    // scene reference throws NotSupportedException at apply time instead of rendering the handle.
    public class AddedChildFieldExpressionsTests
    {
        private const string DoorOpenerType = "Game.DoorOpener";
        private const string PrefabGuid = "guid-enemy-prefab";

        private const string HandledInstanceFixture = @"
public class HandledInstanceScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
    }
}
";

        private const string HandledInstanceWithDoorFixture = @"
public class HandledInstanceWithDoorScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        var door = scene.Add(""Door"");
    }
}
";

        private const string HandledInstanceWithTwoDoorsFixture = @"
public class HandledInstanceWithTwoDoorsScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        var doorA = scene.Add(""DoorA"");
        var doorB = scene.Add(""DoorB"");
    }
}
";

        private static AppendInstanceAddChild BuildAddChildEdit(
            string anchor, ValueNode targetValue, IReadOnlyDictionary<string, string>? fieldExpressions) =>
            new()
            {
                Anchor = anchor,
                ParentPath = "",
                ParentSelectorExpr = "",
                Name = "MuzzleFlash",
                Node = new GameObjectNode
                {
                    Name = "MuzzleFlash",
                    Components = new[]
                    {
                        new ComponentData
                        {
                            Type = new TypeRef(DoorOpenerType),
                            Fields = new FieldMap(new[]
                            {
                                new KeyValuePair<string, ValueNode>("target", targetValue),
                            }),
                        },
                    },
                },
                // Single-component fixture: wrap the caller's flat map as the one-element
                // per-component slice AppendInstanceAddChild.FieldExpressions now expects.
                FieldExpressions = fieldExpressions == null
                    ? null
                    : new IReadOnlyDictionary<string, string>?[] { fieldExpressions },
            };

        // Defect 3 (crash-to-handle): an AppendInstanceAddChild whose component carries an ObjectRef
        // field AND whose FieldExpressions maps that field to a pre-rendered handle must render the
        // handle in the AddChild closure — never throw NotSupportedException.
        [Fact]
        public void Apply_AppendInstanceAddChildWithFieldExpressions_RendersHandle_NoThrow()
        {
            var edit = BuildAddChildEdit(
                anchor: "enemy",
                targetValue: new ValueNode.ObjectRef("goid-door"),
                fieldExpressions: new Dictionary<string, string> { ["target"] = "door" });

            var patch = new SourcePatch { Edits = new SourceEdit[] { edit } };
            var anchors = BuilderParser.Parse(HandledInstanceFixture).Anchors;

            var rendered = SourcePatchApplier.Apply(HandledInstanceFixture, patch, anchors);

            Assert.Contains("c.Set(\"target\", door)", rendered);
        }

        // Emit-side handle: a full reconcile over a snapshot-only added child whose component holds
        // an ObjectRef resolving to a mapped, source-anchored existing object must populate the
        // edit's FieldExpressions with the pre-rendered handle, and Apply must render it.
        [Fact]
        public void Reconcile_AddedChildWithResolvableObjectRef_PopulatesFieldExpressionsWithHandle()
        {
            var parsed = BuilderParser.Parse(HandledInstanceWithDoorFixture);
            var instanceLogicalId = parsed.Model.Roots.OfType<PrefabInstanceNode>().Single().LogicalId;
            var doorLogicalId = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;

            var added = new AddedGameObject
            {
                Parent = new OverrideTarget { ChildPath = "" },
                Node = new GameObjectNode
                {
                    Name = "MuzzleFlash",
                    Components = new[]
                    {
                        new ComponentData
                        {
                            Type = new TypeRef(DoorOpenerType),
                            Fields = new FieldMap(new[]
                            {
                                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(doorLogicalId)),
                            }),
                        },
                    },
                },
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-enemy",
                Name = "Enemy",
                SourcePrefabGuid = PrefabGuid,
                AddedGameObjects = new[] { added },
            };
            var doorSnapshot = new SnapshotNode { GlobalObjectId = "goid-door", Name = "Door" };

            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance, doorSnapshot } };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = instanceLogicalId, GlobalObjectId = "goid-enemy", Kind = "PrefabInstance",
                        SourcePrefabGuid = PrefabGuid,
                    },
                    new IdentityMapEntry { LogicalId = doorLogicalId, GlobalObjectId = "goid-door", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, handles: parsed.Handles);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddChild>());
            Assert.NotNull(append.FieldExpressions);
            Assert.Equal("door", append.FieldExpressions![0]!["target"]);

            var component = Assert.Single(append.Node.Components);
            Assert.Equal(new ValueNode.ObjectRef(doorLogicalId), component.Fields["target"]);

            var rendered = SourcePatchApplier.Apply(HandledInstanceWithDoorFixture, result.Patch, parsed.Anchors);
            Assert.Contains("c.Set(\"target\", door)", rendered);
        }

        // Same-batch pending target: an added child's ObjectRef pointing at a snapshot-only object
        // with no IdentityMap entry yet must omit the field silently (converges on the guaranteed
        // second sync), never report a conflict.
        [Fact]
        public void Reconcile_AddedChildWithPendingObjectRef_OmitsFieldSilently_NoConflict()
        {
            const string pendingGoid = "goid-newtarget";
            var parsed = BuilderParser.Parse(HandledInstanceFixture);
            var instanceLogicalId = parsed.Model.Roots.OfType<PrefabInstanceNode>().Single().LogicalId;

            var added = new AddedGameObject
            {
                Parent = new OverrideTarget { ChildPath = "" },
                Node = new GameObjectNode
                {
                    Name = "MuzzleFlash",
                    Components = new[]
                    {
                        new ComponentData
                        {
                            Type = new TypeRef(DoorOpenerType),
                            Fields = new FieldMap(new[]
                            {
                                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(pendingGoid)),
                            }),
                        },
                    },
                },
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-enemy",
                Name = "Enemy",
                SourcePrefabGuid = PrefabGuid,
                AddedGameObjects = new[] { added },
            };
            var pendingSnapshot = new SnapshotNode { GlobalObjectId = pendingGoid, Name = "NewTarget" };

            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance, pendingSnapshot } };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = instanceLogicalId, GlobalObjectId = "goid-enemy", Kind = "PrefabInstance",
                        SourcePrefabGuid = PrefabGuid,
                    },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, handles: parsed.Handles);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddChild>());
            var component = Assert.Single(append.Node.Components);
            Assert.False(component.Fields.TryGetValue("target", out _));
            Assert.Empty(result.Conflicts);
        }

        // Collision guard: an added child with TWO components that both carry a same-named
        // resolvable ObjectRef field ("target") must render each component's OWN handle. A shared
        // per-field-key map across components would let the second component's handle overwrite
        // the first's, so both closures would render the second component's target.
        [Fact]
        public void Reconcile_AddedChildWithTwoComponentsSameFieldName_RendersDistinctHandlesPerComponent()
        {
            var parsed = BuilderParser.Parse(HandledInstanceWithTwoDoorsFixture);
            var instanceLogicalId = parsed.Model.Roots.OfType<PrefabInstanceNode>().Single().LogicalId;
            var doorALogicalId = parsed.Model.Roots.Single(r => r.Name == "DoorA").LogicalId;
            var doorBLogicalId = parsed.Model.Roots.Single(r => r.Name == "DoorB").LogicalId;

            var added = new AddedGameObject
            {
                Parent = new OverrideTarget { ChildPath = "" },
                Node = new GameObjectNode
                {
                    Name = "MuzzleFlash",
                    Components = new[]
                    {
                        new ComponentData
                        {
                            Type = new TypeRef(DoorOpenerType),
                            Fields = new FieldMap(new[]
                            {
                                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(doorALogicalId)),
                            }),
                        },
                        new ComponentData
                        {
                            Type = new TypeRef(DoorOpenerType),
                            Fields = new FieldMap(new[]
                            {
                                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(doorBLogicalId)),
                            }),
                        },
                    },
                },
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-enemy",
                Name = "Enemy",
                SourcePrefabGuid = PrefabGuid,
                AddedGameObjects = new[] { added },
            };
            var doorASnapshot = new SnapshotNode { GlobalObjectId = "goid-doorA", Name = "DoorA" };
            var doorBSnapshot = new SnapshotNode { GlobalObjectId = "goid-doorB", Name = "DoorB" };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { snapshotInstance, doorASnapshot, doorBSnapshot },
            };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = instanceLogicalId, GlobalObjectId = "goid-enemy", Kind = "PrefabInstance",
                        SourcePrefabGuid = PrefabGuid,
                    },
                    new IdentityMapEntry { LogicalId = doorALogicalId, GlobalObjectId = "goid-doorA", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = doorBLogicalId, GlobalObjectId = "goid-doorB", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, handles: parsed.Handles);

            var rendered = SourcePatchApplier.Apply(HandledInstanceWithTwoDoorsFixture, result.Patch, parsed.Anchors);

            Assert.Contains("c.Set(\"target\", doorA)", rendered);
            Assert.Contains("c.Set(\"target\", doorB)", rendered);
        }
    }
}
