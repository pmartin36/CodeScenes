using UnityEngine;

namespace SceneBuilder.GateFixtures
{
    // b6-t1 (specs/28-typed-asset-catalogs.md): a real compiled ScriptableObject subclass. The bare
    // engine type `ScriptableObject` has no backing MonoScript, so an asset created directly from it
    // is never returned by `AssetDatabase.FindAssets("t:ScriptableObject")` — the scan needs a real
    // subclass to be indexable.
    public class AssetCatalogScriptableObjectFixture : ScriptableObject
    {
    }
}
