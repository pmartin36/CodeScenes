using System;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
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
            // Every component carries the 'f' suffix — mandatory on non-integers (a bare 1.53 is a
            // double and won't convert to the float authoring parameter) and applied to integral
            // values too so the literal unambiguously reads as a float, never an int or double.
            Assert.Equal("(0f, 1.53f, 0f)", patchArg.NewExpr);
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

            // The unmapped scene-created root ("goid-created-in-scene") is a create candidate
            // (b2-t1) and produces one AppendStatement; the missing-goid delete candidate
            // ("deleted-1") is a delete candidate (b2-t4) and produces one RemoveStatement.
            Assert.Equal(2, result.Patch.Edits.Length);

            var append = Assert.IsType<AppendStatement>(
                Assert.Single(result.Patch.Edits, e => e is AppendStatement));
            Assert.Null(append.ParentAnchor);
            Assert.Equal("CreatedInScene", append.Name);

            var remove = Assert.IsType<RemoveStatement>(
                Assert.Single(result.Patch.Edits, e => e is RemoveStatement));
            Assert.Equal("deleted-1", remove.Anchor);
            Assert.Contains("deleted-1", result.RemovedLogicalIds);
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

        [Fact]
        public void Reconcile_NewRootObject_AppendsStatement_AndMapEntry()
        {
            var existingRoot = new GameObjectNode { LogicalId = "root-1", Name = "Existing" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { existingRoot } };

            var snapshotExisting = new SnapshotNode { GlobalObjectId = "goid-existing", Name = "Existing" };
            var newRootTransform = new TransformData { Position = new Vec3(1, 2, 3) };
            var snapshotNewRoot = new SnapshotNode { GlobalObjectId = "goid-new-root", Name = "NewRoot", Transform = newRootTransform };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotExisting, snapshotNewRoot } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-existing", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.IsType<AppendStatement>(Assert.Single(result.Patch.Edits));
            Assert.Null(append.ParentAnchor);
            Assert.Equal("NewRoot", append.Name);
            Assert.Equal(LogicalIdResolver.Synthesize(null, "NewRoot", 1), append.NewLogicalId);
            Assert.NotNull(append.Transform);
            Assert.Equal(new Vec3(1, 2, 3), append.Transform!.Position);

            var addedEntry = Assert.Single(result.AddedEntries);
            Assert.Equal(append.NewLogicalId, addedEntry.LogicalId);
            Assert.Equal("goid-new-root", addedEntry.GlobalObjectId);
            Assert.Equal("GameObject", addedEntry.Kind);
            Assert.Null(addedEntry.ParentLogicalId);
        }

        [Fact]
        public void Reconcile_NewChildOfMappedParent_AppendsUnderParent()
        {
            var existingChild = new GameObjectNode { LogicalId = "p/ExistingChild/0", Name = "ExistingChild" };
            var parent = new GameObjectNode { LogicalId = "p", Name = "Parent", Children = new[] { existingChild } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { parent } };

            var snapshotExistingChild = new SnapshotNode { GlobalObjectId = "goid-existing-child", Name = "ExistingChild" };
            var snapshotNewChild = new SnapshotNode { GlobalObjectId = "goid-new-child", Name = "NewChild" };
            var snapshotParent = new SnapshotNode
            {
                GlobalObjectId = "goid-parent",
                Name = "Parent",
                Children = new[] { snapshotExistingChild, snapshotNewChild },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotParent } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "p", GlobalObjectId = "goid-parent", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = "p/ExistingChild/0",
                        GlobalObjectId = "goid-existing-child",
                        Kind = "GameObject",
                        ParentLogicalId = "p",
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.IsType<AppendStatement>(Assert.Single(result.Patch.Edits));
            Assert.Equal("p", append.ParentAnchor);
            Assert.Equal("NewChild", append.Name);
            Assert.Equal(LogicalIdResolver.Synthesize("p", "NewChild", 1), append.NewLogicalId);
            // "p" is already a handle (non-synthesized LogicalId) - no handle introduction,
            // no map re-key (b2-t2 regression guard).
            Assert.Equal("p", append.ParentHandle);
            Assert.False(append.IntroduceParentHandle);
            Assert.Empty(result.RemovedLogicalIds);

            var addedEntry = Assert.Single(result.AddedEntries);
            Assert.Equal(append.NewLogicalId, addedEntry.LogicalId);
            Assert.Equal("goid-new-child", addedEntry.GlobalObjectId);
            Assert.Equal("p", addedEntry.ParentLogicalId);
        }

        [Fact]
        public void Reconcile_NewChildOfHandlelessParent_IntroducesHandle()
        {
            // "Parent" is a ROOT with a SYNTHESIZED (handle-less) LogicalId "Parent/0" - no
            // existing coded children, no handle. A new snapshot child forces the Reconciler to
            // introduce a handle for it and re-key the parent's map entry.
            var parent = new GameObjectNode { LogicalId = "Parent/0", Name = "Parent" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { parent } };

            var snapshotNewChild = new SnapshotNode { GlobalObjectId = "goid-new-child", Name = "NewChild" };
            var snapshotParent = new SnapshotNode
            {
                GlobalObjectId = "goid-parent",
                Name = "Parent",
                Children = new[] { snapshotNewChild },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotParent } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "Parent/0", GlobalObjectId = "goid-parent", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.IsType<AppendStatement>(Assert.Single(result.Patch.Edits));
            Assert.Equal("Parent/0", append.ParentAnchor);
            Assert.Equal("NewChild", append.Name);
            Assert.Equal("parent", append.ParentHandle);
            Assert.True(append.IntroduceParentHandle);
            Assert.Equal("parent/NewChild/0", append.NewLogicalId);

            Assert.Contains("Parent/0", result.RemovedLogicalIds);

            Assert.Contains(
                result.AddedEntries,
                e => e.LogicalId == "parent" && e.GlobalObjectId == "goid-parent" && e.ParentLogicalId == null);
            Assert.Contains(
                result.AddedEntries,
                e => e.LogicalId == "parent/NewChild/0" && e.GlobalObjectId == "goid-new-child" && e.ParentLogicalId == "parent");
        }

        [Fact]
        public void Reconcile_GrandchildOfUnmappedNewChild_AppendsUnderIntroducedHandle()
        {
            // "NewChild" is unmapped but its parent "p" IS mapped (b2-t1 appends it). NewChild
            // itself has a create-candidate child ("Grandchild"), so per b2-t3 NewChild now HEADS
            // ITS OWN HANDLE and Grandchild is appended referencing that handle (no longer
            // deferred - supersedes the old "no append" expectation).
            var parent = new GameObjectNode { LogicalId = "p", Name = "Parent" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { parent } };

            var snapshotGrandchild = new SnapshotNode { GlobalObjectId = "goid-grandchild", Name = "Grandchild" };
            var snapshotNewChild = new SnapshotNode
            {
                GlobalObjectId = "goid-new-child",
                Name = "NewChild",
                Children = new[] { snapshotGrandchild },
            };
            var snapshotParent = new SnapshotNode { GlobalObjectId = "goid-parent", Name = "Parent", Children = new[] { snapshotNewChild } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotParent } };

            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = "p", GlobalObjectId = "goid-parent", Kind = "GameObject" } },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var appends = result.Patch.Edits.OfType<AppendStatement>().ToArray();
            Assert.Equal(2, appends.Length);

            var newChildAppend = Assert.Single(appends, a => a.Name == "NewChild");
            Assert.Equal("p", newChildAppend.ParentAnchor);
            Assert.Equal("newChild", newChildAppend.Handle);
            Assert.Equal("newChild", newChildAppend.NewLogicalId);

            var grandchildAppend = Assert.Single(appends, a => a.Name == "Grandchild");
            Assert.Equal("newChild", grandchildAppend.ParentAnchor);
            Assert.Equal("newChild", grandchildAppend.ParentHandle);
            Assert.False(grandchildAppend.IntroduceParentHandle);
            Assert.Null(grandchildAppend.Handle);
            Assert.Equal("newChild/Grandchild/0", grandchildAppend.NewLogicalId);

            Assert.Contains(
                result.AddedEntries,
                e => e.LogicalId == "newChild/Grandchild/0" && e.GlobalObjectId == "goid-grandchild" && e.ParentLogicalId == "newChild");
        }

        [Fact]
        public void Reconcile_NewSubtree_AppendsParentHandleAndChild()
        {
            // Both "Parent" and its child "Child" are brand-new (unmapped, no existing map/model
            // entries at all). Parent heads its own handle (no re-key - it never had a prior id);
            // Child is appended referencing that handle.
            var model = new SceneModel { SchemaVersion = 1, Roots = Array.Empty<GameObjectNode>() };

            var snapshotChild = new SnapshotNode { GlobalObjectId = "goid-child", Name = "Child" };
            var snapshotParent = new SnapshotNode
            {
                GlobalObjectId = "goid-parent",
                Name = "Parent",
                Children = new[] { snapshotChild },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotParent } };

            var map = new IdentityMap { Entries = Array.Empty<IdentityMapEntry>() };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var appends = result.Patch.Edits.OfType<AppendStatement>().ToArray();
            Assert.Equal(2, appends.Length);

            // Parent statement precedes the child statement in edit order.
            var parentAppend = appends[0];
            Assert.Null(parentAppend.ParentAnchor);
            Assert.Equal("Parent", parentAppend.Name);
            Assert.Equal("parent", parentAppend.Handle);
            Assert.Equal("parent", parentAppend.NewLogicalId);
            Assert.False(parentAppend.IntroduceParentHandle);

            var childAppend = appends[1];
            Assert.Equal("parent", childAppend.ParentAnchor);
            Assert.Equal("Child", childAppend.Name);
            Assert.Equal("parent", childAppend.ParentHandle);
            Assert.False(childAppend.IntroduceParentHandle);
            Assert.Null(childAppend.Handle);
            Assert.Equal("parent/Child/0", childAppend.NewLogicalId);

            Assert.Contains(
                result.AddedEntries,
                e => e.LogicalId == "parent" && e.GlobalObjectId == "goid-parent" && e.ParentLogicalId == null);
            Assert.Contains(
                result.AddedEntries,
                e => e.LogicalId == "parent/Child/0" && e.GlobalObjectId == "goid-child" && e.ParentLogicalId == "parent");

            Assert.Empty(result.RemovedLogicalIds);
        }

        [Fact]
        public void Reconcile_CreatedObjectWithComponents_AppendsAndReportsComponents()
        {
            var existingRoot = new GameObjectNode { LogicalId = "root-1", Name = "Existing" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { existingRoot } };

            var snapshotExisting = new SnapshotNode { GlobalObjectId = "goid-existing", Name = "Existing" };
            var snapshotNewWithComponents = new SnapshotNode
            {
                GlobalObjectId = "goid-new-with-components",
                Name = "NewWithComponents",
                Components = new[] { new ComponentData { Type = new TypeRef("UnityEngine.Rigidbody") } },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotExisting, snapshotNewWithComponents } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-existing", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            // §13 one-pass attach: owner append (with a Handle so the component can reference it)
            // PLUS the component attach, in the same pass - components are never dropped silently
            // and never deferred behind a conflict.
            var append = Assert.Single(result.Patch.Edits.OfType<AppendStatement>());
            Assert.Equal("NewWithComponents", append.Name);
            Assert.NotNull(append.Handle);

            var attach = Assert.Single(result.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.Equal(append.NewLogicalId, attach.Anchor);
            Assert.Equal(append.Handle, attach.OwnerHandle);
            Assert.Equal("UnityEngine.Rigidbody", attach.TypeFullName);

            var ownerEntry = Assert.Single(result.AddedEntries, e => e.Kind == "GameObject");
            Assert.Equal(append.NewLogicalId, ownerEntry.LogicalId);
            Assert.Equal("goid-new-with-components", ownerEntry.GlobalObjectId);

            var componentEntry = Assert.Single(result.AddedEntries, e => e.Kind == "Component");
            Assert.Equal($"{append.NewLogicalId}/UnityEngine.Rigidbody#0", componentEntry.LogicalId);
            Assert.Equal(append.NewLogicalId, componentEntry.ParentLogicalId);

            // The retired report-only path never fires for a representable component; the
            // component is attached, not deferred behind a conflict.
            Assert.Empty(result.Conflicts);
        }

        [Fact]
        public void Reconcile_CreatedObjectWithoutComponents_ReportsNoUnrepresentedComponentsConflict()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = Array.Empty<GameObjectNode>() };

            var snapshotNew = new SnapshotNode { GlobalObjectId = "goid-new", Name = "New" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotNew } };

            var map = new IdentityMap { Entries = Array.Empty<IdentityMapEntry>() };

            var result = Reconciler.Reconcile(model, snapshot, map);

            // No components on the created object -> no component edits and no conflicts at all.
            Assert.Empty(result.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.Empty(result.Conflicts);
        }

        [Fact]
        public void Reconcile_DeletedObject_RemovesStatement_AndDropsMapEntry()
        {
            var kept = new GameObjectNode { LogicalId = "kept-1", Name = "Kept" };
            var gone = new GameObjectNode { LogicalId = "gone-1", Name = "Gone" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { kept, gone } };

            var snapshotKept = new SnapshotNode { GlobalObjectId = "goid-kept", Name = "Kept" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotKept } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "kept-1", GlobalObjectId = "goid-kept", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "gone-1", GlobalObjectId = "goid-gone", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var remove = Assert.IsType<RemoveStatement>(Assert.Single(result.Patch.Edits));
            Assert.Equal("gone-1", remove.Anchor);
            Assert.Equal("gone-1", Assert.Single(result.RemovedLogicalIds));
        }

        [Fact]
        public void Reconcile_MappedObjectStillInSnapshot_ProducesNoRemoveStatement()
        {
            var kept = new GameObjectNode { LogicalId = "kept-1", Name = "Kept" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { kept } };

            var snapshotKept = new SnapshotNode { GlobalObjectId = "goid-kept", Name = "Kept" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotKept } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "kept-1", GlobalObjectId = "goid-kept", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Patch.Edits);
            Assert.Empty(result.RemovedLogicalIds);
        }
    }
}
