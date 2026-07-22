namespace SceneBuilder.Authoring
{
    /// <summary>
    /// Base type every generated typed prefab reference derives from — both root prefab refs
    /// (e.g. <c>TankRef</c>, exposed as <c>Prefabs.Tank</c>) and node refs for nested children
    /// (e.g. <c>TurretRef</c>, exposed as <c>TankRef.Turret</c>).
    /// </summary>
    /// <remarks>
    /// Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so
    /// derived types carry no runtime behavior (generated get-only properties simply return
    /// <c>default!</c>).
    /// </remarks>
    public class PrefabRef
    {
    }
}
