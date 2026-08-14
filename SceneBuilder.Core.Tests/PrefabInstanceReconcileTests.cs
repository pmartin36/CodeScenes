using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t1: scene->code append/remove of a prefab instance root (SnapshotNode.SourcePrefabGuid),
    // and the emitted `.Instance(path)` statement's round trip back through BuilderParser.
    public class PrefabInstanceReconcileTests
    {
        private const string PrefabGuid = "guid-enemy-prefab";
        private const string PrefabPath = "Assets/Prefabs/Enemy.prefab";

        [Fact]
        public void Reconcile_SnapshotOnlyInstanceAtRoot_AppendsWithPathFromAssetsCache_AndPrefabInstanceIdentityEntry()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = System.Array.Empty<GameObjectNode>() };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-instance-1",
                        Name = "Enemy",
                        SourcePrefabGuid = PrefabGuid,
                        PrefabKey = new PrefabInstanceKey { TargetPrefabId = 100, TargetObjectId = 200 },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = System.Array.Empty<IdentityMapEntry>(),
                Assets = new[] { new AssetEntry { Guid = PrefabGuid, LastKnownPath = PrefabPath, TypeHint = "GameObject" } },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.IsType<AppendStatement>(Assert.Single(result.Patch.Edits));
            Assert.Equal(PrefabPath, append.SourcePrefabPath);
            Assert.Null(append.ParentAnchor);

            var added = Assert.Single(result.AddedEntries);
            Assert.Equal("PrefabInstance", added.Kind);
            Assert.Equal(PrefabGuid, added.SourcePrefabGuid);
            Assert.NotNull(added.PrefabKey);
        }

        [Fact]
        public void Reconcile_SnapshotOnlyInstanceNestedUnderMappedParent_AppendsUnderParentAnchor()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "pickups", Name = "Pickups" } },
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance-1",
                Name = "Enemy",
                SourcePrefabGuid = PrefabGuid,
            };

            var snapshotParent = new SnapshotNode
            {
                GlobalObjectId = "goid-pickups",
                Name = "Pickups",
                Children = new[] { snapshotInstance },
            };

            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotParent } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "pickups", GlobalObjectId = "goid-pickups", Kind = "GameObject" },
                },
                Assets = new[] { new AssetEntry { Guid = PrefabGuid, LastKnownPath = PrefabPath, TypeHint = "GameObject" } },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.IsType<AppendStatement>(Assert.Single(result.Patch.Edits));
            Assert.Equal("pickups", append.ParentAnchor);
            Assert.Equal(PrefabPath, append.SourcePrefabPath);
        }

        [Fact]
        public void Reconcile_SnapshotOnlyInstance_NoMatchingAssetsEntry_EmitsDanglingReferenceConflict_NoStatement()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = System.Array.Empty<GameObjectNode>() };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-instance-1", Name = "Enemy", SourcePrefabGuid = PrefabGuid },
                },
            };

            // No Assets[] entry for PrefabGuid — the path cannot be re-derived.
            var map = new IdentityMap { Entries = System.Array.Empty<IdentityMapEntry>() };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.DoesNotContain(result.Patch.Edits, e => e is AppendStatement);
            Assert.Contains(result.Conflicts, c => c.Kind == ConflictKind.DanglingReference);
        }

        [Fact]
        public void Reconcile_SourceOnlyInstance_AbsentFromSnapshot_EmitsRemoveStatement()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new PrefabInstanceNode
                    {
                        LogicalId = "enemy-1",
                        Name = "Enemy",
                        SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
                    },
                },
            };

            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "enemy-1",
                        GlobalObjectId = "goid-instance-1",
                        Kind = "PrefabInstance",
                        SourcePrefabGuid = PrefabGuid,
                    },
                },
            };

            var anchors = new Dictionary<string, SourceSpan> { ["enemy-1"] = new SourceSpan(0, 10) };

            var result = Reconciler.Reconcile(model, snapshot, map, anchors);

            Assert.Contains(result.Patch.Edits, e => e is RemoveStatement rs && rs.Anchor == "enemy-1");
            Assert.Contains("enemy-1", result.RemovedLogicalIds);
        }

        private const string InstanceAppendFixture = @"
