using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using SceneBuilder.Authoring;

// M-Between (spec 45) EditMode geometry coverage: the projected-extent corridor kernel driving
// Between.Evaluate(), and the ProjectedExtent kernel it calls. Constructs Between directly against a
// live scene (no builder/sidecar round trip needed for pure geometry, mirroring RoundTripSpatialTests.cs's
// direct-AddComponent pattern for FitSize/SurfaceSnap). Every anchor and self object is a unit
// (1x1x1) primitive cube (half-extent 0.5 on every axis) unless a test states otherwise, so expected
// positions below are hand-derived from the corridor formula: fromInnerFace/toInnerFace are the anchors'
// faces facing each other, ps0/ps1 are self's centre at fraction 0/1 (self's own extent placed flush
// against those faces), and the target centre is the unclamped lerp between ps0 and ps1.
public class RoundTripBetweenPlacementTests
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

    // fromInnerFace = -5+0.5 = -4.5; ps0 = -4.5+0.5 = -4.0 (self's from-facing face flush at -4.5).
    // toInnerFace = 5-0.5 = 4.5; ps1 = 4.5-0.5 = 4.0 (self's to-facing face flush at 4.5).
    // Midpoint (equal-extent anchors) = (-5+5)/2 = 0.
    [Test]
    public void Between_FractionZero_HalfMidOne_PlacesFlushCentredFlush()
    {
        var from = UnitCube("From", new Vector3(-5f, 0f, 0f));
        var to = UnitCube("To", new Vector3(5f, 0f, 0f));
        var zero = UnitCube("Zero", new Vector3(100f, 100f, 100f));
        var half = UnitCube("Half", new Vector3(100f, 100f, 100f));
        var one = UnitCube("One", new Vector3(100f, 100f, 100f));
        try
        {
            AddBetween(zero, from.transform, to.transform, 0f, Between.Axis.X).Evaluate();
            AddBetween(half, from.transform, to.transform, 0.5f, Between.Axis.X).Evaluate();
            AddBetween(one, from.transform, to.transform, 1f, Between.Axis.X).Evaluate();

            Assert.AreEqual(-4.0f, zero.transform.position.x, Tol, "fraction 0 must place self's centre at ps0.");
            Assert.AreEqual(-4.5f, zero.GetComponent<Renderer>().bounds.min.x, Tol,
                "fraction 0 must land self's from-facing face flush against from's inner face.");

            Assert.AreEqual(0f, half.transform.position.x, Tol, "fraction 0.5 must centre self between equal-extent anchors.");

            Assert.AreEqual(4.0f, one.transform.position.x, Tol, "fraction 1 must place self's centre at ps1.");
            Assert.AreEqual(4.5f, one.GetComponent<Renderer>().bounds.max.x, Tol,
                "fraction 1 must land self's to-facing face flush against to's inner face.");
        }
        finally
        {
            Object.DestroyImmediate(from); Object.DestroyImmediate(to);
            Object.DestroyImmediate(zero); Object.DestroyImmediate(half); Object.DestroyImmediate(one);
        }
    }

    [Test]
    public void Between_NegativeAndOverOne_PlacePastAnchors()
    {
        var from = UnitCube("From", new Vector3(-5f, 0f, 0f));
        var to = UnitCube("To", new Vector3(5f, 0f, 0f));
        var negative = UnitCube("Negative", new Vector3(100f, 0f, 0f));
        var overOne = UnitCube("OverOne", new Vector3(100f, 0f, 0f));
        try
        {
            AddBetween(negative, from.transform, to.transform, -0.5f, Between.Axis.X).Evaluate();
            AddBetween(overOne, from.transform, to.transform, 1.5f, Between.Axis.X).Evaluate();

            Assert.Less(negative.transform.position.x, from.transform.position.x,
                "A negative fraction must place self past the from anchor.");
            Assert.Greater(overOne.transform.position.x, to.transform.position.x,
                "A fraction > 1 must place self past the to anchor.");
        }
        finally
        {
            Object.DestroyImmediate(from); Object.DestroyImmediate(to);
            Object.DestroyImmediate(negative); Object.DestroyImmediate(overOne);
        }
    }

    [Test]
    public void Between_DifferentAnchorHeights_AxisX_LerpsAlongXHeightIgnored()
    {
        var from = UnitCube("From", new Vector3(-5f, 2f, 0f));
        var to = UnitCube("To", new Vector3(5f, -3f, 0f));
        var self = UnitCube("Self", new Vector3(0f, 10f, 0f));
        try
        {
            AddBetween(self, from.transform, to.transform, 0.5f, Between.Axis.X).Evaluate();

            Assert.AreEqual(0f, self.transform.position.x, Tol, "X must lerp along the axis ignoring anchor heights.");
            Assert.AreEqual(10f, self.transform.position.y, Tol, "Y must stay untouched — height is ignored on axis X.");
        }
        finally
        {
            Object.DestroyImmediate(from); Object.DestroyImmediate(to); Object.DestroyImmediate(self);
        }
    }

    [Test]
    public void Between_AnchorsInDifferentBranches_PlacementHierarchyIndependent()
    {
        var branchA = new GameObject("BranchA");
        var branchB = new GameObject("BranchB");
        var branchC = new GameObject("BranchC");
        branchA.transform.SetPositionAndRotation(new Vector3(20f, 5f, -8f), Quaternion.Euler(0f, 30f, 0f));
        branchB.transform.SetPositionAndRotation(new Vector3(-40f, -2f, 3f), Quaternion.Euler(10f, 0f, 0f));
        branchC.transform.SetPositionAndRotation(new Vector3(1f, 1f, 1f), Quaternion.identity);

        var from = UnitCube("From", Vector3.zero);
        var to = UnitCube("To", Vector3.zero);
        var self = UnitCube("Self", Vector3.zero);
        try
        {
            from.transform.SetParent(branchA.transform, worldPositionStays: false);
            to.transform.SetParent(branchB.transform, worldPositionStays: false);
            self.transform.SetParent(branchC.transform, worldPositionStays: false);

            // Reparenting under a rotated branch would otherwise tilt each anchor's world rotation
            // along with it (SetParent preserves LOCAL rotation, not world). Hold world rotation at
            // identity so the branch's own tilt (irrelevant to this test) does not shrink the anchors'
            // projected extent on the X axis and shift the expected corridor midpoint.
            from.transform.rotation = Quaternion.identity;
            to.transform.rotation = Quaternion.identity;
            self.transform.rotation = Quaternion.identity;

            from.transform.position = new Vector3(-5f, 0f, 0f);
            to.transform.position = new Vector3(5f, 0f, 0f);
            self.transform.position = new Vector3(0f, 10f, 0f);

            AddBetween(self, from.transform, to.transform, 0.5f, Between.Axis.X).Evaluate();

            Assert.AreEqual(0f, self.transform.position.x, Tol,
                "Placement must not depend on which scene-tree branch the anchors or self live under.");
        }
        finally
        {
            Object.DestroyImmediate(branchA); Object.DestroyImmediate(branchB); Object.DestroyImmediate(branchC);
        }
    }

    // Root rotated 45deg about Y; d = root.right. Anchors sit exactly on the tilted line at +-5*d, self
    // starts at 3*d + up*2 (an orthogonal offset). Translation moves ONLY along d, so the expected final
    // world position is exactly the untouched orthogonal component (0, 2, 0) — hand-computable without
    // re-deriving the kernel's own dot-product math.
    [Test]
    public void Between_OrientedAxisUnder45DegRoot_FollowsTiltedAxis()
    {
        var root = new GameObject("Root");
        root.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
        var d = root.transform.right;

        var from = UnitCube("From", -d * 5f);
        var to = UnitCube("To", d * 5f);
        var self = UnitCube("Self", d * 3f + Vector3.up * 2f);
        try
        {
            AddBetween(self, from.transform, to.transform, 0.5f, Between.Axis.X, root.transform).Evaluate();

            Assert.AreEqual(0f, self.transform.position.x, Tol, "Oriented placement must follow the tilted axis, not world X.");
            Assert.AreEqual(2f, self.transform.position.y, Tol, "The component orthogonal to the tilted axis must stay untouched.");
            Assert.AreEqual(0f, self.transform.position.z, Tol, "Oriented placement must follow the tilted axis, not world Z.");
        }
        finally
        {
            Object.DestroyImmediate(root); Object.DestroyImmediate(from); Object.DestroyImmediate(to); Object.DestroyImmediate(self);
        }
    }

    // from at the HIGHER world x, to at the LOWER world x — the anchor argument, not spatial position,
    // must fix which end is fraction 0. fromInnerFace = 5-0.5=4.5; ps0 = 4.5-0.5=4.0 (flush against from).
    [Test]
    public void Between_AnchorOrderFixedByArgument_FromStaysFractionZeroEndEvenWhenHigherCoordinate()
    {
        var from = UnitCube("From", new Vector3(5f, 0f, 0f));
        var to = UnitCube("To", new Vector3(-5f, 0f, 0f));
        var self = UnitCube("Self", new Vector3(100f, 0f, 0f));
        try
        {
            AddBetween(self, from.transform, to.transform, 0f, Between.Axis.X).Evaluate();

            Assert.AreEqual(4.0f, self.transform.position.x, Tol, "fraction 0 must sit at the from-anchor end regardless of its coordinate.");
            Assert.AreEqual(4.5f, self.GetComponent<Renderer>().bounds.max.x, Tol,
                "self's face must be flush against FROM's face, not TO's.");
        }
        finally
        {
            Object.DestroyImmediate(from); Object.DestroyImmediate(to); Object.DestroyImmediate(self);
        }
    }

    // A self object too large to fit the gap (self full extent 3 > corridor length 1) is degenerate but
    // still placed (the formula still yields a value): ps0 = (-1+0.5)+1.5 = 1.0; ps1 = (1-0.5)-1.5 = -1.0;
    // fraction 0.5 centre = 1.0 + 0.5*(-2.0) = 0.0.
    [Test]
    public void Between_DegenerateCorridor_PlacesWithExactlyOneWarningNoError()
    {
        var from = UnitCube("From", new Vector3(-1f, 0f, 0f));
        var to = UnitCube("To", new Vector3(1f, 0f, 0f));
        var self = UnitCube("Self", new Vector3(100f, 0f, 0f));
        self.transform.localScale = new Vector3(3f, 3f, 3f);
        try
        {
            var between = AddBetween(self, from.transform, to.transform, 0.5f, Between.Axis.X);

            LogAssert.Expect(LogType.Warning, new Regex(".*"));
            between.Evaluate();
            LogAssert.NoUnexpectedReceived();

            Assert.AreEqual(0f, self.transform.position.x, Tol, "A degenerate corridor must still place the object.");
        }
        finally
        {
            Object.DestroyImmediate(from); Object.DestroyImmediate(to); Object.DestroyImmediate(self);
        }
    }

    [Test]
    public void Between_MissingRendererOnAnchor_OneErrorNoMove()
    {
        var from = new GameObject("FromNoRenderer");
        var to = UnitCube("To", new Vector3(5f, 0f, 0f));
        var self = UnitCube("Self", new Vector3(2f, 3f, 4f));
        try
        {
            var between = AddBetween(self, from.transform, to.transform, 0.5f, Between.Axis.X);

            LogAssert.Expect(LogType.Error, new Regex(".*"));
            between.Evaluate();
            LogAssert.NoUnexpectedReceived();

            Assert.AreEqual(new Vector3(2f, 3f, 4f), self.transform.position,
                "A missing-Renderer anchor must not move self at all.");
        }
        finally
        {
            Object.DestroyImmediate(from); Object.DestroyImmediate(to); Object.DestroyImmediate(self);
        }
    }

    [Test]
    public void Between_SelfReference_RejectedNoMove()
    {
        var to = UnitCube("To", new Vector3(5f, 0f, 0f));
        var self = UnitCube("Self", new Vector3(2f, 3f, 4f));
        try
        {
            var between = AddBetween(self, self.transform, to.transform, 0.5f, Between.Axis.X);

            LogAssert.Expect(LogType.Error, new Regex(".*"));
            between.Evaluate();
            LogAssert.NoUnexpectedReceived();

            Assert.AreEqual(new Vector3(2f, 3f, 4f), self.transform.position,
                "A self-referencing anchor must be rejected without moving self.");
        }
        finally
        {
            Object.DestroyImmediate(to); Object.DestroyImmediate(self);
        }
    }

    [Test]
    public void ProjectedExtent_WorldAxisUnrotated_EqualsBoundsHalfExtent()
    {
        var go = UnitCube("Unrotated", Vector3.zero);
        try
        {
            var r = go.GetComponent<Renderer>();
            float half = ProjectedExtent.HalfExtentAlong(r, Vector3.right);

            Assert.AreEqual(r.bounds.extents.x, half, Tol,
                "A world axis on an unrotated object must degenerate to the AABB half-extent.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ProjectedExtent_RotatedBox_ReturnsEnlargedSupportRadius()
    {
        var go = UnitCube("Rotated45", Vector3.zero);
        go.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
        try
        {
            var r = go.GetComponent<Renderer>();
            float half = ProjectedExtent.HalfExtentAlong(r, Vector3.right);
            float expected = 0.5f * (Mathf.Cos(Mathf.Deg2Rad * 45f) + Mathf.Sin(Mathf.Deg2Rad * 45f));

            Assert.Greater(half, 0.5f, "A 45deg-rotated box's support radius on a world axis must exceed its unrotated half-extent.");
            Assert.AreEqual(expected, half, Tol, "The support radius must equal the sum of the rotated local axes' projected contributions.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
