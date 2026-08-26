using NUnit.Framework;
using UnityEngine;
using SceneBuilder.Authoring;

// The AlignMin/AlignMax/AlignCenter geometry: each mode maps a self extent-point to the SAME-named
// target extent-point along the resolved frame axis (opposing faces for Abut*, matching faces/
// centers for Align*). Direct MonoBehaviour construction against a live scene, mirroring
// RoundTripAlignFrameTests.
public class RoundTripAlignModeTests
{
    private const float Tol = 1e-3f;

    // AlignMax: self's MAX face lands on the target's MAX face (far faces flush) -- the opposite
    // pairing from AbutMax, which lands self's MIN face on the target's MAX face.
    [Test]
    public void AlignTo_AlignMaxOnAxis_MyMaxFaceFlushWithTargetMaxFace()
    {
        var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            target.transform.position = Vector3.zero; // unit cube: bounds.max.y == 0.5
            float targetTop = target.GetComponent<Renderer>().bounds.max.y;

            go.transform.position = new Vector3(1.5f, 5f, -2f);
            var aligner = go.AddComponent<AlignTo>();
            aligner.target = target.transform;
            aligner.yMode = AlignTo.Mode.AlignMax;

            aligner.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(targetTop, bounds.max.y, Tol,
                "AlignMax must land self's max face flush with the target's max face (far faces flush), not self's min face.");
            Assert.AreEqual(1.5f, go.transform.position.x, Tol, "AlignMax must leave the free X axis untouched.");
            Assert.AreEqual(-2f, go.transform.position.z, Tol, "AlignMax must leave the free Z axis untouched.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(target);
        }
    }

    // AlignMin: self's MIN face lands on the target's MIN face (near faces flush).
    [Test]
    public void AlignTo_AlignMinOnAxis_MyMinFaceFlushWithTargetMinFace()
    {
        var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            target.transform.position = Vector3.zero; // unit cube: bounds.min.y == -0.5
            float targetBottom = target.GetComponent<Renderer>().bounds.min.y;

            go.transform.position = new Vector3(1.5f, 5f, -2f);
            var aligner = go.AddComponent<AlignTo>();
            aligner.target = target.transform;
            aligner.yMode = AlignTo.Mode.AlignMin;

            aligner.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(targetBottom, bounds.min.y, Tol,
                "AlignMin must land self's min face flush with the target's min face (near faces flush), not self's max face.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(target);
        }
    }

    // AlignCenter: self's center lands exactly on the target's center along the resolved axis.
    [Test]
    public void AlignTo_AlignCenterOnAxis_CentersCoincide()
    {
        var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            target.transform.position = new Vector3(3f, 0f, 0f);

            go.transform.position = new Vector3(-9f, 5f, -2f);
            var aligner = go.AddComponent<AlignTo>();
            aligner.target = target.transform;
            aligner.xMode = AlignTo.Mode.AlignCenter;

            aligner.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            var targetBounds = target.GetComponent<Renderer>().bounds;
            Assert.AreEqual(targetBounds.center.x, bounds.center.x, Tol,
                "AlignCenter must land self's center exactly on the target's center along the aligned axis.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(target);
        }
    }

    // The minigolf deliverable shape: X centers across the target, Y lands top faces co-planar
    // (AlignMax), Z abuts just past the target's max-Z end (AbutMax) -- three modes composing
    // per-axis in one Evaluate. Each axis is its own test so a failure on one axis can never mask
    // whether the others resolved correctly in the same composed Evaluate call.
    private static (GameObject Green, GameObject Go, AlignTo Aligner, Bounds GreenBounds) SetUpMixedAlignScene()
    {
        var green = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);

        green.transform.position = new Vector3(2f, 0f, 1f);
        var greenBounds = green.GetComponent<Renderer>().bounds;

        go.transform.position = new Vector3(-9f, 5f, -9f);
        var aligner = go.AddComponent<AlignTo>();
        aligner.target = green.transform;
        aligner.xMode = AlignTo.Mode.AlignCenter;
        aligner.yMode = AlignTo.Mode.AlignMax;
        aligner.zMode = AlignTo.Mode.AbutMax;

        aligner.Evaluate();

        return (green, go, aligner, greenBounds);
    }

    [Test]
    public void AlignTo_MixedCall_XAlignCenter_CentersAcrossTarget()
    {
        var (green, go, _, greenBounds) = SetUpMixedAlignScene();
        try
        {
            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(greenBounds.center.x, bounds.center.x, Tol,
                "X (AlignCenter) must center self across the target on X.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(green);
        }
    }

    [Test]
    public void AlignTo_MixedCall_YAlignMax_TopFacesCoplanar()
    {
        var (green, go, _, greenBounds) = SetUpMixedAlignScene();
        try
        {
            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(greenBounds.max.y, bounds.max.y, Tol,
                "Y (AlignMax) must make self's top face co-planar with the target's top face.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(green);
        }
    }

    [Test]
    public void AlignTo_MixedCall_ZAbutMax_RestsJustPastTargetMaxZ()
    {
        var (green, go, _, greenBounds) = SetUpMixedAlignScene();
        try
        {
            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(greenBounds.max.z, bounds.min.z, Tol,
                "Z (AbutMax) must rest self's min face against the target's max face (abutting just past max-Z), unaffected by the other two axes' modes.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(green);
        }
    }
}
