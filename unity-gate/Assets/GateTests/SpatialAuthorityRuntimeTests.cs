using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using SceneBuilder.Authoring;

// Spec 45 bucket b3: runtime per-axis arbitration between two live position drivers (AlignTo,
// Between) sharing one object. Same world axis -> one deterministic winner + a one-time warning from
// the yielding driver, stable across repeated evaluates and independent of call order. Different world
// axes -> both apply, no warning. Fixtures place AlignTo's raycast surface and Between's corridor
// anchors on non-overlapping rays so each driver's own math is exercised in isolation from the other's
// fixture geometry.
public class SpatialAuthorityRuntimeTests
{
    private const float Tol = 1e-3f;

    private static GameObject UnitCube(string name, Vector3 position)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.position = position;
        return go;
    }

    private static Between AddBetween(GameObject self, Transform from, Transform to, float fraction,
        Between.Axis axis, Transform orientation = null)
    {
        var between = self.AddComponent<Between>();
        between.from = from;
        between.to = to;
        between.fraction = fraction;
        between.axis = axis;
        between.orientation = orientation;
        return between;
    }

    // Self at (0,10,0). Wall at (100,10,0) so a Right-equivalent (AbutMin) raycast hits only the wall
    // (the Between corridor anchors sit at y=0, off the ray's y=10 plane). AlignTo wants x=99 (flush on
    // the wall); Between wants x=0 (corridor midpoint) -- a genuine same-axis conflict. AlignTo's
    // execution order (-90) must win over Between's (-80).
    [Test]
    public void SameAxis_AlignToAndBetween_DeterministicWinner_OneWarning_Stable()
    {
        var wall = UnitCube("Wall", new Vector3(100f, 10f, 0f));
        var from = UnitCube("From", new Vector3(-5f, 0f, 0f));
        var to = UnitCube("To", new Vector3(5f, 0f, 0f));
        var self = UnitCube("Self", new Vector3(0f, 10f, 0f));
        try
        {
            var snap = self.AddComponent<AlignTo>();
            snap.xMode = AlignTo.Mode.AbutMin;
            var between = AddBetween(self, from.transform, to.transform, 0.5f, Between.Axis.X);

            LogAssert.Expect(LogType.Warning, new Regex(".*"));
            snap.Evaluate();
            between.Evaluate();
            LogAssert.NoUnexpectedReceived();

            Assert.AreEqual(99.0f, self.transform.position.x, Tol,
                "AlignTo (execution order -90) must win a same-axis conflict over Between (-80).");
            Assert.AreEqual(0.5f, between.fraction, Tol,
                "A yielding Between must not back-solve fraction from the winner's write.");

            // Stability: further evaluate rounds must not oscillate or re-log the one-time warning.
            snap.Evaluate();
            between.Evaluate();
            snap.Evaluate();
            between.Evaluate();
            LogAssert.NoUnexpectedReceived();
            Assert.AreEqual(99.0f, self.transform.position.x, Tol,
                "The winning position must stay stable across repeated evaluates.");
        }
        finally
        {
            Object.DestroyImmediate(wall); Object.DestroyImmediate(from); Object.DestroyImmediate(to); Object.DestroyImmediate(self);
        }
    }

    [Test]
    public void SameAxis_CallOrderIndependent()
    {
        var wall = UnitCube("Wall", new Vector3(100f, 10f, 0f));
        var from = UnitCube("From", new Vector3(-5f, 0f, 0f));
        var to = UnitCube("To", new Vector3(5f, 0f, 0f));
        var self = UnitCube("Self", new Vector3(0f, 10f, 0f));
        try
        {
            var snap = self.AddComponent<AlignTo>();
            snap.xMode = AlignTo.Mode.AbutMin;
            var between = AddBetween(self, from.transform, to.transform, 0.5f, Between.Axis.X);

            LogAssert.Expect(LogType.Warning, new Regex(".*"));
            between.Evaluate();
            snap.Evaluate();
            LogAssert.NoUnexpectedReceived();

            Assert.AreEqual(99.0f, self.transform.position.x, Tol,
                "The winner must be decided by priority, not by which driver's Evaluate() ran last.");
        }
        finally
        {
            Object.DestroyImmediate(wall); Object.DestroyImmediate(from); Object.DestroyImmediate(to); Object.DestroyImmediate(self);
        }
    }

    [Test]
    public void Warning_IsOneTime()
    {
        var wall = UnitCube("Wall", new Vector3(100f, 10f, 0f));
        var from = UnitCube("From", new Vector3(-5f, 0f, 0f));
        var to = UnitCube("To", new Vector3(5f, 0f, 0f));
        var self = UnitCube("Self", new Vector3(0f, 10f, 0f));
        try
        {
            var snap = self.AddComponent<AlignTo>();
            snap.xMode = AlignTo.Mode.AbutMin;
            var between = AddBetween(self, from.transform, to.transform, 0.5f, Between.Axis.X);

            LogAssert.Expect(LogType.Warning, new Regex(".*"));
            for (int i = 0; i < 5; i++)
            {
                snap.Evaluate();
                between.Evaluate();
            }
            LogAssert.NoUnexpectedReceived();
        }
        finally
        {
            Object.DestroyImmediate(wall); Object.DestroyImmediate(from); Object.DestroyImmediate(to); Object.DestroyImmediate(self);
        }
    }

    [Test]
    public void DifferentAxes_AlignToY_BetweenX_Compose_NoWarning()
    {
        var floor = UnitCube("Floor", Vector3.zero);
        var from = UnitCube("From", new Vector3(-5f, -20f, 0f));
        var to = UnitCube("To", new Vector3(5f, -20f, 0f));
        var self = UnitCube("Self", new Vector3(0f, 5f, 0f));
        try
        {
            var snap = self.AddComponent<AlignTo>();
            snap.yMode = AlignTo.Mode.AbutMax;
            var between = AddBetween(self, from.transform, to.transform, 0.5f, Between.Axis.X);

            snap.Evaluate();
            between.Evaluate();
            LogAssert.NoUnexpectedReceived();

            var bounds = self.GetComponent<Renderer>().bounds;
            float floorTop = floor.GetComponent<Renderer>().bounds.max.y;
            Assert.AreEqual(floorTop, bounds.min.y, Tol,
                "AlignTo must still rest self on the floor when Between claims a different axis.");
            Assert.AreEqual(0f, self.transform.position.x, Tol,
                "Between must still place self at the corridor fraction on X.");
            Assert.AreEqual(0f, self.transform.position.z, Tol, "Z stays free and untouched.");
        }
        finally
        {
            Object.DestroyImmediate(floor); Object.DestroyImmediate(from); Object.DestroyImmediate(to); Object.DestroyImmediate(self);
        }
    }

    // Root rotated 45deg about Y; d = root.right lies in the world XZ plane (d.y == 0), so an oriented
    // Between claims world {X,Z} only -- it must compose with a co-authored AlignTo on world Y,
    // never treated as a Y conflict.
    [Test]
    public void OrientedBetweenXZ_PlusAlignToY_Compose_NoWarning()
    {
        var root = new GameObject("Root");
        root.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
        var d = root.transform.right;

        var floor = UnitCube("Floor", Vector3.zero);
        floor.transform.localScale = new Vector3(20f, 1f, 20f);
        var from = UnitCube("From", -d * 5f + Vector3.up * 20f);
        var to = UnitCube("To", d * 5f + Vector3.up * 20f);
        var self = UnitCube("Self", d * 3f + Vector3.up * 5f);
        try
        {
            var snap = self.AddComponent<AlignTo>();
            snap.yMode = AlignTo.Mode.AbutMax;
            var between = AddBetween(self, from.transform, to.transform, 0.5f, Between.Axis.X, root.transform);

            snap.Evaluate();
            between.Evaluate();
            LogAssert.NoUnexpectedReceived();

            float floorTop = floor.GetComponent<Renderer>().bounds.max.y;
            var bounds = self.GetComponent<Renderer>().bounds;
            Assert.AreEqual(floorTop, bounds.min.y, Tol,
                "AlignTo on Y must still apply when Between's oriented claim is world {X,Z}.");
            Assert.AreEqual(0f, self.transform.position.x, Tol, "Between must still follow the tilted axis on X.");
            Assert.AreEqual(0f, self.transform.position.z, Tol, "Between must still follow the tilted axis on Z.");
        }
        finally
        {
            Object.DestroyImmediate(root); Object.DestroyImmediate(floor);
            Object.DestroyImmediate(from); Object.DestroyImmediate(to); Object.DestroyImmediate(self);
        }
    }

    // Zero-regression guard: a lone driver (no sibling position driver on the object) must behave
    // exactly as it did before arbitration existed -- the single-driver fast path is a no-op.
    [Test]
    public void SingleDriver_NoArbitrationSideEffect()
    {
        var floor = UnitCube("Floor", Vector3.zero);
        var go = UnitCube("Go", new Vector3(1.5f, 5f, -2f));
        try
        {
            var snap = go.AddComponent<AlignTo>();
            snap.yMode = AlignTo.Mode.AbutMax;

            snap.Evaluate();
            LogAssert.NoUnexpectedReceived();

            var bounds = go.GetComponent<Renderer>().bounds;
            float floorTop = floor.GetComponent<Renderer>().bounds.max.y;
            Assert.AreEqual(floorTop, bounds.min.y, Tol,
                "A lone AlignTo must be unaffected by the arbitration fast path.");
        }
        finally
        {
            Object.DestroyImmediate(floor); Object.DestroyImmediate(go);
        }

        var from = UnitCube("From", new Vector3(-5f, 0f, 0f));
        var to = UnitCube("To", new Vector3(5f, 0f, 0f));
        var self = UnitCube("Self", new Vector3(100f, 100f, 100f));
        try
        {
            var between = AddBetween(self, from.transform, to.transform, 0.5f, Between.Axis.X);
            between.Evaluate();
            LogAssert.NoUnexpectedReceived();

            Assert.AreEqual(0f, self.transform.position.x, Tol,
                "A lone Between must be unaffected by the arbitration fast path.");
        }
        finally
        {
            Object.DestroyImmediate(from); Object.DestroyImmediate(to); Object.DestroyImmediate(self);
        }
    }
}
