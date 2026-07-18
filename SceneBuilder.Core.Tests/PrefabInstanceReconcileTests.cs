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
    }
}
