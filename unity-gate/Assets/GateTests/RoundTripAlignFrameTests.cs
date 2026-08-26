using NUnit.Framework;
using UnityEngine;
using SceneBuilder.Authoring;

// AlignTo's new-mechanism proofs beyond the migrated Abut regression suite: the world-unit offset,
// the target-local-by-default frame resolution, the space: World override, and the frame: override.
// Direct MonoBehaviour construction against a live scene, mirroring RoundTripSpatialTests.
public class RoundTripAlignFrameTests
{
    private const float Tol = 1e-3f;

    // An offset is a uniform push along the resolved frame axis, applied AFTER alignment: an
    // AbutMax with a positive offset must open a gap of exactly that size between self's aligned
    // face and the target's face, rather than landing flush.
    [Test]
    public void AlignTo_AbutMaxWithOffset_OpensGapAlongFrameAxis()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            floor.transform.position = Vector3.zero; // default 1x1x1 cube: bounds.max.y == 0.5
            float floorTop = floor.GetComponent<Renderer>().bounds.max.y;

            go.transform.position = new Vector3(1.5f, 5f, -2f);
            var aligner = go.AddComponent<AlignTo>();
            aligner.target = floor.transform;
            aligner.yMode = AlignTo.Mode.AbutMax;
            aligner.yOffset = 0.5f;

            aligner.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(floorTop + 0.5f, bounds.min.y, Tol,
                "An AbutMax offset must open a gap of exactly the offset size between self's bottom face and the target's top face.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(floor);
        }
    }

    // With no frame/space authored, each axis resolves along the TARGET's own local axes, not
    // world axes. A target rotated 90 degrees about Z has its local "up" axis pointing along a
    // world horizontal axis; a y-axis AbutMax must then move self along THAT axis (an exact world
    // axis for a 90-degree rotation), not along world Y. The expected direction is read from the
    // target's own live Transform.up rather than assumed, so the test holds under either rotation
    // handedness convention.
    [Test]
    public void AlignTo_DefaultFrame_AlignsAlongRotatedTargetLocalAxis()
    {
        var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            target.transform.position = Vector3.zero;
            target.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
            Vector3 frameAxis = target.transform.up; // target's own (rotated) local Y axis

            var initialPos = new Vector3(0f, 5f, -2f);
            go.transform.position = initialPos;
            var aligner = go.AddComponent<AlignTo>();
            aligner.target = target.transform;
            aligner.yMode = AlignTo.Mode.AbutMax;

            aligner.Evaluate();

            // Both are default unit cubes (0.5 half-extent each) so self moves by exactly
            // (0.5 + 0.5) = 1 unit along the target's own resolved axis.
            Vector3 expected = initialPos + frameAxis * 1f;
            var actual = go.transform.position;
            Assert.AreEqual(expected.x, actual.x, Tol, "Default frame must resolve along the target's own rotated axis (X component).");
            Assert.AreEqual(expected.y, actual.y, Tol, "Default frame must resolve along the target's own rotated axis (Y component).");
            Assert.AreEqual(expected.z, actual.z, Tol, "Default frame must resolve along the target's own rotated axis (Z component).");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(target);
        }
    }

    // space: World must ignore the target's rotation entirely and resolve every axis on plain
    // world axes, reproducing the same-axis world behavior regardless of how the target is
    // oriented.
    [Test]
    public void AlignTo_SpaceWorld_IgnoresTargetRotationAndAlignsOnWorldAxis()
    {
        var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            target.transform.position = Vector3.zero;
            target.transform.rotation = Quaternion.Euler(0f, 0f, 90f);

            go.transform.position = new Vector3(0f, 5f, -2f);
            var aligner = go.AddComponent<AlignTo>();
            aligner.target = target.transform;
            aligner.yMode = AlignTo.Mode.AbutMax;
            aligner.space = AlignSpace.World;

            aligner.Evaluate();

            var actual = go.transform.position;
            Assert.AreEqual(0f, actual.x, Tol, "space: World must leave the free X axis untouched regardless of the target's rotation.");
            Assert.AreEqual(1f, actual.y, Tol, "space: World must align on the world Y axis regardless of the target's rotation.");
            Assert.AreEqual(-2f, actual.z, Tol, "space: World must leave the free Z axis untouched regardless of the target's rotation.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(target);
        }
    }

    // A frame: override takes precedence over the target's own axes: with an UNROTATED target but
    // a rotated frame transform, alignment must resolve along the frame's axis, not the target's.
    [Test]
    public void AlignTo_FrameOverride_AlignsAlongFrameTransformAxisNotTarget()
    {
        var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var frameObj = new GameObject("Frame");
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            target.transform.position = Vector3.zero; // unrotated: its own axes equal world axes
            frameObj.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
            Vector3 frameAxis = frameObj.transform.up;

            var initialPos = new Vector3(0f, 5f, -2f);
            go.transform.position = initialPos;
            var aligner = go.AddComponent<AlignTo>();
            aligner.target = target.transform;
            aligner.frame = frameObj.transform;
            aligner.yMode = AlignTo.Mode.AbutMax;

            aligner.Evaluate();

            Vector3 expected = initialPos + frameAxis * 1f;
            var actual = go.transform.position;
            Assert.AreEqual(expected.x, actual.x, Tol, "frame: override must resolve along the frame transform's axis, not the target's (X component).");
            Assert.AreEqual(expected.y, actual.y, Tol, "frame: override must resolve along the frame transform's axis, not the target's (Y component).");
            Assert.AreEqual(expected.z, actual.z, Tol, "frame: override must resolve along the frame transform's axis, not the target's (Z component).");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(frameObj);
            Object.DestroyImmediate(target);
        }
    }
}