public class InstanceAppendScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var existing = scene.Add(""Existing"");
    }
}
";

        [Fact]
        public void AppendedInstanceStatement_RendersInstanceCall_AndReparsesToEquivalentPrefabInstanceNode()
        {
            var source = InstanceAppendFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendStatement
                    {
                        NewLogicalId = "New/1",
                        NewSiblingIndex = 1,
                        Name = "Enemy",
                        SourcePrefabPath = PrefabPath,
                        Transform = new TransformData { Position = new Vec3(1f, 2f, 3f) },
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains($"scene.Instance(\"{PrefabPath}\")", result);
            Assert.Contains(".Transform(pos: (1f, 2f, 3f))", result);
            Assert.DoesNotContain(".Add(\"Enemy\")", result);

            var reparsed = BuilderParser.Parse(result);
            Assert.Equal(2, reparsed.Model.Roots.Length);
            var appended = Assert.IsType<PrefabInstanceNode>(reparsed.Model.Roots[1]);
            Assert.Equal(PrefabPath, appended.SourcePrefab.DisplayPath);
            Assert.Equal(new Vec3(1f, 2f, 3f), appended.Transform.Position);
        }

        // b4-t2: a mapped prefab instance is moved (reparent/reorder). The Reconciler must consume
        // the Differ's Reparent/Reorder op for it exactly as it does for a plain GameObject —
        // otherwise the op is silently dropped, no source edit is emitted, and a second sync-back
        // pass re-detects the SAME move forever (never idempotent).
        private const string InstanceMoveFixture = @"
public class InstanceMoveScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        var pickups = scene.Add(""Pickups"");
    }
}
";

        [Fact]
        public void Reconcile_MappedInstanceReparented_IsIdempotentOnSecondPass_AndPreservesPrefabIdentity()
        {
            var parsed = BuilderParser.Parse(InstanceMoveFixture);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "enemy",
                        GlobalObjectId = "goid-enemy",
                        Kind = "PrefabInstance",
                        SourcePrefabGuid = PrefabGuid,
                        PrefabKey = new PrefabInstanceKey { TargetPrefabId = 100, TargetObjectId = 200 },
                    },
                    new IdentityMapEntry { LogicalId = "pickups", GlobalObjectId = "goid-pickups", Kind = "GameObject" },
                },
                Assets = new[] { new AssetEntry { Guid = PrefabGuid, LastKnownPath = PrefabPath, TypeHint = "GameObject" } },
            };

            // Live scene: "enemy" is now nested under "pickups" (was a root sibling in source).
            var snapshotEnemy = new SnapshotNode { GlobalObjectId = "goid-enemy", Name = "Enemy", SourcePrefabGuid = PrefabGuid };
            var snapshotPickups = new SnapshotNode
            {
                GlobalObjectId = "goid-pickups",
                Name = "Pickups",
                Children = new[] { snapshotEnemy },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotPickups } };

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            Assert.Contains(recon1.Patch.Edits, e => e is MoveStatement ms && ms.Anchor == "enemy" && ms.NewParentAnchor == "pickups");

            var patched = SourcePatchApplier.Apply(InstanceMoveFixture, recon1.Patch, parsed.Anchors);

            var foldedEntries = map.Entries
                .Where(e => !recon1.RemovedLogicalIds.Contains(e.LogicalId))
                .Concat(recon1.AddedEntries)
                .ToArray();
            var updatedMap = map with { Entries = foldedEntries };

            var reparsed = BuilderParser.Parse(patched, updatedMap);

            var enemyEntry = Assert.Single(reparsed.IdentityMap.Entries, e => e.LogicalId == "enemy");
            Assert.Equal("PrefabInstance", enemyEntry.Kind);
            Assert.Equal(PrefabGuid, enemyEntry.SourcePrefabGuid);
            Assert.Equal(100ul, enemyEntry.PrefabKey?.TargetPrefabId);
            Assert.Equal(200ul, enemyEntry.PrefabKey?.TargetObjectId);

            var recon2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors);

            Assert.Empty(recon2.Patch.Edits);
            Assert.Empty(recon2.Conflicts);

            var plan2 = Materializer.Materialize(reparsed.Model, snapshot, reparsed.IdentityMap);
            Assert.Empty(plan2.Ops);
        }

        [Fact]
        public void Reconcile_MappedInstanceReordered_IsIdempotentOnSecondPass()
        {
            var parsed = BuilderParser.Parse(InstanceMoveFixture);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "enemy",
                        GlobalObjectId = "goid-enemy",
                        Kind = "PrefabInstance",
                        SourcePrefabGuid = PrefabGuid,
                        PrefabKey = new PrefabInstanceKey { TargetPrefabId = 100, TargetObjectId = 200 },
                    },
                    new IdentityMapEntry { LogicalId = "pickups", GlobalObjectId = "goid-pickups", Kind = "GameObject" },
                },
                Assets = new[] { new AssetEntry { Guid = PrefabGuid, LastKnownPath = PrefabPath, TypeHint = "GameObject" } },
            };

            // Source order is [enemy, pickups] (indices 0, 1); the live scene swapped their sibling
            // order at the same (root) parent — a pure reorder, no reparent.
            var snapshotPickups = new SnapshotNode { GlobalObjectId = "goid-pickups", Name = "Pickups" };
            var snapshotEnemy = new SnapshotNode { GlobalObjectId = "goid-enemy", Name = "Enemy", SourcePrefabGuid = PrefabGuid };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotPickups, snapshotEnemy } };

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            Assert.Contains(recon1.Patch.Edits, e => e is ReorderStatement rs && rs.Anchor == "enemy" && rs.NewSiblingIndex == 1);

            var patched = SourcePatchApplier.Apply(InstanceMoveFixture, recon1.Patch, parsed.Anchors);

            var foldedEntries = map.Entries
                .Where(e => !recon1.RemovedLogicalIds.Contains(e.LogicalId))
                .Concat(recon1.AddedEntries)
                .ToArray();
            var updatedMap = map with { Entries = foldedEntries };

            var reparsed = BuilderParser.Parse(patched, updatedMap);

            var recon2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors);

            Assert.Empty(recon2.Patch.Edits);
            Assert.Empty(recon2.Conflicts);
        }

        // ---- b4-t2: instance-root override reconcile (scene->code append / revert-drop) --------
        // Snapshot is the live authority for THIS direction: a snapshot-only override/add/remove
        // is appended as a chained call (#7); a model-authored one absent from the snapshot means
        // the user reverted it in Unity, so the call is DROPPED (#9), never rewritten to a default.

        private static OverrideTarget OverrideTargetFor(string typeFullName) =>
            new() { ComponentType = typeFullName };

        private static (PrefabInstanceNode instance, SnapshotNode snapshotInstance, IdentityMap map) BuildMatchedInstance(
            PropertyOverride[]? modelOverrides = null,
            PropertyOverride[]? snapshotOverrides = null,
            AddedComponent[]? modelAddedComponents = null,
            AddedComponent[]? snapshotAddedComponents = null,
            OverrideTarget[]? modelRemovedComponents = null,
            OverrideTarget[]? snapshotRemovedComponents = null)
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
                Overrides = modelOverrides ?? System.Array.Empty<PropertyOverride>(),
                AddedComponents = modelAddedComponents ?? System.Array.Empty<AddedComponent>(),
                RemovedComponents = modelRemovedComponents ?? System.Array.Empty<OverrideTarget>(),
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance-1",
                Name = "Enemy",
                SourcePrefabGuid = PrefabGuid,
                Overrides = snapshotOverrides ?? System.Array.Empty<PropertyOverride>(),
                AddedComponents = snapshotAddedComponents ?? System.Array.Empty<AddedComponent>(),
                RemovedComponents = snapshotRemovedComponents ?? System.Array.Empty<OverrideTarget>(),
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "instance-1", GlobalObjectId = "goid-instance-1", Kind = "PrefabInstance",
                        SourcePrefabGuid = PrefabGuid,
                    },
                },
            };

            return (instance, snapshotInstance, map);
        }

        [Fact]
        public void Reconcile_SnapshotOnlyOverride_AppendsInstanceOverride_WithRootTypeAndPath()
        {
            var target = OverrideTargetFor("UnityEngine.BoxCollider");
            var snapshotOverride = new PropertyOverride { Target = target, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(50) };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotOverrides: new[] { snapshotOverride });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceOverride>());
            Assert.Equal("instance-1", append.Anchor);
            var set = Assert.Single(append.Sets);
            Assert.Equal("UnityEngine.BoxCollider", set.TypeFullName);
            Assert.Equal("m_Health", set.PropertyPath);
            Assert.Equal(ValueNode.Primitive.Int(50), set.Value);
        }

        [Fact]
        public void Reconcile_SnapshotOnlyAddedComponent_AppendsInstanceAddComponent()
        {
            var target = OverrideTargetFor("UnityEngine.Light");
            var added = new AddedComponent { Target = target, Component = new ComponentData { LogicalId = "light-1", Type = new TypeRef("UnityEngine.Light") } };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotAddedComponents: new[] { added });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddComponent>());
            Assert.Equal("instance-1", append.Anchor);
            Assert.Equal("UnityEngine.Light", append.TypeFullName);
            // Regression guard (m10-b4-t2 iteration 2): a fieldless snapshot component stays a
            // bare `.AddComponent<T>()` — no Fields/FieldExpressions fabricated from nothing.
            Assert.Empty(append.Fields);
            Assert.Null(append.FieldExpressions);
        }

        // ---- m10-b4-t2 iteration 2: added-component field values must be carried into the
        // reconciled AppendInstanceAddComponent (symmetric with materialize's EmitAddedComponents
        // and with the plain-node AppendComponentStatement field-emit convention). Iteration 1
        // shipped Anchor/TypeFullName only, silently dropping every authored/observed field value
        // (scope/bucket-b4.md SEVERITY med finding).

        [Fact]
        public void Reconcile_SnapshotOnlyAddedComponent_ScalarField_CarriesFieldIntoAppend()
        {
            var target = OverrideTargetFor("UnityEngine.Light");
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("m_Intensity", ValueNode.Primitive.Float(2.5f)),
            });
            var added = new AddedComponent
            {
                Target = target,
                Component = new ComponentData { LogicalId = "light-1", Type = new TypeRef("UnityEngine.Light"), Fields = fields },
            };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotAddedComponents: new[] { added });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddComponent>());
            Assert.Equal("UnityEngine.Light", append.TypeFullName);
            Assert.Equal(ValueNode.Primitive.Float(2.5f), append.Fields["m_Intensity"]);
            Assert.Null(append.FieldExpressions);
        }

        [Fact]
        public void Reconcile_SnapshotOnlyAddedComponent_AssetRefField_HarvestsAssetIntoAddedAssets()
        {
            var target = OverrideTargetFor("UnityEngine.MeshRenderer");
            var assetValue = new ValueNode.AssetRef(new AssetRef { Guid = "mat-guid-added", DisplayPath = "Assets/Materials/Added.mat" });
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("m_Material", assetValue),
            });
            var added = new AddedComponent
            {
                Target = target,
                Component = new ComponentData { LogicalId = "renderer-1", Type = new TypeRef("UnityEngine.MeshRenderer"), Fields = fields },
            };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotAddedComponents: new[] { added });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddComponent>());
            Assert.Equal(assetValue, append.Fields["m_Material"]);
            var addedAsset = Assert.Single(result.AddedAssets, a => a.Guid == "mat-guid-added");
            Assert.Equal("Assets/Materials/Added.mat", addedAsset.LastKnownPath);
        }

        [Fact]
        public void Reconcile_SnapshotOnlyAddedComponent_ObjectRefField_PreRendersFieldExpression_AndIntroducesHandle()
        {
            const string doorLid = "Door/0";
            var target = OverrideTargetFor("Game.DoorOpener");
            // "Door/0" is a synthesized-shape LogicalId (parent-less, sibling index 0) authored in
            // the model — a genuinely resolvable target — so ResolveOwnerHandle treats it as not
            // yet having a `var` name and introduces a handle for it, exercising the same
            // side-effecting IntroduceHandle path override/component-field reconcile already use
            // (ComponentReconciler.RenderFieldValue).
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(doorLid)),
            });
            var added = new AddedComponent
            {
                Target = target,
                Component = new ComponentData { LogicalId = "opener-1", Type = new TypeRef("Game.DoorOpener"), Fields = fields },
            };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotAddedComponents: new[] { added });
            var door = new GameObjectNode { LogicalId = doorLid, Name = "Door" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { door, instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddComponent>());
            Assert.Equal(new ValueNode.ObjectRef(doorLid), append.Fields["target"]);
            Assert.NotNull(append.FieldExpressions);
            Assert.True(append.FieldExpressions!.TryGetValue("target", out var renderedHandle));

            var introduced = Assert.Single(result.Patch.Edits.OfType<IntroduceHandle>());
            Assert.Equal(doorLid, introduced.Anchor);
            Assert.Equal(introduced.Handle, renderedHandle);
        }

        [Fact]
        public void Reconcile_SnapshotOnlyAddedComponent_ListFieldWithDanglingTarget_OmitsFieldAndReportsDanglingReference()
        {
            var target = OverrideTargetFor("Game.DoorOpener");
            // "ghost-1" is neither a live scene object, nor in the parsed source model, nor a
            // known handle, nor a same-batch create candidate -- unresolvable by construction, the
            // same shape ObjectReferenceResolver.BuildSceneRefResolver yields for a scene reference
            // to an object the identity map has never mapped.
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("targets", new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef("ghost-1") })),
            });
            var added = new AddedComponent
            {
                Target = target,
                Component = new ComponentData { LogicalId = "opener-1", Type = new TypeRef("Game.DoorOpener"), Fields = fields },
            };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotAddedComponents: new[] { added });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddComponent>());
            Assert.False(append.Fields.ContainsKey("targets"));
            Assert.False(append.FieldExpressions != null && append.FieldExpressions.ContainsKey("targets"));
            var conflict = Assert.Single(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Contains("ghost-1", conflict.Reason);
        }

        [Fact]
        public void Reconcile_SnapshotOnlyAddedComponent_BareObjectRefWithDanglingTarget_OmitsFieldAndReportsDanglingReference()
        {
            var target = OverrideTargetFor("Game.DoorOpener");
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef("ghost-1")),
            });
            var added = new AddedComponent
            {
                Target = target,
                Component = new ComponentData { LogicalId = "opener-1", Type = new TypeRef("Game.DoorOpener"), Fields = fields },
            };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotAddedComponents: new[] { added });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddComponent>());
            Assert.False(append.Fields.ContainsKey("target"));
            Assert.False(append.FieldExpressions != null && append.FieldExpressions.ContainsKey("target"));
            var conflict = Assert.Single(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Contains("ghost-1", conflict.Reason);
            Assert.Empty(result.Patch.Edits.OfType<IntroduceHandle>());
        }

        [Fact]
        public void Reconcile_SnapshotOnlyAddedComponent_ListFieldWithPendingTarget_OmitsFieldSilently()
        {
            const string pendingGoid = "goid-newtarget";
            var target = OverrideTargetFor("Game.DoorOpener");
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("targets", new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(pendingGoid) })),
            });
            var added = new AddedComponent
            {
                Target = target,
                Component = new ComponentData { LogicalId = "opener-1", Type = new TypeRef("Game.DoorOpener"), Fields = fields },
            };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotAddedComponents: new[] { added });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            // pendingGoid is a genuinely-new, same-batch object: present in the snapshot with no
            // IdentityMap entry yet -- resolves on the guaranteed second Sync, not dangling.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { snapshotInstance, new SnapshotNode { GlobalObjectId = pendingGoid, Name = "NewTarget" } },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddComponent>());
            Assert.False(append.Fields.ContainsKey("targets"));
            Assert.False(append.FieldExpressions != null && append.FieldExpressions.ContainsKey("targets"));
            Assert.Empty(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Empty(result.Notes.Where(c => c.Kind == ConflictKind.UnrepresentableValue));
        }

        [Fact]
        public void Reconcile_SnapshotOnlyAddedComponent_MixedResolvableAndDanglingList_OmitsWholeFieldAndReportsOnce()
        {
            const string doorLid = "Door/0";
            var target = OverrideTargetFor("Game.DoorOpener");
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "targets",
                    new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.ObjectRef("ghost-1") })),
            });
            var added = new AddedComponent
            {
                Target = target,
                Component = new ComponentData { LogicalId = "opener-1", Type = new TypeRef("Game.DoorOpener"), Fields = fields },
            };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotAddedComponents: new[] { added });
            var door = new GameObjectNode { LogicalId = doorLid, Name = "Door" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { door, instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddComponent>());
            Assert.False(append.Fields.ContainsKey("targets"));
            Assert.False(append.FieldExpressions != null && append.FieldExpressions.ContainsKey("targets"));
            var conflict = Assert.Single(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Contains("ghost-1", conflict.Reason);
            // The resolvable element must not have side-effected a handle introduction for an
            // omitted field.
            Assert.Empty(result.Patch.Edits.OfType<IntroduceHandle>());
        }

        [Fact]
        public void Reconcile_SnapshotOnlyAddedComponent_DanglingField_LeavesSiblingFieldsEmitted()
        {
            var target = OverrideTargetFor("Game.DoorOpener");
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("before", ValueNode.Primitive.Float(1f)),
                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef("ghost-1")),
                new KeyValuePair<string, ValueNode>("after", ValueNode.Primitive.Float(2f)),
            });
            var added = new AddedComponent
            {
                Target = target,
                Component = new ComponentData { LogicalId = "opener-1", Type = new TypeRef("Game.DoorOpener"), Fields = fields },
            };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotAddedComponents: new[] { added });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddComponent>());
            Assert.False(append.Fields.ContainsKey("target"));
            Assert.Equal(ValueNode.Primitive.Float(1f), append.Fields["before"]);
            Assert.Equal(ValueNode.Primitive.Float(2f), append.Fields["after"]);
        }

        [Fact]
        public void Reconcile_SnapshotOnlyAddedComponent_ChildPathTarget_DanglingField_OmitsOnScopedOp()
        {
            var target = new OverrideTarget { ComponentType = "Game.DoorOpener", ChildPath = "Body/Door" };
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef("ghost-1")),
            });
            var added = new AddedComponent
            {
                Target = target,
                Component = new ComponentData { LogicalId = "opener-1", Type = new TypeRef("Game.DoorOpener"), Fields = fields },
            };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotAddedComponents: new[] { added });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var scopedOn = Assert.Single(result.Patch.Edits.OfType<AppendScopedOn>());
            var scopedAdd = Assert.IsType<ScopedAddComponentOp>(Assert.Single(scopedOn.Ops));
            Assert.False(scopedAdd.Fields.ContainsKey("target"));
            Assert.False(scopedAdd.FieldExpressions != null && scopedAdd.FieldExpressions.ContainsKey("target"));
            var conflict = Assert.Single(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Contains("ghost-1", conflict.Reason);
        }

        [Fact]
        public void Reconcile_SnapshotOnlyAddedComponent_ListFieldWithUnemittableItem_OmitsFieldAndReportsUnrepresentableValue()
        {
            const string doorLid = "door-1";
            var target = OverrideTargetFor("Game.DoorOpener");
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "targets",
                    new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.Unsupported("ObjectReference") })),
            });
            var added = new AddedComponent
            {
                Target = target,
                Component = new ComponentData { LogicalId = "opener-1", Type = new TypeRef("Game.DoorOpener"), Fields = fields },
            };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotAddedComponents: new[] { added });
            var door = new GameObjectNode { LogicalId = doorLid, Name = "Door" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { door, instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };
            map = map with
            {
                Entries = map.Entries
                    .Append(new IdentityMapEntry { LogicalId = doorLid, GlobalObjectId = "goid-door", Kind = "GameObject" })
                    .ToArray(),
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddComponent>());
            Assert.False(append.Fields.ContainsKey("targets"));
            Assert.False(append.FieldExpressions != null && append.FieldExpressions.ContainsKey("targets"));
            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);
        }

        [Fact]
        public void Reconcile_SnapshotOnlyRemovedComponent_AppendsInstanceRemoveComponent()
        {
            var target = OverrideTargetFor("UnityEngine.BoxCollider");
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotRemovedComponents: new[] { target });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceRemoveComponent>());
            Assert.Equal("instance-1", append.Anchor);
            Assert.Equal("UnityEngine.BoxCollider", append.TypeFullName);
        }

        [Fact]
        public void Reconcile_ModelOnlyOverride_AbsentFromSnapshot_DropsOverrideCall()
        {
            var target = OverrideTargetFor("UnityEngine.BoxCollider");
            var modelOverride = new PropertyOverride { Target = target, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(50) };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(modelOverrides: new[] { modelOverride });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var drop = Assert.Single(result.Patch.Edits.OfType<DropInstanceCall>());
            Assert.Equal("instance-1", drop.Anchor);
            Assert.Equal(InstanceCallKind.Override, drop.Kind);
            Assert.Equal("m_Health", drop.PropertyPath);
        }

        [Fact]
        public void Reconcile_ModelOnlyAddedComponent_AbsentFromSnapshot_DropsAddComponentCall()
        {
            var target = OverrideTargetFor("UnityEngine.Light");
            var added = new AddedComponent { Target = target, Component = new ComponentData { LogicalId = "light-1", Type = new TypeRef("UnityEngine.Light") } };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(modelAddedComponents: new[] { added });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var drop = Assert.Single(result.Patch.Edits.OfType<DropInstanceCall>());
            Assert.Equal("instance-1", drop.Anchor);
            Assert.Equal(InstanceCallKind.AddComponent, drop.Kind);
            Assert.Equal("UnityEngine.Light", drop.TypeFullName);
        }

        [Fact]
        public void Reconcile_SnapshotOnlyObjectReferenceOverride_AppendsWithAssetExpression()
        {
            var target = OverrideTargetFor("UnityEngine.MeshRenderer");
            var assetRefValue = new ValueNode.AssetRef(new AssetRef { Guid = "mat-guid-1", DisplayPath = "Assets/Materials/Enemy.mat" });
            var snapshotOverride = new PropertyOverride
            {
                Target = target,
                PropertyPath = "m_Material",
                Value = new ValueNode.Unsupported(""),
                ObjectReference = assetRefValue,
            };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(snapshotOverrides: new[] { snapshotOverride });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };
            var anchors = new Dictionary<string, SourceSpan> { ["instance-1"] = new SourceSpan(0, 10) };

            var result = Reconciler.Reconcile(model, snapshot, map, anchors);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceOverride>());
            var set = Assert.Single(append.Sets);
            Assert.Equal("m_Material", set.PropertyPath);
            var rendered = set.ValueExpression ?? SourceExpr.ValueNodeLiteral(set.Value);
            Assert.Equal("Asset(\"Assets/Materials/Enemy.mat\")", rendered);

            var addedAsset = Assert.Single(result.AddedAssets);
            Assert.Equal("mat-guid-1", addedAsset.Guid);
        }

        [Fact]
        public void Reconcile_StaleOverride_SurfacesConflict_AndSuppressesAppendAndDrop()
        {
            var target = OverrideTargetFor("UnityEngine.BoxCollider");
            var recordedBase = ValueNode.Primitive.Int(100);
            var currentBase = ValueNode.Primitive.Int(75);
            var modelOverride = new PropertyOverride { Target = target, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(100), BaseValue = recordedBase };
            var snapshotOverride = new PropertyOverride { Target = target, PropertyPath = "m_Health", Value = currentBase, BaseValue = currentBase };
            var (instance, snapshotInstance, map) = BuildMatchedInstance(
                modelOverrides: new[] { modelOverride },
                snapshotOverrides: new[] { snapshotOverride });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            // A StaleOverride report is a STANDING condition (spec 44 Invariant #2): it carries a
            // stable RecurrenceKey, so Reconciler's RecurrenceKey split routes it into Notes (the
            // once-per-session channel), not the per-pass Conflicts array.
            Assert.Contains(result.Notes, c => c.Kind == ConflictKind.StaleOverride && c.LogicalId == "instance-1");
            Assert.DoesNotContain(result.Conflicts, c => c.Kind == ConflictKind.StaleOverride);
            Assert.Empty(result.Patch.Edits.OfType<AppendInstanceOverride>());
            Assert.Empty(result.Patch.Edits.OfType<DropInstanceCall>());
        }

        // ---- m-nested-props b4-t2: below-root membership diff (nested override rename stability,
        // child-GameObject add/remove) -----------------------------------------------------------

        private const string HandledInstanceFixture = @"
