using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A prefab-instance handle CAPTURED in a variable and used in a later statement accepts every
    // instance verb -- Component, AddComponent, RemoveComponent, Override, On, AddChild,
    // RemoveChild -- exactly like the inline `Instance(...).Verb` chain; none of them are refused
    // as an unsupported builder call. `.Component<T>()` on an instance (inline or captured) lowers
    // onto AddedComponents (the collection BuildInstanceNode actually emits), never the plain-node
    // Components list, which BuildInstanceNode always emits empty for an instance.
    public class InstanceComponentSurfaceTests
    {
        private const string PrefabPath = "Assets/Prefabs/Enemy.prefab";

        private static string Source(string statements) => $@"using SceneBuilder.Authoring;
public class InstanceComponentSurfaceScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{statements}
    }}
}}";

        private static PrefabInstanceNode ParseSingleInstance(string statements, FacadeCatalog? catalog = null) =>
            Assert.IsType<PrefabInstanceNode>(Assert.Single(BuilderParser.Parse(Source(statements), null, catalog).Model.Roots));

        [Fact]
        public void CapturedInstance_Component_LowersToAddedComponentNotNodeComponents()
        {
            var instance = ParseSingleInstance(
                $"        var ball = scene.Instance(\"{PrefabPath}\");\n" +
                "        ball.Component<Rigidbody>();");

            var added = Assert.Single(instance.AddedComponents);
            Assert.Equal("Rigidbody", added.Component.Type.FullName);
            Assert.Empty(instance.Components);
        }

        [Fact]
        public void InlineInstance_Component_LowersToAddedComponent()
        {
            var instance = ParseSingleInstance(
                $"        scene.Instance(\"{PrefabPath}\").Component<Rigidbody>();");

            var added = Assert.Single(instance.AddedComponents);
            Assert.Equal("Rigidbody", added.Component.Type.FullName);
        }

        [Fact]
        public void CapturedInstance_AddComponent_LowersToAddedComponent()
        {
            var instance = ParseSingleInstance(
                $"        var ball = scene.Instance(\"{PrefabPath}\");\n" +
                "        ball.AddComponent<Rigidbody>();");

            var added = Assert.Single(instance.AddedComponents);
            Assert.Equal("Rigidbody", added.Component.Type.FullName);
        }

        [Fact]
        public void CapturedInstance_ComponentConfigureClosure_SetsBothFields()
        {
            var instance = ParseSingleInstance(
                $"        var ball = scene.Instance(\"{PrefabPath}\");\n" +
                "        ball.Component<SphereCollider>(c => { c.Set(\"m_Radius\", 2f); c.Set(\"m_IsTrigger\", true); });");

            var added = Assert.Single(instance.AddedComponents);
            Assert.Equal(2, added.Component.Fields.Count);
        }

        [Fact]
        public void CapturedInstance_Override_LowersToRootOverride()
        {
            var instance = ParseSingleInstance(
                $"        var ball = scene.Instance(\"{PrefabPath}\");\n" +
                "        ball.Override(e => e.Set<Rigidbody>(\"m_Mass\", 2f));");

            var propertyOverride = Assert.Single(instance.Overrides);
            Assert.Equal("m_Mass", propertyOverride.PropertyPath);
        }

        [Fact]
        public void CapturedInstance_RemoveComponent_LowersToRemovedComponents()
        {
            var instance = ParseSingleInstance(
                $"        var ball = scene.Instance(\"{PrefabPath}\");\n" +
                "        ball.RemoveComponent<BoxCollider>();");

            var removed = Assert.Single(instance.RemovedComponents);
            Assert.Equal("BoxCollider", removed.ComponentType);
        }

        [Fact]
        public void CapturedInstance_AddChild_LowersToAddedGameObjects()
        {
            var instance = ParseSingleInstance(
                $"        var ball = scene.Instance(\"{PrefabPath}\");\n" +
                "        ball.AddChild(\"\", \"Muzzle\");");

            var addedChild = Assert.Single(instance.AddedGameObjects);
            Assert.Equal("Muzzle", addedChild.Node.Name);
        }

        [Fact]
        public void CapturedInstance_RemoveChild_LowersToRemovedGameObjects()
        {
            var instance = ParseSingleInstance(
                $"        var ball = scene.Instance(\"{PrefabPath}\");\n" +
                "        ball.RemoveChild(\"Turret\");");

            var removedChild = Assert.Single(instance.RemovedGameObjects);
            Assert.Equal("Turret", removedChild.ChildPath);
        }

        [Fact]
        public void CapturedInstance_On_LowersToScopedOverride()
        {
            var catalog = new FacadeCatalog
            {
                Entries = new[]
                {
                    new FacadeEntry
                    {
                        PropertyName = "Enemy",
                        Guid = "guid-enemy",
                        Root = new FacadeNode
                        {
                            Children = new[]
                            {
                                new FacadeChild { PropertyName = "Turret", RealName = "Turret", LocalId = 1 },
                            },
                        },
                    },
                },
            };

            var instance = ParseSingleInstance(
                "        var ball = scene.Instance(Prefabs.Enemy);\n" +
                "        ball.On(t => t.Turret, b => { });",
                catalog);

            Assert.Single(instance.ScopedOverrides!);
        }

        // Unchanged: a verb reserved for an instance receiver is still refused on a plain node.
        [Fact]
        public void NodeReceiver_AddComponent_StillThrowsUnsupportedBuilderCall()
        {
            var ex = Assert.Throws<ParseException>(() => BuilderParser.Parse(Source(
                "        var n = scene.Add(\"A\");\n" +
                "        n.AddComponent<Rigidbody>();")));

            Assert.Contains("Unsupported builder call '.AddComponent(...)'", ex.Message);
        }
    }
}
