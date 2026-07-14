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
    // m3-handleless-component-attach b1-t1: BuilderParser exposes authored `var x = ...` handles
    // via ParseResult.Handles (logicalId -> handle name), so downstream (reconcile/apply) can
    // distinguish handle-full owners from handle-less ones. Closure-parameter transient bindings
    // (e.g. the `m` in `scene.Add("X", m => ...)`) are NOT authored handles and must be omitted
    // even though they let source refer to the node by name inside the closure.
    public class HandleLessAttachTests
    {
        private const string MixedHandleScene = @"
public class MixedHandleScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var weapon = scene.Add(""Weapon"");
        scene.Add(""Enemy"", m => { m.Tag(""Foe""); });
    }
}
";

        [Fact]
        public void Parse_AuthoredVarHandle_AppearsInHandles_ClosureParamNodeOmitted()
        {
            var parsed = BuilderParser.Parse(MixedHandleScene);

            // "weapon" is an authored `var` handle -> must be present, keyed by its final LogicalId.
            Assert.Single(parsed.Handles);
            Assert.True(parsed.Handles.ContainsKey("weapon"));
            Assert.Equal("weapon", parsed.Handles["weapon"]);

            // The "Enemy" node is only reachable inside its own closure via `m` (a transient
            // closure-parameter binding, not an authored handle) — its LogicalId must not appear.
            Assert.DoesNotContain(parsed.Handles.Keys, key => key.StartsWith("Enemy"));
        }

        // ---- b1-t2: Reconciler synthesizes a handle for handle-less mapped owners ----

        private static IdentityMap MapWithMappedRoot(string logicalId) => new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = logicalId, GlobalObjectId = "goid-weapon", Kind = "GameObject" },
            },
        };

        private static SceneModel ModelWithRoot(string logicalId, string name) => new SceneModel
        {
            SchemaVersion = 1,
            Roots = new[] { new GameObjectNode { LogicalId = logicalId, Name = name } },
        };

        // Owner LogicalId absent from `handles` (handle-less, e.g. bare `scene.Add("Weapon");`)
        // with two snapshot components to add: both AppendComponentStatement share ONE synthesized
        // OwnerHandle, and only the first carries IntroduceOwnerHandle=true.
        [Fact]
        public void Reconcile_ComponentsOnHandleLessOwner_SynthesizesSharedHandle_FirstIntroduces()
        {
            var model = ModelWithRoot("Weapon/0", "Weapon");

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-weapon",
                        Name = "Weapon",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused-rb", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = FieldMap.Empty },
                            new ComponentData { LogicalId = "unused-bc", Type = new TypeRef("UnityEngine.BoxCollider"), Fields = FieldMap.Empty },
                        },
                    },
                },
            };

            var map = MapWithMappedRoot("Weapon/0");
            var handles = new Dictionary<string, string>(); // non-null, owner NOT present -> handle-less.

            var result = Reconciler.Reconcile(model, snapshot, map, handles: handles);

            var appends = result.Patch.Edits.OfType<AppendComponentStatement>().ToArray();
            Assert.Equal(2, appends.Length);

            Assert.NotNull(appends[0].OwnerHandle);
            Assert.Equal(appends[0].OwnerHandle, appends[1].OwnerHandle);
            Assert.True(appends[0].IntroduceOwnerHandle);
            Assert.False(appends[1].IntroduceOwnerHandle);
        }

        // Owner LogicalId IS a key in `handles` (handle-full, e.g. `var weapon = scene.Add(...)`):
        // no synthesis, today's OwnerHandle=null/IntroduceOwnerHandle=false shape is unchanged.
        [Fact]
        public void Reconcile_ComponentOnHandledOwner_OwnerPresentInHandles_NoSynthesize()
        {
            var model = ModelWithRoot("Weapon/0", "Weapon");

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-weapon",
                        Name = "Weapon",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused-rb", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = FieldMap.Empty },
                        },
                    },
                },
            };

            var map = MapWithMappedRoot("Weapon/0");
            var handles = new Dictionary<string, string> { ["Weapon/0"] = "weapon" };

            var result = Reconciler.Reconcile(model, snapshot, map, handles: handles);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.Null(append.OwnerHandle);
            Assert.False(append.IntroduceOwnerHandle);
        }

        // ---- b1-t3: Applier sequences multiple components on a just-introduced owner + end-to-end round-trip ----

        private const string HandleLessWeaponScene = @"
public class HandleLessWeaponScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Weapon"");
    }
}
";

        private const string HandledWeaponScene = @"
