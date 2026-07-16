using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// Smoke test for the nested-value-type gate FIXTURE (b2-t1), not product behavior. Proves the
// serializable-struct fields added to GateSampleBehaviour are reachable SerializedProperty nodes
// from the GateTests assembly — the concrete real-scene surface b2-t2/t3/t4's round-trip tests
// depend on. A manifest/source grep alone would not prove the fixture compiles + serializes.
public class NestedFixtureCapabilitiesTests
{
    [Test]
    public void Fixture_NestedStructField_IsReachableSerializedProperty()
    {
        var go = new GameObject("NestedFixtureCapabilitiesTests.Damage");
        try
        {
            var c = go.AddComponent<GateFixtures.GateSampleBehaviour>();
            var so = new SerializedObject(c);

            var damage = so.FindProperty("Damage");
            Assert.IsNotNull(damage, "GateSampleBehaviour.Damage was not reachable via SerializedProperty.");
            Assert.IsNotNull(so.FindProperty("Damage.amount"), "Damage.amount was not reachable via SerializedProperty.");
            Assert.IsNotNull(so.FindProperty("Damage.kind"), "Damage.kind was not reachable via SerializedProperty.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Fixture_NestedInNestedField_IsReachable()
    {
        var go = new GameObject("NestedFixtureCapabilitiesTests.Outer");
        try
        {
            var c = go.AddComponent<GateFixtures.GateSampleBehaviour>();
            var so = new SerializedObject(c);

            Assert.IsNotNull(so.FindProperty("Outer.inner"), "GateSampleBehaviour.Outer.inner was not reachable via SerializedProperty.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Fixture_NestedCollectionField_IsArray()
    {
        var go = new GameObject("NestedFixtureCapabilitiesTests.Volley");
        try
        {
            var c = go.AddComponent<GateFixtures.GateSampleBehaviour>();
            var so = new SerializedObject(c);

            var volley = so.FindProperty("Volley");
            Assert.IsNotNull(volley, "GateSampleBehaviour.Volley was not reachable via SerializedProperty.");
            Assert.IsTrue(volley.isArray, "GateSampleBehaviour.Volley did not serialize as an array.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Fixture_GenericField_SerializesAsGeneric()
    {
        var go = new GameObject("NestedFixtureCapabilitiesTests.Pair");
        try
        {
            var c = go.AddComponent<GateFixtures.GateSampleBehaviour>();
            var so = new SerializedObject(c);

            var pair = so.FindProperty("Pair");
            Assert.IsNotNull(pair, "GateSampleBehaviour.Pair was not reachable via SerializedProperty.");
            Assert.AreEqual(SerializedPropertyType.Generic, pair.propertyType,
                "GateSampleBehaviour.Pair did not serialize as SerializedPropertyType.Generic.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
