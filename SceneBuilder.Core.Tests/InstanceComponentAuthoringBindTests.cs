using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Compile-proofs for the `.Component<T>()` alias on InstanceHandle / InstanceHandle<TRef>,
    // against the REAL com.codescenes/Runtime scaffolding: it must exist, both bare and with a
    // configure closure, and keep returning the handle so a further instance verb can chain onto
    // it -- exactly like AddComponent<T> already does.
    public class InstanceComponentAuthoringBindTests
    {
        private const string RigidbodyStub = "namespace UnityEngine { public class Rigidbody : Component { } }";

        private const string CapturedBareSource = @"using SceneBuilder.Authoring;
using UnityEngine;
public class CapturedBareComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var ball = scene.Instance(""Assets/Prefabs/Ball.prefab"");
        ball.Component<Rigidbody>();
    }
}";

        [Fact]
        public void CapturedInstance_BareComponentAlias_CompilesAgainstRealAuthoringSurface()
        {
            var errors = AuthoringBindHarness.BindErrors(CapturedBareSource, RigidbodyStub);

            Assert.Empty(errors);
        }

        private const string CapturedConfigureAndChainSource = @"using SceneBuilder.Authoring;
using UnityEngine;
public class CapturedConfigureComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var ball = scene.Instance(""Assets/Prefabs/Ball.prefab"");
        ball.Component<Rigidbody>(c => { }).RemoveComponent<Rigidbody>();
    }
}";

        // The alias must keep returning the handle -- proven by chaining a second instance verb
        // (RemoveComponent) straight off its result, the same way AddComponent<T> already allows.
        [Fact]
        public void CapturedInstance_ConfigureComponentAlias_ReturnsHandleForChaining()
        {
            var errors = AuthoringBindHarness.BindErrors(CapturedConfigureAndChainSource, RigidbodyStub);

            Assert.Empty(errors);
        }

        private const string TypedHandleInlineSource = @"using SceneBuilder.Authoring;
using UnityEngine;
public class TypedHandleComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank).Component<Rigidbody>();
    }
}";

        // Same alias on the typed InstanceHandle<TRef> returned by Instance<TRef>(...).
        [Fact]
        public void TypedInstanceHandle_ComponentAlias_CompilesAgainstRealAuthoringSurface()
        {
            var errors = AuthoringBindHarness.BindErrors(
                TypedHandleInlineSource, AuthoringBindHarness.DoorOpenerStubs + RigidbodyStub);

            Assert.Empty(errors);
        }
    }
}