public class HandledWeaponScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var weapon = scene.Add(""Weapon"");
    }
}
";

        private static IdentityMap MapWithMappedRoot2(string logicalId) => new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = logicalId, GlobalObjectId = "goid-weapon", Kind = "GameObject" },
            },
        };

        // Sole root is a bare `scene.Add("Weapon");` (handle-less). One snapshot component to
        // add. End-to-end: parse -> Reconcile(handles: parsed.Handles) -> Apply must not throw,
        // must introduce a handle declaration and the component call, and must converge to an
        // EMPTY Materialize plan on re-parse.
        [Fact]
        public void Reconcile_ComponentOnHandleLessOwner_IntroducesHandleAndConverges()
        {
            var parsed = BuilderParser.Parse(HandleLessWeaponScene);
            var map = MapWithMappedRoot2(Assert.Single(parsed.Model.Roots).LogicalId);

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-weapon",
                        Name = "Weapon",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused-rb",
                                Type = new TypeRef("UnityEngine.Rigidbody"),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)),
                                }),
                            },
                        },
                    },
                },
            };

            var recon = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, handles: parsed.Handles);

            Assert.NotEmpty(recon.Patch.Edits);
            Assert.Empty(recon.Conflicts);

            var patched = SourcePatchApplier.Apply(HandleLessWeaponScene, recon.Patch, parsed.Anchors);

            Assert.Contains("var ", patched);
            Assert.Contains(".Component<UnityEngine.Rigidbody>", patched);

            // Introducing a handle changes the owner's LogicalId (was the synthesized "Weapon/0",
            // now the authored handle name) -- re-derive the identity map from the REPARSED node's
            // actual LogicalId rather than reusing the stale pre-patch `map`. Mapping that LogicalId
            // continuity across a handle-introducing patch is an adapter-level concern, out of this
            // Core task's scope; the convergence oracle here only needs a CORRECT post-patch map.
            var reparsed = BuilderParser.Parse(patched);
            var reparsedMap = MapWithMappedRoot2(Assert.Single(reparsed.Model.Roots).LogicalId);
            var plan = Materializer.Materialize(reparsed.Model, snapshot, reparsedMap);

            Assert.Empty(plan.Ops);
        }

        // Same handle-less owner, but TWO snapshot components to add. This is the case that
        // specifically exercises the 2nd+ same-batch sequencing this task adds: after the first
        // component introduces the owner's handle, the second must be sequenced after it (not
        // hit the "has no handle variable" throw).
        [Fact]
        public void Reconcile_MultipleComponentsOnHandleLessOwner_AllAttachAndConverge()
        {
            var parsed = BuilderParser.Parse(HandleLessWeaponScene);
            var map = MapWithMappedRoot2(Assert.Single(parsed.Model.Roots).LogicalId);

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-weapon",
                        Name = "Weapon",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused-rb", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = FieldMap.Empty },
                            new ComponentData { LogicalId = "unused-bc", Type = new TypeRef("UnityEngine.BoxCollider"), Fields = FieldMap.Empty },
                        },
                    },
                },
            };

            var recon = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, handles: parsed.Handles);

            Assert.NotEmpty(recon.Patch.Edits);
            Assert.Empty(recon.Conflicts);

            var patched = SourcePatchApplier.Apply(HandleLessWeaponScene, recon.Patch, parsed.Anchors);

            Assert.Contains(".Component<UnityEngine.Rigidbody>", patched);
            Assert.Contains(".Component<UnityEngine.BoxCollider>", patched);

            // See note in the single-component test above: re-derive the identity map from the
            // reparsed node's actual (post-handle-introduction) LogicalId.
            var reparsed = BuilderParser.Parse(patched);
            var reparsedMap = MapWithMappedRoot2(Assert.Single(reparsed.Model.Roots).LogicalId);
            var plan = Materializer.Materialize(reparsed.Model, snapshot, reparsedMap);

            Assert.Empty(plan.Ops);
        }

        // Regression: owner already has an authored handle (`var weapon = scene.Add(...)`).
        // Gaining a component must attach through that EXISTING handle — no new `var` is
        // introduced (still exactly one `var weapon =`), and the round trip converges.
        [Fact]
        public void Reconcile_ComponentOnHandledOwner_UsesExistingHandleNoDoubleIntroduce()
        {
            var parsed = BuilderParser.Parse(HandledWeaponScene);
            var map = MapWithMappedRoot2(Assert.Single(parsed.Model.Roots).LogicalId);

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-weapon",
                        Name = "Weapon",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused-rb", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = FieldMap.Empty },
                        },
                    },
                },
            };

            var recon = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, handles: parsed.Handles);

            Assert.NotEmpty(recon.Patch.Edits);
            Assert.Empty(recon.Conflicts);

            var patched = SourcePatchApplier.Apply(HandledWeaponScene, recon.Patch, parsed.Anchors);

            Assert.Equal(1, CountOccurrences(patched, "var weapon ="));
            Assert.Contains(".Component<UnityEngine.Rigidbody>", patched);

            var reparsed = BuilderParser.Parse(patched, map);
            var plan = Materializer.Materialize(reparsed.Model, snapshot, reparsed.IdentityMap);

            Assert.Empty(plan.Ops);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }
    }
}
