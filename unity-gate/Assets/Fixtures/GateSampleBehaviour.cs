using System;
using UnityEngine;

namespace GateFixtures
{
    [Serializable]
    public struct Damage
    {
        public float amount;
        public int kind;
    }

    [Serializable]
    public struct Inner
    {
        public float x;
    }

    [Serializable]
    public struct Outer
    {
        public Inner inner;
        public float y;
    }

    [Serializable]
    public struct Pair<T>
    {
        public T value;
    }

    // A real user MonoBehaviour backed by a MonoScript asset — its component identity must anchor to
    // that MonoScript's GUID (not just Type.FullName) so it survives assembly/namespace churn.
    public class GateSampleBehaviour : MonoBehaviour
    {
        public int Health;
        public Damage Damage;
        public Outer Outer;
        public Damage[] Volley;
        public Pair<int> Pair;
    }

    // A serialized field with no corresponding public member at all: `m_Hidden` -> conventional
    // name `hidden`, and this struct exposes no such property (only a differently-named accessor).
    // No compiling initializer form exists for it, ever.
    [Serializable]
    public struct PrivateBacked
    {
        [SerializeField] private float m_Hidden;
        public float ReadHidden => m_Hidden;
    }

    // A struct with ONE representable public member alongside a private-backed member with no
    // compiling spelling: a sync that changes only the public member must still surface the
    // private one as a located conflict, not stay silent about it.
    [Serializable]
    public struct MixedPrivateBacked
    {
        public float Visible;
        [SerializeField] private float m_Hidden;
        public float ReadHidden => m_Hidden;
    }

    // Isolated on its own behaviour (never GateSampleBehaviour, whose field set several suites pin)
    // so a nested member with no compiling emission form can be exercised without disturbing them.
    public class NestedUnrepresentableFixtureBehaviour : MonoBehaviour
    {
        public PrivateBacked Nested;
        public MixedPrivateBacked Mixed;
    }

    [Serializable]
    public struct DeepInner
    {
        public float a;
        public float b;
    }

    [Serializable]
    public struct DeepOuter
    {
        public DeepInner inner;
        public float y;
    }

    // Isolated on its own behaviour: `Inner`/`Outer` above have a single inner member, so a
    // partially-authored inner struct (some members set, others omitted) is not expressible with
    // them.
    public class NestedInNestedFixtureBehaviour : MonoBehaviour
    {
        public DeepOuter Deep;
    }
}
