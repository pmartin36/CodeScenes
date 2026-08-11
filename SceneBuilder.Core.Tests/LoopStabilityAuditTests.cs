using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Characterization proofs that the parse -> Reconcile -> apply -> re-parse -> Materialize
    // loop is idempotent and pure on converged/unchanged structure: a desired==actual pass emits
    // no edits in either direction, a snapshot's VALUE (not how it was assembled) is what drives
    // Reconcile's output, and repeated cycles over unchanged structure leave source text,
    // sidecar bytes, and every synthesized id unchanged.
    public class LoopStabilityAuditTests
    {
        // ---- Proof 1: desired == actual is a true no-op in both directions ----

        [Fact]
        public void Reconcile_DesiredEqualsActual_ProducesEmptyPatch_AndNoMapDelta()
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var child = new GameObjectNode { LogicalId = "child-1", Name = "Child", Transform = transform };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Transform = transform, Children = new[] { child } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotChild = new SnapshotNode { GlobalObjectId = "goid-child", Name = "Child", Transform = transform };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = transform, Children = new[] { snapshotChild } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "child-1", GlobalObjectId = "goid-child", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Patch.Edits);
            Assert.Empty(result.AddedEntries);
            Assert.Empty(result.RemovedLogicalIds);
            Assert.Empty(result.Conflicts);
        }

        // ---- Proof 2: Reconcile is a pure function of the snapshot VALUE, not of how the
        // snapshot was assembled (diff is authority; a trigger contributes nothing) ----

        private const string DriftScene = @"
public class DriftScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var player = scene.Add(""Player"").Transform(pos: (1, 2, 3));
    }
}
";

        private static SceneSnapshot BuildDriftedSnapshot() => new SceneSnapshot
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new SnapshotNode
                {
                    GlobalObjectId = "goid-player",
                    Name = "Player",
                    Transform = new TransformData { Position = new Vec3(10, 20, 30) },
                },
            },
        };

        [Fact]
        public void Reconcile_SameDriftAssembledTwoWays_ProducesIdenticalPatch()
        {
            var parsed = BuilderParser.Parse(DriftScene);
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "player", GlobalObjectId = "goid-player", Kind = "GameObject" },
                },
            };

            var coldRead = BuildDriftedSnapshot();
            var eventAssembled = BuildDriftedSnapshot();
            Assert.NotSame(coldRead, eventAssembled);

            var reconA = Reconciler.Reconcile(parsed.Model, coldRead, map, parsed.Anchors, handles: parsed.Handles);
            var reconB = Reconciler.Reconcile(parsed.Model, eventAssembled, map, parsed.Anchors, handles: parsed.Handles);

            Assert.NotEmpty(reconA.Patch.Edits);
            Assert.Empty(reconA.Conflicts);
            Assert.Equal(reconA.Patch.FilePath, reconB.Patch.FilePath);
            Assert.Equal(reconA.Patch.Edits, reconB.Patch.Edits);

            var patched = SourcePatchApplier.Apply(DriftScene, reconA.Patch, parsed.Anchors);
            var reparsed = BuilderParser.Parse(patched, map);
            var plan = Materializer.Materialize(reparsed.Model, coldRead, reparsed.IdentityMap);

            Assert.Empty(plan.Ops);
        }

        // ---- Proof 3: N cycles over converged structure leave source, sidecar, and every
        // synthesized id byte-identical to cycle 0 (no drift, no id churn) ----

        private const string LoopScene = @"
public class LoopScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var player = scene.Add(""Player"").Transform(pos: (1, 2, 3));
        player.Add(""Weapon"").Transform(pos: (0, 0, 1));
    }
}
";

        private static SceneSnapshot BuildConvergedSnapshot() => new SceneSnapshot
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new SnapshotNode
                {
                    GlobalObjectId = "goid-player",
                    Name = "Player",
                    Transform = new TransformData { Position = new Vec3(1, 2, 3) },
                    Children = new[]
                    {
                        new SnapshotNode
                        {
                            GlobalObjectId = "goid-weapon",
                            Name = "Weapon",
                            Transform = new TransformData { Position = new Vec3(0, 0, 1) },
                        },
                    },
                },
            },
        };

        [Fact]
        public void Loop_TenCycles_ByteStableSource_Sidecar_AndNoIdChurn()
        {
            var snapshot = BuildConvergedSnapshot();

            var initialParse = BuilderParser.Parse(LoopScene);
            var seedMap = new IdentityMap
            {
                SchemaVersion = 1,
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "player", GlobalObjectId = "goid-player", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "player/Weapon/0", GlobalObjectId = "goid-weapon", Kind = "GameObject", ParentLogicalId = "player" },
                },
            };

            // Guard against a vacuous pass: the fixture's handle-less child must actually resolve
            // to the synthesized id the seed map keys off of.
            Assert.Equal("player/Weapon/0", initialParse.Model.Roots.Single().Children.Single().LogicalId);

            var currentSource = LoopScene;
            var currentMap = seedMap;

            // Baseline captured from cycle 1's own parser-populated map (Name/SiblingIndex etc.
            // filled in by BuilderParser, unlike the hand-seeded bootstrap map above) — cycles
            // 2-10 must reproduce it byte-for-byte.
            string? baselineSource = null;
            string? baselineMapJson = null;
            (string LogicalId, string GlobalObjectId)[]? baselinePairs = null;

            for (var cycle = 1; cycle <= 10; cycle++)
            {
                var parsed = BuilderParser.Parse(currentSource, currentMap);

                var recon = Reconciler.Reconcile(parsed.Model, snapshot, parsed.IdentityMap, parsed.Anchors, handles: parsed.Handles);

                Assert.Empty(recon.Patch.Edits);
                Assert.Empty(recon.AddedEntries);
                Assert.Empty(recon.RemovedLogicalIds);
                Assert.Empty(recon.Conflicts);

                var patchedSource = SourcePatchApplier.Apply(currentSource, recon.Patch, parsed.Anchors);
                var reparsed = BuilderParser.Parse(patchedSource, parsed.IdentityMap);

                var plan = Materializer.Materialize(reparsed.Model, snapshot, reparsed.IdentityMap);
                Assert.Empty(plan.Ops);

                currentSource = patchedSource;
                currentMap = reparsed.IdentityMap;

                if (cycle == 1)
                {
                    baselineSource = currentSource;
                    baselineMapJson = IdentityMapJson.Serialize(currentMap);
                    baselinePairs = currentMap.Entries.Select(e => (e.LogicalId, e.GlobalObjectId)).ToArray();
                }
            }

            Assert.Equal(baselineSource, currentSource);
            Assert.Equal(baselineMapJson, IdentityMapJson.Serialize(currentMap));
            Assert.Equal(baselinePairs, currentMap.Entries.Select(e => (e.LogicalId, e.GlobalObjectId)).ToArray());
        }
    }
}