public class HandledInstanceScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
    }
}
";

        // Spec #2: a nested override's ChildPath is re-derived by the adapter on every sync (it is
        // NOT part of OverrideTarget equality — Model/OverrideTarget.cs keys on (SubKey,
        // ComponentType) only). A pure rename (same SubKey/ComponentType/PropertyPath, new
        // ChildPath) must therefore compare EQUAL — no drop, no re-add — while a genuinely NEW
        // nested override on that same (renamed) sub-object still must route through the nested
        // `.On` emit (AppendScopedOn), never the flat root AppendInstanceOverride.
        [Fact]
        public void NestedOverride_Rename_ReDerivesPath_KeepsKey()
        {
            var subKey = new PrefabInstanceKey { TargetObjectId = 42 };
            var healthTarget = new OverrideTarget { SubKey = subKey, ComponentType = "UnityEngine.BoxCollider", ChildPath = "Turret/Barrel" };
            var modelHealthOverride = new PropertyOverride { Target = healthTarget, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(50) };

            // Renamed in Unity: same SubKey/ComponentType/PropertyPath, ChildPath now "Cannon/Barrel".
            var renamedHealthTarget = healthTarget with { ChildPath = "Cannon/Barrel" };
            var snapshotHealthOverride = new PropertyOverride { Target = renamedHealthTarget, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(50) };

            // A genuinely new nested override authored on the SAME (renamed) sub-object.
            var armorTarget = renamedHealthTarget with { };
            var snapshotArmorOverride = new PropertyOverride { Target = armorTarget, PropertyPath = "m_Armor", Value = ValueNode.Primitive.Int(30) };

            var (instance, snapshotInstance, map) = BuildMatchedInstance(
                modelOverrides: new[] { modelHealthOverride },
                snapshotOverrides: new[] { snapshotHealthOverride, snapshotArmorOverride });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            // Identity stable across the rename: no drop of the carried-over override, and it is
            // not re-appended either (root OR nested form).
            Assert.DoesNotContain(result.Patch.Edits, e => e is DropInstanceCall d && d.PropertyPath == "m_Health");
            Assert.DoesNotContain(result.Patch.Edits, e => e is AppendInstanceOverride a && a.Sets.Any(s => s.PropertyPath == "m_Health"));
            Assert.DoesNotContain(result.Patch.Edits, e => e is AppendScopedOn s && s.Ops.OfType<ScopedOverrideOp>().Any(op => op.Sets.Any(set => set.PropertyPath == "m_Health")));

            // The new nested override routes through the scoped `.On` emit, keyed on the CURRENT
            // (renamed) child path — never a flat root AppendInstanceOverride.
            Assert.Empty(result.Patch.Edits.OfType<AppendInstanceOverride>());
            var scoped = Assert.Single(result.Patch.Edits.OfType<AppendScopedOn>());
            Assert.Equal("Cannon/Barrel", scoped.SelectorMatchKey);
            var op = Assert.Single(scoped.Ops.OfType<ScopedOverrideOp>());
            var set = Assert.Single(op.Sets);
            Assert.Equal("m_Armor", set.PropertyPath);
        }

        // Spec #6: a snapshot-only AddedGameObjects entry under a nested parent emits a top-level
        // `.AddChild(parentPath, name)` — b4-t1 under-delivered this machinery (see research.md), so
        // both the reconcile emit AND the apply/reparse round trip are new behavior.
        [Fact]
        public void AddedChildGameObject_RoundTrips()
        {
            var added = new AddedGameObject
            {
                Parent = new OverrideTarget { ChildPath = "Turret" },
                Node = new GameObjectNode { Name = "MuzzleFlash" },
            };

            var instance = new PrefabInstanceNode
            {
                LogicalId = "enemy",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
            };
            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-enemy",
                Name = "Enemy",
                SourcePrefabGuid = PrefabGuid,
                AddedGameObjects = new[] { added },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "enemy", GlobalObjectId = "goid-enemy", Kind = "PrefabInstance", SourcePrefabGuid = PrefabGuid },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddChild>());
            Assert.Equal("enemy", append.Anchor);
            Assert.Equal("Turret", append.ParentPath);
            Assert.Equal("MuzzleFlash", append.Name);

            var anchors = BuilderParser.Parse(HandledInstanceFixture).Anchors;
            var rendered = SourcePatchApplier.Apply(HandledInstanceFixture, result.Patch, anchors);

            Assert.Contains(".AddChild(\"Turret\", \"MuzzleFlash\")", rendered);

            var reparsed = BuilderParser.Parse(rendered);
            var reparsedInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            var reparsedAdded = Assert.Single(reparsedInstance.AddedGameObjects);
            Assert.Equal("Turret", reparsedAdded.Parent.ChildPath);
            Assert.Equal("MuzzleFlash", reparsedAdded.Node.Name);
        }

        // Spec #7: a snapshot-only RemovedGameObjects entry emits a top-level
        // `.RemoveChild(childPath)`.
        [Fact]
        public void RemovedChildGameObject_RoundTrips()
        {
            var removedTarget = new OverrideTarget { ChildPath = "Turret/Antenna" };

            var instance = new PrefabInstanceNode
            {
                LogicalId = "enemy",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
            };
            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-enemy",
                Name = "Enemy",
                SourcePrefabGuid = PrefabGuid,
                RemovedGameObjects = new[] { removedTarget },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "enemy", GlobalObjectId = "goid-enemy", Kind = "PrefabInstance", SourcePrefabGuid = PrefabGuid },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceRemoveChild>());
            Assert.Equal("enemy", append.Anchor);
            Assert.Equal("Turret/Antenna", append.ChildPath);

            var anchors = BuilderParser.Parse(HandledInstanceFixture).Anchors;
            var rendered = SourcePatchApplier.Apply(HandledInstanceFixture, result.Patch, anchors);

            Assert.Contains(".RemoveChild(\"Turret/Antenna\")", rendered);

            var reparsed = BuilderParser.Parse(rendered);
            var reparsedInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            var reparsedRemoved = Assert.Single(reparsedInstance.RemovedGameObjects);
            Assert.Equal("Turret/Antenna", reparsedRemoved.ChildPath);
        }
    }
}
