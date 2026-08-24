using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A live component Unity adds because a surviving component declares it required (canonically
    // CanvasRenderer, required by Graphic) must never be planned for removal just because the
    // builder omits it. Differ.Diff / Materializer.Materialize take an injected required-set
    // predicate (Editor-backed by real [RequireComponent] reflection); these fixtures stand the
    // required component in as a plain ComponentData the predicate names as required by a
    // surviving sibling, without any Unity dependency.
    public class RequiredComponentGuardTests
    {
        private const string RequirerType = "Game.Requirer";
        private const string RequiredType = "UnityEngine.CanvasRenderer";

        private static SceneModel DesiredWithRequirer() =>
            new()
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "root-1",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "root-1/Game.Requirer#0", Type = new TypeRef(RequirerType) },
                        },
                    },
                },
            };

        private static SceneModel DesiredWithoutRequirer() =>
            new()
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode { LogicalId = "root-1", Name = "Root" },
                },
            };

        private static SceneSnapshot ActualWithBothComponents() =>
            new()
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { Type = new TypeRef(RequirerType) },
                            new ComponentData { Type = new TypeRef(RequiredType) },
                        },
                    },
                },
            };

        // Both components carry a managed identity-map entry -- the removal WOULD fire for either
        // absent the guard (Differ.cs:378's managed gate is satisfied for both).
        private static IdentityMap ManagedMap() =>
            new()
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = "root-1/Game.Requirer#0",
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = RequirerType,
                        ParentLogicalId = "root-1",
                    },
                    new IdentityMapEntry
                    {
                        LogicalId = "root-1/UnityEngine.CanvasRenderer#0",
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = RequiredType,
                        ParentLogicalId = "root-1",
                    },
                },
            };

        private static ISet<string> RequiredSetOf(TypeRef type) =>
            type.FullName == RequirerType ? new HashSet<string> { RequiredType } : new HashSet<string>();

        [Fact]
        public void Diff_RequirerSurvivesInDesired_SuppressesRemoveOfRequiredComponent()
        {
            var changeSet = Differ.Diff(DesiredWithRequirer(), ActualWithBothComponents(), ManagedMap(), RequiredSetOf);

            Assert.DoesNotContain(
                changeSet.Ops.OfType<RemoveComponent>(),
                op => op.ComponentType.FullName == RequiredType);
        }

        [Fact]
        public void Diff_RequirerDroppedFromDesired_EmitsExactlyOneRemoveOfRequiredComponent()
        {
            var changeSet = Differ.Diff(DesiredWithoutRequirer(), ActualWithBothComponents(), ManagedMap(), RequiredSetOf);

            var removedRequired = Assert.Single(
                changeSet.Ops.OfType<RemoveComponent>(),
                op => op.ComponentType.FullName == RequiredType);
            Assert.Equal("root-1/UnityEngine.CanvasRenderer#0", removedRequired.ComponentLogicalId);
        }

        [Fact]
        public void Diff_NullPredicate_ReproducesTodayBehavior_RemovesRequiredComponent()
        {
            var changeSet = Differ.Diff(DesiredWithRequirer(), ActualWithBothComponents(), ManagedMap());

            Assert.Contains(
                changeSet.Ops.OfType<RemoveComponent>(),
                op => op.ComponentType.FullName == RequiredType);
        }

        [Fact]
        public void Materialize_RequirerSurvivesInDesired_SuppressesRemovePlanOp()
        {
            var plan = Materializer.Materialize(DesiredWithRequirer(), ActualWithBothComponents(), ManagedMap(), RequiredSetOf);

            Assert.DoesNotContain(
                plan.Ops,
                op => op is SceneBuilder.Core.Plan.RemoveComponent rc && rc.LogicalId == "root-1/UnityEngine.CanvasRenderer#0");
        }

        [Fact]
        public void Materialize_RequirerDroppedFromDesired_EmitsRemovePlanOpForRequiredComponent()
        {
            var plan = Materializer.Materialize(DesiredWithoutRequirer(), ActualWithBothComponents(), ManagedMap(), RequiredSetOf);

            Assert.Contains(
                plan.Ops,
                op => op is SceneBuilder.Core.Plan.RemoveComponent rc && rc.LogicalId == "root-1/UnityEngine.CanvasRenderer#0");
        }
    }
}
