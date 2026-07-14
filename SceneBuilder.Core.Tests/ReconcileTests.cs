using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class ReconcileTests
    {
        [Fact]
        public void Reconcile_Move_ProducesTransformArgumentPatch()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "root-1", Name = "Root" } },
            };

            var driftedTransform = new TransformData { Position = new Vec3(10, 20, 30) };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = driftedTransform } },
            };

            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" } },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var edit = Assert.Single(result.Patch.Edits);
            var patchArg = Assert.IsType<PatchArgument>(edit);
            Assert.Equal("root-1", patchArg.Anchor);
            Assert.Equal("pos", patchArg.ArgName);
            Assert.Contains("10", patchArg.NewExpr);
            Assert.Contains("20", patchArg.NewExpr);
            Assert.Contains("30", patchArg.NewExpr);
        }

        [Fact]
        public void Reconcile_MoveToFractionalPosition_EmitsCompilableFloatLiterals()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "root-1", Name = "Root" } },
            };

            var driftedTransform = new TransformData { Position = new Vec3(0f, 1.53f, 0f) };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = driftedTransform } },
            };

            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" } },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var patchArg = Assert.IsType<PatchArgument>(Assert.Single(result.Patch.Edits));
            // Non-integer components MUST carry the 'f' suffix, else the generated (int,double,int)
            // tuple won't convert to the (float,float,float) authoring API parameter (won't compile).
            Assert.Equal("(0, 1.53f, 0)", patchArg.NewExpr);
        }

        [Fact]
        public void Reconcile_Rename_ProducesNameArgumentPatch_LogicalIdUnchanged()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "root-1", Name = "OldName" } },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-root", Name = "NewName" } },
            };

            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" } },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var edit = Assert.Single(result.Patch.Edits);
            var patchArg = Assert.IsType<PatchArgument>(edit);
            Assert.Equal("root-1", patchArg.Anchor);
            Assert.Equal("name", patchArg.ArgName);
            Assert.Contains("NewName", patchArg.NewExpr);
        }

        [Fact]
        public void Reconcile_Reparent_ProducesMoveStatement()
        {
            var c = new GameObjectNode { LogicalId = "c", Name = "C" };
            var a = new GameObjectNode { LogicalId = "a", Name = "A", Children = new[] { c } };
            var b = new GameObjectNode { LogicalId = "b", Name = "B" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { a, b } };

            var snapshotC = new SnapshotNode { GlobalObjectId = "goid-c", Name = "C" };
            var snapshotA = new SnapshotNode { GlobalObjectId = "goid-a", Name = "A" };
            var snapshotB = new SnapshotNode { GlobalObjectId = "goid-b", Name = "B", Children = new[] { snapshotC } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotA, snapshotB } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "a", GlobalObjectId = "goid-a", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "b", GlobalObjectId = "goid-b", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "c", GlobalObjectId = "goid-c", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var edit = Assert.Single(result.Patch.Edits);
            var move = Assert.IsType<MoveStatement>(edit);
            Assert.Equal("c", move.Anchor);
            Assert.Equal("b", move.NewParentAnchor);
        }

        [Fact]
        public void Reconcile_Reorder_ProducesReorderStatement()
        {
            var x = new GameObjectNode { LogicalId = "x", Name = "X" };
            var y = new GameObjectNode { LogicalId = "y", Name = "Y" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { x, y } };

            var snapshotX = new SnapshotNode { GlobalObjectId = "goid-x", Name = "X" };
            var snapshotY = new SnapshotNode { GlobalObjectId = "goid-y", Name = "Y" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotY, snapshotX } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "x", GlobalObjectId = "goid-x", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "y", GlobalObjectId = "goid-y", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var reorders = result.Patch.Edits.OfType<ReorderStatement>().ToArray();
            Assert.Contains(reorders, r => r.Anchor == "x" && r.NewSiblingIndex == 1);
        }

        [Fact]
        public void Reconcile_RenamedSameGlobalObjectId_IsRename_NotDeleteThenCreate()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "root-1", Name = "OldName" } },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-root", Name = "NewName" } },
            };

            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" } },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Single(result.Patch.Edits);
            var patchArg = Assert.IsType<PatchArgument>(result.Patch.Edits[0]);
            Assert.Equal("name", patchArg.ArgName);
        }

        [Fact]
        public void Reconcile_NewGlobalObjectId_IsCreate_MissingGlobalObjectId_IsDelete()
        {
            var kept = new GameObjectNode { LogicalId = "kept-1", Name = "Kept" };
            var deletedInScene = new GameObjectNode { LogicalId = "deleted-1", Name = "DeletedInScene" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { kept, deletedInScene } };

            var snapshotKept = new SnapshotNode { GlobalObjectId = "goid-kept", Name = "Kept" };
            var snapshotCreatedInScene = new SnapshotNode { GlobalObjectId = "goid-created-in-scene", Name = "CreatedInScene" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotKept, snapshotCreatedInScene } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "kept-1", GlobalObjectId = "goid-kept", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "deleted-1", GlobalObjectId = "goid-deleted-missing", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Patch.Edits);
        }

        [Fact]
        public void Reconcile_EventBatchNarrowerThanSnapshot_PatchesAllSnapshotEdits()
        {
            var a = new GameObjectNode { LogicalId = "a", Name = "A" };
            var b = new GameObjectNode { LogicalId = "b", Name = "OldB" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { a, b } };

            var movedTransform = new TransformData { Position = new Vec3(5, 6, 7) };
            var snapshotA = new SnapshotNode { GlobalObjectId = "goid-a", Name = "A", Transform = movedTransform };
            var snapshotB = new SnapshotNode { GlobalObjectId = "goid-b", Name = "NewB" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotA, snapshotB } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "a", GlobalObjectId = "goid-a", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "b", GlobalObjectId = "goid-b", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Equal(2, result.Patch.Edits.Length);
            Assert.Contains(result.Patch.Edits.OfType<PatchArgument>(), e => e.Anchor == "a" && e.ArgName == "pos" && e.NewExpr.Contains("5"));
            Assert.Contains(result.Patch.Edits.OfType<PatchArgument>(), e => e.Anchor == "b" && e.ArgName == "name" && e.NewExpr.Contains("NewB"));
        }

        [Fact]
        public void Reconcile_MovePatch_EmitsEulerRotation_WhileDiffingQuaternion()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "root-1", Name = "Root" } },
            };

            var rotatedTransform = new TransformData { Rotation = Rotation.EulerToQuat(0, 90, 0) };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = rotatedTransform } },
            };

            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" } },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var edit = Assert.Single(result.Patch.Edits);
            var patchArg = Assert.IsType<PatchArgument>(edit);
            Assert.Equal("root-1", patchArg.Anchor);
            Assert.Equal("rot", patchArg.ArgName);
            Assert.Contains("90", patchArg.NewExpr);
        }
    }
}
