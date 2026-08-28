using UnityEngine;

namespace GateFixtures
{
    // A component's OWN top-level serialized field is private with no public property setter
    // (unlike PrivateBacked's struct member in GateSampleBehaviour.cs) -- an authored typed
    // selector naming it (`r => r.m_Secret`) parses but never compiles. Isolated in its own
    // single-class file (not alongside GateSampleBehaviour's several MonoBehaviour types):
    // measured on this Unity version, a MonoBehaviour that is not the first/main type declared
    // in a multi-class script file loses its script reference (`m_Script: {fileID: 0}`, a
    // broken/missing-script placeholder) when added via `AddComponent<T>()` and persisted through
    // `PrefabUtility.SaveAsPrefabAsset` -- reflection-based lookups (`GetComponent<T>()`,
    // `MonoScript.FromMonoBehaviour`) still resolve the type fine, so the failure is silent until
    // the asset is reloaded from disk.
    public class InaccessibleFieldFixtureBehaviour : MonoBehaviour
    {
        [SerializeField] private float m_Secret;
        public float ReadSecret => m_Secret;
    }
}
