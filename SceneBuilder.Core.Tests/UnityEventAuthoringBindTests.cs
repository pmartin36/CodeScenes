using System.Linq;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Compile-proofs for the `.Ref<T>()`/`.OnClick(...)` authoring surface, against the REAL
    // com.codescenes/Runtime scaffolding rather than a re-implementation of C#'s own overload
    // resolution: the void wiring form compiles, configuring a captured `ComponentRef<T>` from
    // outside its own `.Component<T>(...)` closure does not (it exposes no members), and wiring a
    // method the target does not have does not either.
    public class UnityEventAuthoringBindTests
    {
        private const string OnClickSource = @"using SceneBuilder.Authoring;
public class OnClickBindScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Door"").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();
        scene.Add(""QuitButton"").Component<UnityEngine.UI.Button>(b => b.OnClick(opener, o => o.Open()));
    }
}";

        [Fact]
        public void OnClickVoidForm_CompilesAgainstRealAuthoringSurface()
        {
            var errors = AuthoringBindHarness.BindErrors(OnClickSource, AuthoringBindHarness.UnityEventStubs);

            Assert.Empty(errors);
        }

        private const string ConfigureOutsideLambdaSource = @"using SceneBuilder.Authoring;
public class ConfigureOutsideLambdaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Door"").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();
        opener.Set(""m_Speed"", 1f);
    }
}";

        // spec 09:33-34 — a ComponentRef<T> exposes no configuration members, so it can only be
        // configured inside its own Component<T>(...) closure.
        [Fact]
        public void ConfiguringComponentRefOutsideItsOwnClosure_FailsToCompile()
        {
            var errors = AuthoringBindHarness.BindErrors(ConfigureOutsideLambdaSource, AuthoringBindHarness.UnityEventStubs);

            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains("CS1061") && e.Contains("ComponentRef"));
        }

        private const string NoSuchMethodSource = @"using SceneBuilder.Authoring;
public class NoSuchMethodScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Door"").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();
        scene.Add(""QuitButton"").Component<UnityEngine.UI.Button>(b => b.OnClick(opener, o => o.NoSuchMethod()));
    }
}";

        // spec 09:268-269 — wiring a method the target does not have fails to compile; the void
        // form above wiring the same target's real Open() with zero errors is the proof this is
        // not vacuous.
        [Fact]
        public void OnClickWiringNonexistentMethod_FailsToCompile()
        {
            var errors = AuthoringBindHarness.BindErrors(NoSuchMethodSource, AuthoringBindHarness.UnityEventStubs);

            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains("CS1061"));
        }

        private const string CallStateStubs = AuthoringBindHarness.UnityEventStubs + @"
namespace UnityEngine.Events { public enum UnityEventCallState { Off, EditorAndRuntime, RuntimeOnly } }
";

        private const string CallStateSource = @"using SceneBuilder.Authoring;
public class CallStateBindScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Door"").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();
        scene.Add(""QuitButton"").Component<UnityEngine.UI.Button>(b => b.OnClick(opener, o => o.Open(), callState: UnityEngine.Events.UnityEventCallState.EditorAndRuntime));
    }
}";

        // deliverable (i) — the `callState:` named-argument form compiles against the real
        // Runtime authoring surface, not just against Core's syntax-only parser.
        [Fact]
        public void OnClickWithCallStateArgument_CompilesAgainstRealAuthoringSurface()
        {
            var errors = AuthoringBindHarness.BindErrors(CallStateSource, CallStateStubs);

            Assert.Empty(errors);
        }
    }
}
