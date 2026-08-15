using System.Collections.Generic;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // From-scratch DetectAppends emits a parent's component-append BEFORE recursing into its
    // children, so a same-batch node whose component references its own (also same-batch) target
    // is written above that target's declaration. This drops the field as an unresolvable Pending
    // reference and, independently, mis-derives the referencing owner's own receiver name when
    // that owner heads a handle and has children (the owner's real handle is never registered
    // against its own logical id, so the deriver re-mints a numeric-suffixed duplicate).
    public class FromScratchForwardReferenceEmitTests
    {
        private const string DoorOpenerType = "Game.DoorOpener";
        private const string TargetField = "target";

        private const string EmptyScene = @"using SceneBuilder.Authoring;
public class EmptyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
    }
}
";

        // A same-batch parent's own component references its own same-batch child. The child's
        // Add must land above the referencing component, the child must acquire its own handle, the
        // parent's receiver must be its real derived handle (not a numeric-suffixed duplicate), and
        // the applied source must compile with zero Roslyn errors in one sync.
        [Fact]
        public void Reconcile_ParentComponentReferencesOwnChild_ChildAddedAboveComponent_CompilesCleanly()
        {
            var parsed = BuilderParser.Parse(EmptyScene);
            var map = new IdentityMap { Entries = System.Array.Empty<IdentityMapEntry>() };

            const string childGoid = "goid-child";

            var parentFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(TargetField, new ValueNode.ObjectRef(childGoid)),
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-parent",
                        Name = "Parent",
                        Components = new[]
                        {
                            new ComponentData { Type = new TypeRef(DoorOpenerType), Fields = parentFields },
                        },
                        Children = new[]
                        {
                            new SnapshotNode { GlobalObjectId = childGoid, Name = "Child" },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, handles: parsed.Handles);

            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(EmptyScene, result.Patch, parsed.Anchors);

            Assert.DoesNotContain("parent2", patched);
            Assert.Contains(".Set(\"target\", child)", patched);

            var childDeclIndex = patched.IndexOf("parent.Add(\"Child\")", System.StringComparison.Ordinal);
            var componentIndex = patched.IndexOf(".Set(\"target\", child)", System.StringComparison.Ordinal);
            Assert.True(childDeclIndex >= 0, "expected the child's own Add statement in applied source:\n" + patched);
            Assert.True(
                childDeclIndex < componentIndex,
                "child's declaration must precede the referencing component:\n" + patched);

            var errors = AuthoringBindHarness.BindErrors(patched, AuthoringBindHarness.DoorOpenerStubs);
            Assert.Empty(errors);
        }

        // Two brand-new roots in the same batch cross-reference: A's component targets B, and both
        // are unmapped create candidates. The reference must resolve in this ONE sync (never dropped
        // as Pending), both nodes must acquire handles, and the applied source must compile.
        [Fact]
        public void Reconcile_TwoNewRootsCrossReference_ResolvesInOneSync_CompilesCleanly()
        {
            var parsed = BuilderParser.Parse(EmptyScene);
            var map = new IdentityMap { Entries = System.Array.Empty<IdentityMapEntry>() };

            const string bGoid = "goid-bbb";

            var aFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(TargetField, new ValueNode.ObjectRef(bGoid)),
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-aaa",
                        Name = "Aaa",
                        Components = new[]
                        {
                            new ComponentData { Type = new TypeRef(DoorOpenerType), Fields = aFields },
                        },
                    },
                    new SnapshotNode { GlobalObjectId = bGoid, Name = "Bbb" },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, handles: parsed.Handles);

            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(EmptyScene, result.Patch, parsed.Anchors);

            Assert.Contains(".Set(\"target\", bbb)", patched);

            var bDeclIndex = patched.IndexOf("scene.Add(\"Bbb\")", System.StringComparison.Ordinal);
            var componentIndex = patched.IndexOf(".Set(\"target\", bbb)", System.StringComparison.Ordinal);
            Assert.True(bDeclIndex >= 0, "expected Bbb's own Add statement in applied source:\n" + patched);
            Assert.True(
                bDeclIndex < componentIndex,
                "Bbb's declaration must precede the referencing component:\n" + patched);

            var errors = AuthoringBindHarness.BindErrors(patched, AuthoringBindHarness.DoorOpenerStubs);
            Assert.Empty(errors);
        }

        // The reverse direction of the cross-reference above: B (created second) references A
        // (created first, already earlier in the batch). No hoist is needed, but the reference must
        // still resolve in one sync rather than dropping as Pending.
        [Fact]
        public void Reconcile_SecondRootReferencesFirstRoot_ResolvesInOneSync_CompilesCleanly()
        {
            var parsed = BuilderParser.Parse(EmptyScene);
            var map = new IdentityMap { Entries = System.Array.Empty<IdentityMapEntry>() };

            const string aGoid = "goid-aaa";

            var bFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(TargetField, new ValueNode.ObjectRef(aGoid)),
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = aGoid, Name = "Aaa" },
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-bbb",
                        Name = "Bbb",
                        Components = new[]
                        {
                            new ComponentData { Type = new TypeRef(DoorOpenerType), Fields = bFields },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, handles: parsed.Handles);

            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(EmptyScene, result.Patch, parsed.Anchors);

            Assert.Contains(".Set(\"target\", aaa)", patched);

            var errors = AuthoringBindHarness.BindErrors(patched, AuthoringBindHarness.DoorOpenerStubs);
            Assert.Empty(errors);
        }
    }
}
