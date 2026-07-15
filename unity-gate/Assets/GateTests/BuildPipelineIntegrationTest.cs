using NUnit.Framework;
using UnityEngine;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Identity;
using SceneBuilder.Editor;

// Real end-to-end gate test: drives the ACTUAL plugin build pipeline
// (parse -> authored-path resolve -> snapshot -> identity remap -> materialize -> execute)
// against a live EditMode scene and asserts real GameObject/Transform state. This is the class
// of test that catches adapter escapes the headless dotnet gate cannot see.
public class BuildPipelineIntegrationTest
{
    private const string Source = @"
using SceneBuilder.Authoring;
public class GateScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""GateCube"").Transform(pos: (1f, 2f, 3f));
    }
}";

    [Test]
    public void BuildPipeline_MaterializesGameObjectAtAuthoredTransform()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        // Through the SAME source->desired seam the product uses, never a hand-wired subset of it: a
        // test that assembles its own pipeline can silently omit a stage (that is exactly how sync
        // came to skip asset-ref lowering) and would then be proving a pipeline nobody ships.
        var loaded = DesiredModelLoader.Load(Source, null);
        var snapshot = SceneSnapshotReader.Read(scene);
        var remapped = IdentityRemapper.Remap(loaded.Parse.Model, new IdentityMap());
        var plan = Materializer.Materialize(loaded.Desired, snapshot, remapped);
        PlanExecutor.Execute(plan, remapped, scene);

        var go = GameObject.Find("GateCube");
        Assert.IsNotNull(go, "GateCube was not created by the real Build pipeline");
        Assert.AreEqual(new Vector3(1f, 2f, 3f), go.transform.position);
    }
}
