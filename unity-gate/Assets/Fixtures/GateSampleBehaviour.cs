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
}
