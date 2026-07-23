using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Plan;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b5-t1: owns spec test #7 — the whole loop composes to identity. One direction (Materialize)
    // pushes an authored PrefabInstanceNode's overrides/added-component/removed-component/
    // objectReference-override onto a vanilla snapshot; the resulting Plan's effects are simulated
    // onto a new snapshot; the other direction (Reconcile) is then run against the ORIGINAL
    // (override-stripped) source, and the resulting source patch is applied + reparsed. The
    // reparsed model must carry the same overrides/added/removed components as the originally
    // authored one. Excludes child-GameObjects (deferred per DESCRIPTION).
    public class PrefabInstanceRoundTripIntegrationTests
    {
        private const string PrefabGuid = "guid-enemy-prefab";
        private const string PrefabPath = "Assets/Prefabs/Enemy.prefab";

        private const string RoundTripFixture = @"
public class RoundTripScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
    }
}
";

        [Fact]
        public void FullLoop_ModelToPlanToSimulatedSnapshotToReconcile_ReproducesOriginalOverrides()
        {
            // ---- Arrange: the override-stripped baseline, as it exists in source today ----------
            var parsedBase = BuilderParser.Parse(RoundTripFixture);
            var baseInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(parsedBase.Model.Roots));

            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "enemy", GlobalObjectId = "goid-enemy", Kind = "PrefabInstance",
                        SourcePrefabGuid = PrefabGuid,
                    },
                },
                Assets = new[] { new AssetEntry { Guid = PrefabGuid, LastKnownPath = PrefabPath, TypeHint = "GameObject" } },
            };

            // ---- Arrange: the desired model — same instance, now carrying overrides -------------
            var scalarTarget = new OverrideTarget { ComponentType = "UnityEngine.BoxCollider" };
            var scalarOverride = new PropertyOverride
            {
                Target = scalarTarget,
                PropertyPath = "member:health",
                Value = ValueNode.Primitive.Int(50),
            };

            var materialTarget = new OverrideTarget { ComponentType = "UnityEngine.MeshRenderer" };
            // No Guid: an authored `.Override(... Asset("path"))` call never carries one in source —
            // the GUID is adapter-resolved on sync, not stored literally in code.
            var materialRef = new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/Materials/Enemy.mat" });
            var objectRefOverride = new PropertyOverride
            {
                Target = materialTarget,
                PropertyPath = "member:material",
                Value = new ValueNode.Unsupported(""),
                ObjectReference = materialRef,
            };

            var addedComponent = new AddedComponent
            {
                Target = new OverrideTarget(),
                Component = new ComponentData { LogicalId = "enemy/UnityEngine.Light#0", Type = new TypeRef("UnityEngine.Light") },
            };

            var removedTarget = new OverrideTarget { ComponentType = "UnityEngine.AudioSource" };

            var desiredInstance = baseInstance with
            {
                Overrides = new[] { scalarOverride, objectRefOverride },
                AddedComponents = new[] { addedComponent },
                RemovedComponents = new[] { removedTarget },
            };
            var desiredModel = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { desiredInstance } };

            var vanillaSnapshotNode = new SnapshotNode
            {
                GlobalObjectId = "goid-enemy",
                Name = "Enemy",
                SourcePrefabGuid = PrefabGuid,
            };
            var snapshotBeforeApply = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { vanillaSnapshotNode } };

            // ---- Act 1: Materialize the desired model onto the vanilla scene --------------------
            var plan = Materializer.Materialize(desiredModel, snapshotBeforeApply, map);

            Assert.Empty(plan.Ops.OfType<InstantiatePrefab>());
            Assert.Equal(2, plan.Ops.OfType<SetInstanceOverride>().Count());
            Assert.Single(plan.Ops.OfType<AddInstanceComponent>());
            Assert.Single(plan.Ops.OfType<RemoveInstanceComponent>());

            // ---- Act 2: simulate the Plan's effects landing in the live scene -------------------
            var appliedOverrides = plan.Ops.OfType<SetInstanceOverride>()
                .Select(op => new PropertyOverride { Target = op.Target, PropertyPath = op.PropertyPath, Value = op.Value, ObjectReference = op.ObjectReference })
                .ToArray();
            var appliedAddedComponents = plan.Ops.OfType<AddInstanceComponent>()
                .Select(op => new AddedComponent { Target = op.Target, Component = op.Component })
                .ToArray();
            var appliedRemovedComponents = plan.Ops.OfType<RemoveInstanceComponent>()
                .Select(op => op.Target)
                .ToArray();

            var snapshotAfterApplyNode = vanillaSnapshotNode with
            {
                Overrides = appliedOverrides,
                AddedComponents = appliedAddedComponents,
                RemovedComponents = appliedRemovedComponents,
            };
            var snapshotAfterApply = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotAfterApplyNode } };

            // ---- Act 3: Reconcile the simulated snapshot against the ORIGINAL (stripped) source -
            var result = Reconciler.Reconcile(parsedBase.Model, snapshotAfterApply, map, parsedBase.Anchors);

            Assert.Empty(result.Conflicts);

            var patchedSource = SourcePatchApplier.Apply(RoundTripFixture, result.Patch, parsedBase.Anchors);
            var reparsed = BuilderParser.Parse(patchedSource);
            var reconciledInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));

            // ---- Assert: the reconciled model reproduces every authored override ----------------
            Assert.Equal(2, reconciledInstance.Overrides.Length);

            var reconciledScalar = reconciledInstance.Overrides[0];
            Assert.Equal(scalarOverride.Target, reconciledScalar.Target);
            Assert.Equal(scalarOverride.PropertyPath, reconciledScalar.PropertyPath);
            Assert.Equal(scalarOverride.Value, reconciledScalar.Value);
            Assert.Null(reconciledScalar.ObjectReference);

            var reconciledObjectRef = reconciledInstance.Overrides[1];
            Assert.Equal(objectRefOverride.Target, reconciledObjectRef.Target);
            Assert.Equal(objectRefOverride.PropertyPath, reconciledObjectRef.PropertyPath);
            Assert.Equal(objectRefOverride.ObjectReference, reconciledObjectRef.ObjectReference);

            var reconciledAdded = Assert.Single(reconciledInstance.AddedComponents);
            Assert.Equal(addedComponent.Target, reconciledAdded.Target);
            Assert.Equal(addedComponent.Component.Type.FullName, reconciledAdded.Component.Type.FullName);

            var reconciledRemoved = Assert.Single(reconciledInstance.RemovedComponents);
            Assert.Equal(removedTarget, reconciledRemoved);
        }
    }
}
