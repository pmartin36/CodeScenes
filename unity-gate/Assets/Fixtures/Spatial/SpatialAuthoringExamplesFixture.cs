using SceneBuilder.Authoring;
using UnityEngine;
using static SceneBuilder.Authoring.AssetRefs;

// Compiles the spec's spatial authoring examples (specs/19-spatial-authoring-components.md
// §"Authoring API") so the real .Sizer/.Snapper call-sites and overload resolution — the
// aspect-locked overload, the explicit tuple `size:` overload, and the target-override /
// depth-axis Snapper forms — are exercised by the Unity compile, not just NodeHandle.cs alone.
// Lives in its own asmdef (referencing SceneBuilder.Authoring) rather than GateFixtures, which is
// deliberately reference-free (BuilderProjectInjectorTests.ReferencesAuthoring_ReadsTheRealEditorAssemblyGraph
// asserts GateFixtures reports no Authoring reference).
public class SpatialAuthoringExamplesFixture : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Crate: aspect-locked height, then snapped down onto whatever is below.
        scene.Add("Crate")
             .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Cube")))
             .Sizer(height: 1.2f)
             .Snapper(down: true);

        // Floor: explicit per-axis world size via the tuple overload — the load-bearing call.
        scene.Add("Floor")
             .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Plane")))
             .Sizer(size: (20, 1, 20));

        // Lamp snapped up onto an explicit target (no raycast needed).
        var ceiling = scene.Add("Ceiling").Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Plane")));
        scene.Add("Lamp")
             .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Cylinder")))
             .Sizer(height: 0.3f)
             .Snapper(up: true, target: ceiling);

        // Poster snapped back-flush against a wall (depth axis).
        scene.Add("Poster")
             .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Quad")))
             .Sizer(width: 0.8f)
             .Snapper(back: true);
    }
}
