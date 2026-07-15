using UnityEngine;

namespace GateFixtures
{
    // A real user MonoBehaviour backed by a MonoScript asset — its component identity must anchor to
    // that MonoScript's GUID (not just Type.FullName) so it survives assembly/namespace churn.
    public class GateSampleBehaviour : MonoBehaviour
    {
        public int Health;
    }
}
