using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class MaterializerTests
    {
        [Fact]
        public void Materialize_OrdersParentsBeforeChildren_AndParentingAfterCreation()
        {
            var child = new GameObjectNode { LogicalId = "child-1", Name = "Child" };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Children = new[] { child } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };
            var map = new IdentityMap { Scene = "Assets/Scenes/Demo.unity" };

            var plan = Materializer.Materialize(model, snapshot, map);

            var ops = plan.Ops;
            var rootCreateIndex = System.Array.FindIndex(ops, op => op is CreateObject co && co.LogicalId == "root-1");
            var childCreateIndex = System.Array.FindIndex(ops, op => op is CreateObject co && co.LogicalId == "child-1");
            var lastCreateIndex = ops
                .Select((op, i) => (op, i))
                .Where(t => t.op is CreateObject)
                .Select(t => t.i)
                .DefaultIfEmpty(-1)
                .Max();
            var firstSetParentIndex = System.Array.FindIndex(ops, op => op is SetParent);

            Assert.True(rootCreateIndex >= 0, "root CreateObject op missing");
            Assert.True(childCreateIndex >= 0, "child CreateObject op missing");
            Assert.True(rootCreateIndex < childCreateIndex, "parent must be created before child");
            Assert.True(firstSetParentIndex >= 0, "expected a SetParent op for the child");
            Assert.True(firstSetParentIndex > lastCreateIndex, "SetParent must come after every CreateObject");

            var childSetParent = Assert.Single(ops.OfType<SetParent>(), op => op.LogicalId == "child-1");
            Assert.Equal("root-1", childSetParent.ParentLogicalId);
        }

        [Fact]
        public void Materialize_LowersTransform_ToConstrainedSetFieldPaths()
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Transform = transform };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var driftedTransform = transform with { Position = new Vec3(9, 9, 9) };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = driftedTransform };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);

            var setFields = plan.Ops.OfType<SetField>().Where(op => op.LogicalId == "root-1").ToArray();

            Assert.Equal(3, setFields.Length);
            Assert.Equal(new[] { "m_LocalPosition", "m_LocalRotation", "m_LocalScale" }, setFields.Select(f => f.Path));

            var position = Assert.IsType<Vec3Value>(setFields[0].Value);
            Assert.Equal(new Vec3(1, 2, 3), position.Value);

            Assert.IsType<QuatValue>(setFields[1].Value);
            Assert.IsType<Vec3Value>(setFields[2].Value);

            Assert.DoesNotContain(plan.Ops.OfType<SetField>(), op => op.Path != "m_LocalPosition" && op.Path != "m_LocalRotation" && op.Path != "m_LocalScale");
        }
    }
}
