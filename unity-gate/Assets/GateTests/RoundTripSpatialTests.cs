using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using SceneBuilder.Authoring;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using SceneBuilder.Editor;

// M-Spatial (FitSize/SurfaceSnap) EditMode geometry coverage. b5-t1 is this file's minimal slice — the exact
// world-size solve and the serialized-field-name contract for FitSize. Constructs the MonoBehaviour
// directly against a live scene (no builder/sidecar round trip needed for pure geometry). The full
// checklist suite (SurfaceSnap, build-strip, ordering, disabled/re-snap, fallback) lands in b5-t2..b5-t4.
public class RoundTripSpatialTests
{
    private const float Tol = 1e-3f;

    // 1. Aspect-locked height solve: a non-unit starting scale on a unit cube must still land the
    //    WORLD height exactly on the authored value, with X/Z scaled by the same aspect factor (the
    //    unit cube's local bounds are 1x1x1, so the uniform factor equals the target height).
    [Test]
    public void FitSize_HeightOnNonUnitCube_DrivesWorldHeightToTwo()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            go.transform.localScale = new Vector3(5f, 5f, 5f);
            var sizer = go.AddComponent<FitSize>();
            sizer.height = 2f;

            sizer.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(2f, bounds.size.y, Tol, "FitSize must drive the WORLD height to the authored value.");
            Assert.AreEqual(bounds.size.y, bounds.size.x, Tol, "Aspect-locked height must scale X uniformly with Y.");
            Assert.AreEqual(bounds.size.y, bounds.size.z, Tol, "Aspect-locked height must scale Z uniformly with Y.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    // 2. The serialized field names ARE the write contract (SerializedFieldBridge writes M3 field-map
    //    keys by name) — they must literally match SpatialComponents.FitSizeFields.*. Subset check: an
    //    internal discriminator/flag field is permitted in addition.
    [Test]
    public void FitSize_SerializedFields_MatchSpatialComponentsFieldNameKeys()
    {
        var type = typeof(FitSize);
        string[] required =
        {
            SpatialComponents.FitSizeFields.Width,
            SpatialComponents.FitSizeFields.Height,
            SpatialComponents.FitSizeFields.Depth,
            SpatialComponents.FitSizeFields.Size,
        };

        foreach (var fieldName in required)
        {
            var field = type.GetField(fieldName);
            Assert.IsNotNull(field, $"FitSize must expose a public field named '{fieldName}' (SpatialComponents.FitSizeFields).");
        }
    }

    // 3. No MeshFilter/mesh to size ⇒ a located error naming the node, never a silent no-op or an
    //    exception, and the transform must be left untouched (no divide-by-zero guess).
    [Test]
    public void FitSize_NoMeshFilter_LogsLocatedErrorAndDoesNotScale()
    {
        var go = new GameObject("NoMeshFitSize");
        try
        {
            go.transform.localScale = Vector3.one;
            var sizer = go.AddComponent<FitSize>();
            sizer.height = 2f;

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("NoMeshFitSize"));
            sizer.Evaluate();

            Assert.AreEqual(Vector3.one, go.transform.localScale, "No MeshFilter must never write a scale.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    // 4. b5-t2 minimal slice: down-snap rests the bottom face of the world Renderer.bounds flush on a
    //    floor collider's top face, leaving the free axes (X/Z) untouched — pivot-agnostic because the
    //    delta is applied to transform.position on the snapped axis only.
    [Test]
    public void SurfaceSnap_DownOnFloor_RestsBottomFaceOnFloorTop()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            floor.transform.position = Vector3.zero; // default 1x1x1 cube: bounds.max.y == 0.5
            float floorTop = floor.GetComponent<Renderer>().bounds.max.y;

            go.transform.position = new Vector3(1.5f, 5f, -2f);
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.down = true;

            snapper.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(floorTop, bounds.min.y, Tol, "Down-snap must rest the bottom face flush on the floor's top face.");
            Assert.AreEqual(1.5f, go.transform.position.x, Tol, "Down-snap must leave the free X axis untouched.");
            Assert.AreEqual(-2f, go.transform.position.z, Tol, "Down-snap must leave the free Z axis untouched.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(floor);
        }
    }

    // 5. The serialized field names ARE the write contract — they must literally match
    //    SpatialComponents.SurfaceSnapFields.* (subset check: internal book-keeping fields are permitted).
    [Test]
    public void SurfaceSnap_SerializedFields_MatchSpatialComponentsFieldNameKeys()
    {
        var type = typeof(SurfaceSnap);
        string[] required =
        {
            SpatialComponents.SurfaceSnapFields.Up,
            SpatialComponents.SurfaceSnapFields.Down,
            SpatialComponents.SurfaceSnapFields.Left,
            SpatialComponents.SurfaceSnapFields.Right,
            SpatialComponents.SurfaceSnapFields.Forward,
            SpatialComponents.SurfaceSnapFields.Back,
            SpatialComponents.SurfaceSnapFields.Target,
        };

        foreach (var fieldName in required)
        {
            var field = type.GetField(fieldName);
            Assert.IsNotNull(field, $"SurfaceSnap must expose a public field named '{fieldName}' (SpatialComponents.SurfaceSnapFields).");
        }

        var targetField = type.GetField(SpatialComponents.SurfaceSnapFields.Target);
        Assert.AreEqual(typeof(Transform), targetField.FieldType, "SurfaceSnap.target must be a Transform.");
    }

    // 6. No Renderer/mesh bounds to snap ⇒ a located error naming the node, never a silent no-op or an
    //    exception, and the transform must be left untouched (no bounds-of-nothing guess).
    [Test]
    public void SurfaceSnap_NoRenderer_LogsLocatedErrorAndDoesNotMove()
    {
        var go = new GameObject("NoRendererSurfaceSnap");
        try
        {
            var originalPosition = new Vector3(3f, 4f, 5f);
            go.transform.position = originalPosition;
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.down = true;

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("NoRendererSurfaceSnap"));
            snapper.Evaluate();

            Assert.AreEqual(originalPosition, go.transform.position, "No Renderer must never write a position.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    // 7. Build-strip (b5-t3): a real player build must bake the current transform then destroy the
    //    FitSize/SurfaceSnap components entirely, leaving no missing-script stub. Drives the internal
    //    StripScene(scene) entry point directly — BuildReport is not a ScriptableObject and cannot be
    //    constructed via CreateInstance in EditMode (confirmed CS0311), so this is the documented
    //    fallback for exercising the non-null-report path (same observable effect as OnProcessScene).
    [Test]
    public void BuildStrip_WithReport_RemovesFitSizeAndSurfaceSnap_NoMissingScript()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            floor.transform.position = Vector3.zero;

            go.transform.localScale = new Vector3(5f, 5f, 5f);
            go.transform.position = new Vector3(1.5f, 5f, -2f);
            var sizer = go.AddComponent<FitSize>();
            sizer.height = 2f;
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.down = true;

            SpatialBuildStripper.StripScene(go.scene);

            Assert.IsNull(go.GetComponent<FitSize>(), "Build strip must remove FitSize.");
            Assert.IsNull(go.GetComponent<SurfaceSnap>(), "Build strip must remove SurfaceSnap.");
            Assert.AreEqual(0, GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go),
                "Build strip must leave no missing-script stub.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(floor);
        }
    }

    // 8. The strip must bake before destroying: the object's final world geometry (post FitSize resize,
    //    post SurfaceSnap snap) must survive component removal exactly as if the components were still live.
    [Test]
    public void BuildStrip_WithReport_KeepsBakedTransform()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            floor.transform.position = Vector3.zero;
            float floorTop = floor.GetComponent<Renderer>().bounds.max.y;

            go.transform.localScale = new Vector3(5f, 5f, 5f);
            go.transform.position = new Vector3(1.5f, 5f, -2f);
            var sizer = go.AddComponent<FitSize>();
            sizer.height = 2f;
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.down = true;

            SpatialBuildStripper.StripScene(go.scene);

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(2f, bounds.size.y, Tol, "Baked height must survive component removal.");
            Assert.AreEqual(floorTop, bounds.min.y, Tol, "Baked down-snap must survive component removal.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(floor);
        }
    }

    // 9. report == null is the editor-play no-op path — the components must survive untouched (they
    //    self-disable via the Application.isPlaying guard instead of being stripped).
    [Test]
    public void BuildStrip_NullReport_LeavesComponents()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            var sizer = go.AddComponent<FitSize>();
            sizer.height = 2f;
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.down = true;

            new SpatialBuildStripper().OnProcessScene(go.scene, null);

            Assert.IsNotNull(go.GetComponent<FitSize>(), "Null report (editor play) must leave FitSize in place.");
            Assert.IsNotNull(go.GetComponent<SurfaceSnap>(), "Null report (editor play) must leave SurfaceSnap in place.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    // b5-t4: EditMode geometry confirmation suite — fills the remaining checklist gaps not already
    // covered by tests 1-9 above (items 15 & 16 are already fully covered; do not re-add).

    /// <summary>8-vert/12-tri axis-aligned box mesh with an OFFSET centre, so
    /// <c>Renderer.bounds.center != transform.position</c> — the only way to produce genuine pivot
    /// variance for the three-pivot SurfaceSnap test (a primitive cube is always centre-pivoted).</summary>
    private static Mesh MakeBoxMesh(Vector3 center, Vector3 size)
    {
        Vector3 h = size * 0.5f;
        Vector3[] vertices =
        {
            center + new Vector3(-h.x, -h.y, -h.z),
            center + new Vector3(h.x, -h.y, -h.z),
            center + new Vector3(h.x, h.y, -h.z),
            center + new Vector3(-h.x, h.y, -h.z),
            center + new Vector3(-h.x, -h.y, h.z),
            center + new Vector3(h.x, -h.y, h.z),
            center + new Vector3(h.x, h.y, h.z),
            center + new Vector3(-h.x, h.y, h.z),
        };
        int[] triangles =
        {
            0, 2, 1, 0, 3, 2, // back
            1, 6, 5, 1, 2, 6, // right
            5, 7, 4, 5, 6, 7, // front
            4, 3, 0, 4, 7, 3, // left
            3, 6, 2, 3, 7, 6, // top
            4, 1, 5, 4, 0, 1, // bottom
        };

        var mesh = new Mesh { vertices = vertices, triangles = triangles };
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        return mesh;
    }

    // 1 (extends). Native-size indifference: a non-cubic native mesh (cylinder, native local bounds
    // height 2) must still land the WORLD height on the authored value, aspect preserved.
    [Test]
    public void FitSize_HeightOnNonUnitMesh_DrivesWorldHeightIndependentOfNativeSize()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        try
        {
            var sizer = go.AddComponent<FitSize>();
            sizer.height = 2f;

            sizer.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(2f, bounds.size.y, Tol, "FitSize must drive the WORLD height to the authored value regardless of native mesh size.");
            Assert.AreEqual(bounds.size.x, bounds.size.z, Tol, "Aspect-locked height must scale X and Z by the same factor.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    // 2. FitSize under a scaled parent: the parent's lossyScale must be divided out so the child still
    //    hits the exact authored WORLD height.
    [Test]
    public void FitSize_UnderScaledParent_DrivesWorldHeightDividingOutParentScale()
    {
        var parent = new GameObject("FitSizeParent");
        var child = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            parent.transform.localScale = new Vector3(3f, 3f, 3f);
            child.transform.SetParent(parent.transform, worldPositionStays: false);

            var sizer = child.AddComponent<FitSize>();
            sizer.height = 2f;

            sizer.Evaluate();

            var bounds = child.GetComponent<Renderer>().bounds;
            Assert.AreEqual(2f, bounds.size.y, Tol, "FitSize must divide out the parent's lossyScale to hit the exact world height.");
        }
        finally
        {
            Object.DestroyImmediate(child);
            Object.DestroyImmediate(parent);
        }
    }

    // 3. Explicit per-axis size (Vector3 size, no aspect-locked dimension set): each axis lands its
    //    own authored world size independently.
    [Test]
    public void FitSize_ExplicitSize_DrivesPerAxisWorldBounds()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            var sizer = go.AddComponent<FitSize>();
            sizer.size = new Vector3(2f, 1f, 0.5f);

            sizer.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(2f, bounds.size.x, Tol, "Explicit size.x must drive world bounds.size.x.");
            Assert.AreEqual(1f, bounds.size.y, Tol, "Explicit size.y must drive world bounds.size.y.");
            Assert.AreEqual(0.5f, bounds.size.z, Tol, "Explicit size.z must drive world bounds.size.z.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    // 4. The headline pivot-agnostic case: three objects with different mesh pivots (feet/centre/head)
    //    down-snapped over the same floor must ALL land their bottom face flush, regardless of pivot.
    [Test]
    public void SurfaceSnap_DownAcrossThreePivots_AllRestBottomOnFloor()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var feet = new GameObject("FeetPivot");
        var centre = new GameObject("CentrePivot");
        var head = new GameObject("HeadPivot");
        try
        {
            floor.transform.position = Vector3.zero;
            float floorTop = floor.GetComponent<Renderer>().bounds.max.y;

            var size = new Vector3(1f, 1f, 1f);
            AddBoxMeshRenderer(feet, new Vector3(0f, 0.5f, 0f), size);   // pivot at feet (bottom)
            AddBoxMeshRenderer(centre, Vector3.zero, size);              // pivot at centre
            AddBoxMeshRenderer(head, new Vector3(0f, -0.5f, 0f), size);  // pivot at head (top)

            // All three sit directly above the floor's x/z footprint (0,0) so each raycast lands
            // DIRECTLY on the floor's collider, independent of where its siblings already snapped to
            // (they carry no Collider, so they cannot occlude each other's raycast).
            feet.transform.position = new Vector3(0f, 5f, 0f);
            centre.transform.position = new Vector3(0f, 6f, 0f);
            head.transform.position = new Vector3(0f, 7f, 0f);

            foreach (var go in new[] { feet, centre, head })
            {
                var snapper = go.AddComponent<SurfaceSnap>();
                snapper.down = true;
                snapper.Evaluate();
            }

            Assert.AreEqual(floorTop, feet.GetComponent<Renderer>().bounds.min.y, Tol, "Feet-pivot object must rest bottom flush on the floor.");
            Assert.AreEqual(floorTop, centre.GetComponent<Renderer>().bounds.min.y, Tol, "Centre-pivot object must rest bottom flush on the floor.");
            Assert.AreEqual(floorTop, head.GetComponent<Renderer>().bounds.min.y, Tol, "Head-pivot object must rest bottom flush on the floor.");
        }
        finally
        {
            Object.DestroyImmediate(feet);
            Object.DestroyImmediate(centre);
            Object.DestroyImmediate(head);
            Object.DestroyImmediate(floor);
        }
    }

    private static void AddBoxMeshRenderer(GameObject go, Vector3 meshCenter, Vector3 size)
    {
        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = MakeBoxMesh(meshCenter, size);
        go.AddComponent<MeshRenderer>();
    }

    // 5. Up-snap against a ceiling: the top face must land flush against the ceiling's bottom face.
    [Test]
    public void SurfaceSnap_UpOntoCeiling_RestsTopFaceOnCeilingBottom()
    {
        var ceiling = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            ceiling.transform.position = new Vector3(0f, 10f, 0f);
            float ceilingBottom = ceiling.GetComponent<Renderer>().bounds.min.y;

            go.transform.position = new Vector3(0f, 2f, 0f);
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.up = true;

            snapper.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(ceilingBottom, bounds.max.y, Tol, "Up-snap must rest the top face flush on the ceiling's bottom face.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(ceiling);
        }
    }

    // 6. Corner: down+left combine must land BOTH the bottom face on the floor AND the left face flush
    //    against the wall's inner (right) face.
    [Test]
    public void SurfaceSnap_DownLeftCorner_RestsBottomAndLeftFacesFlush()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var leftWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            floor.transform.position = Vector3.zero;
            float floorTop = floor.GetComponent<Renderer>().bounds.max.y;

            leftWall.transform.position = new Vector3(-5f, 0f, 0f);
            float leftWallInner = leftWall.GetComponent<Renderer>().bounds.max.x;

            go.transform.position = new Vector3(-1f, 5f, 0f);
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.down = true;
            snapper.left = true;

            snapper.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(floorTop, bounds.min.y, Tol, "Down+left combine must rest the bottom face flush on the floor.");
            Assert.AreEqual(leftWallInner, bounds.min.x, Tol, "Down+left combine must rest the left face flush on the wall's inner face.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(leftWall);
            Object.DestroyImmediate(floor);
        }
    }

    // 7. Ordering: FitSize resizes first, THEN SurfaceSnap reads the post-resize bounds — proving SurfaceSnap
    //    never rests on the pre-FitSize size.
    [Test]
    public void FitSizeThenSurfaceSnap_Ordering_PostResizeBottomRestsOnFloor()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            floor.transform.position = Vector3.zero;
            float floorTop = floor.GetComponent<Renderer>().bounds.max.y;

            go.transform.position = new Vector3(0f, 5f, 0f);
            var sizer = go.AddComponent<FitSize>();
            sizer.height = 1.2f;
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.down = true;

            sizer.Evaluate();
            snapper.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(1.2f, bounds.size.y, Tol, "FitSize must still resize to the authored height.");
            Assert.AreEqual(floorTop, bounds.min.y, Tol, "SurfaceSnap must rest the POST-resize bottom face on the floor.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(floor);
        }
    }

    // 8. Live re-evaluation: after the floor moves, a fresh Evaluate() must track the NEW floor top
    //    (no Build/reconstruction needed).
    [Test]
    public void SurfaceSnap_FloorMovesUp_ReEvaluateTracksNewFloorTop()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            floor.transform.position = Vector3.zero;

            go.transform.position = new Vector3(0f, 5f, 0f);
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.down = true;
            snapper.Evaluate();

            floor.transform.position = new Vector3(0f, 1f, 0f);
            float newFloorTop = floor.GetComponent<Renderer>().bounds.max.y;

            snapper.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(newFloorTop, bounds.min.y, Tol, "Re-Evaluate must track the floor's new position.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(floor);
        }
    }

    // 9. Collider-less fallback: a floor with a Renderer but no Collider must still resolve the surface
    //    via the renderer-bounds fallback scan (no raycast hit possible).
    [Test]
    public void SurfaceSnap_FloorWithoutCollider_FallbackScanStillLands()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            floor.transform.position = Vector3.zero;
            Object.DestroyImmediate(floor.GetComponent<Collider>());
            float floorTop = floor.GetComponent<Renderer>().bounds.max.y;

            go.transform.position = new Vector3(0f, 5f, 0f);
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.down = true;

            snapper.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(floorTop, bounds.min.y, Tol, "Fallback scan (no collider) must still land the object on the floor.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(floor);
        }
    }

    // 10. Explicit target override: a nearer obstacle would otherwise win the raycast, but an explicit
    //     target must be used instead of the raycast hit.
    [Test]
    public void SurfaceSnap_ExplicitTarget_SnapsToTargetNotRaycastHit()
    {
        var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var ceiling = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            obstacle.transform.position = new Vector3(0f, 5f, 0f); // nearer to `go`, would win the raycast
            ceiling.transform.position = new Vector3(0f, 10f, 0f);
            float ceilingBottom = ceiling.GetComponent<Renderer>().bounds.min.y;

            go.transform.position = new Vector3(0f, 2f, 0f);
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.up = true;
            snapper.target = ceiling.transform;

            snapper.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(ceilingBottom, bounds.max.y, Tol, "Explicit target must be used instead of the nearer raycast hit.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(ceiling);
            Object.DestroyImmediate(obstacle);
        }
    }

    // 17. Back-snap: the object's −Z (back) face must land flush against the wall-behind's inner
    //     (+Z / max.z) face — i.e. bounds.min.z, NOT bounds.max.z (research.md correction).
    [Test]
    public void SurfaceSnap_BackAgainstWall_RestsBackFaceFlush()
    {
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            wall.transform.position = new Vector3(0f, 0f, -5f);
            float wallInner = wall.GetComponent<Renderer>().bounds.max.z;

            go.transform.position = new Vector3(0f, 0f, 2f);
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.back = true;

            snapper.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(wallInner, bounds.min.z, Tol, "Back-snap must rest the object's −Z face flush against the wall's inner (+Z) face.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(wall);
        }
    }

    // 17 (combine). down+back two-axis combine: both the bottom face on the floor AND the back face
    //    flush against the wall must land simultaneously.
    [Test]
    public void SurfaceSnap_DownBack_TwoAxisCombineRestsBottomAndBackFlush()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            floor.transform.position = Vector3.zero;
            float floorTop = floor.GetComponent<Renderer>().bounds.max.y;

            // Wall is directly behind at the SAME x/z as go's footprint, and tall enough (y) to catch
            // go's raycast at both its pre-snap and post-down-snap heights — so the back raycast lands
            // directly on the wall, not on a same-height unrelated surface via the fallback scan.
            wall.transform.position = new Vector3(0f, 0f, -2f);
            wall.transform.localScale = new Vector3(1f, 20f, 1f);
            float wallInner = wall.GetComponent<Renderer>().bounds.max.z;

            // Directly above the floor's x/z footprint so the down raycast lands on the floor, not a
            // fallback scan (see item 4's fix for the same reasoning).
            go.transform.position = new Vector3(0f, 5f, 0f);
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.down = true;
            snapper.back = true;

            snapper.Evaluate();

            var bounds = go.GetComponent<Renderer>().bounds;
            Assert.AreEqual(floorTop, bounds.min.y, Tol, "Down+back combine must rest the bottom face flush on the floor.");
            Assert.AreEqual(wallInner, bounds.min.z, Tol, "Down+back combine must rest the back face flush against the wall.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(wall);
            Object.DestroyImmediate(floor);
        }
    }

    // 19 (FitSize half). Disabled FitSize must drive nothing — a manual localScale set while disabled must
    //    stand untouched through Evaluate().
    [Test]
    public void FitSize_Disabled_ManualScaleStands()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            var sizer = go.AddComponent<FitSize>();
            sizer.height = 2f;
            sizer.enabled = false;

            var manualScale = new Vector3(4f, 4f, 4f);
            go.transform.localScale = manualScale;

            sizer.Evaluate();

            Assert.AreEqual(manualScale, go.transform.localScale, "A disabled FitSize must never write localScale; the manual scale must stand.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    // 19 (SurfaceSnap half). Disabled SurfaceSnap must drive nothing — a manual position set while disabled
    //    must stand untouched through Evaluate().
    [Test]
    public void SurfaceSnap_Disabled_ManualMoveStands()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            floor.transform.position = Vector3.zero;

            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.down = true;
            snapper.enabled = false;

            var manualPosition = new Vector3(3f, 7f, 1f);
            go.transform.position = manualPosition;

            snapper.Evaluate();

            Assert.AreEqual(manualPosition, go.transform.position, "A disabled SurfaceSnap must never write position; the manual position must stand.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(floor);
        }
    }

    // ---- b6-t1: snapshot read stamps DrivenChannels from live enabled FitSize/SurfaceSnap -------------
    // These stamp SnapshotNode.Transform.DrivenChannels — the "one subtle seam" the spec flags: an
    // enabled component must suppress the edit-path diff on its owned axes; a disabled one must
    // release them so a manual edit still syncs (Differ.cs:162 consumes this value). These tests
    // assert ONLY on the derived DrivenChannels value, not on geometry, so a bare (mesh-less)
    // GameObject is deliberately used and any FitSize/SurfaceSnap "no mesh/renderer" Console noise —
    // real or from the components' own [ExecuteAlways] ticking — is irrelevant here and ignored,
    // matching the SyncFuzzTests.cs precedent for tests whose reporting surface is the assertion,
    // not the Console.

    private static SnapshotNode FindNode(SnapshotNode[] roots, string name)
    {
        foreach (var n in roots)
        {
            if (n.Name == name) return n;
            var found = FindNode(n.Children, name);
            if (found != null) return found;
        }

        return null;
    }

    [Test]
    public void Read_LiveEnabledFitSize_StampsDrivenChannelsScale()
    {
        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        var go = new GameObject("FitSizeNode");
        try
        {
            var sizer = go.AddComponent<FitSize>();
            sizer.height = 2f;

            var snapshot = SceneSnapshotReader.Read(go.scene);
            var node = FindNode(snapshot.Roots, "FitSizeNode");

            Assert.IsNotNull(node, "FitSizeNode not found in snapshot.");
            Assert.AreEqual(ChannelMask.Scale, node.Transform.DrivenChannels,
                "An active-enabled FitSize must stamp DrivenChannels == Scale.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            LogAssert.ignoreFailingMessages = prevIgnore;
        }
    }

    [Test]
    public void Read_DisabledFitSize_StampsDrivenChannelsNone()
    {
        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        var go = new GameObject("DisabledFitSizeNode");
        try
        {
            var sizer = go.AddComponent<FitSize>();
            sizer.height = 2f;
            sizer.enabled = false;

            var snapshot = SceneSnapshotReader.Read(go.scene);
            var node = FindNode(snapshot.Roots, "DisabledFitSizeNode");

            Assert.IsNotNull(node, "DisabledFitSizeNode not found in snapshot.");
            Assert.AreEqual(ChannelMask.None, node.Transform.DrivenChannels,
                "A disabled FitSize must release its channel (DrivenChannels == None) so a manual scale edit syncs.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            LogAssert.ignoreFailingMessages = prevIgnore;
        }
    }

    [Test]
    public void Read_EnabledSurfaceSnapDown_StampsPositionYOnly()
    {
        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        var go = new GameObject("SurfaceSnapDownNode");
        try
        {
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.down = true;

            var snapshot = SceneSnapshotReader.Read(go.scene);
            var node = FindNode(snapshot.Roots, "SurfaceSnapDownNode");

            Assert.IsNotNull(node, "SurfaceSnapDownNode not found in snapshot.");
            Assert.AreEqual(ChannelMask.PositionY, node.Transform.DrivenChannels,
                "An active-enabled down-SurfaceSnap must stamp DrivenChannels == PositionY only (X/Z free).");
        }
        finally
        {
            Object.DestroyImmediate(go);
            LogAssert.ignoreFailingMessages = prevIgnore;
        }
    }

    [Test]
    public void Read_EnabledSurfaceSnapDownLeft_StampsPositionXAndY()
    {
        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        var go = new GameObject("SurfaceSnapDownLeftNode");
        try
        {
            var snapper = go.AddComponent<SurfaceSnap>();
            snapper.down = true;
            snapper.left = true;

            var snapshot = SceneSnapshotReader.Read(go.scene);
            var node = FindNode(snapshot.Roots, "SurfaceSnapDownLeftNode");

            Assert.IsNotNull(node, "SurfaceSnapDownLeftNode not found in snapshot.");
            Assert.AreEqual(ChannelMask.PositionX | ChannelMask.PositionY, node.Transform.DrivenChannels,
                "A down+left SurfaceSnap must OR both axes into DrivenChannels.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            LogAssert.ignoreFailingMessages = prevIgnore;
        }
    }

    [Test]
    public void Read_PlainNode_NoSpatialComponent_StampsDrivenChannelsNone()
    {
        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        var go = new GameObject("PlainNode");
        try
        {
            var snapshot = SceneSnapshotReader.Read(go.scene);
            var node = FindNode(snapshot.Roots, "PlainNode");

            Assert.IsNotNull(node, "PlainNode not found in snapshot.");
            Assert.AreEqual(ChannelMask.None, node.Transform.DrivenChannels,
                "A node with no FitSize/SurfaceSnap must stamp DrivenChannels == None.");
        }
        finally
        {
            Object.DestroyImmediate(go);
            LogAssert.ignoreFailingMessages = prevIgnore;
        }
    }

    // ---- b6-t1: write-seam per-axis skip (PlanExecutor.ApplyTransformField) -----------------------
    // A SetField op carrying DrivenChannels must skip writing ONLY the driven axes (component-owned,
    // left at whatever the transform already holds), while free axes still write the op's value —
    // per-axis, not whole-vector, so a down-SurfaceSnap still lets X/Z write from source.

    [Test]
    public void PlanExecutor_MaskedSetField_SkipsDrivenAxes_WritesFreeAxes()
    {
        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        var scene = SceneManager.GetActiveScene();
        var plan = new SceneBuilder.Core.Plan.Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "n1", Name = "MaskedWriteNode" },
                new SetField
                {
                    LogicalId = "n1",
                    Path = "m_LocalScale",
                    Value = new ValueNode.Vec3(new Vec3(9f, 9f, 9f)),
                    DrivenChannels = ChannelMask.Scale,
                },
                new SetField
                {
                    LogicalId = "n1",
                    Path = "m_LocalPosition",
                    Value = new ValueNode.Vec3(new Vec3(9f, 9f, 9f)),
                    DrivenChannels = ChannelMask.PositionY,
                },
            },
        };

        GameObject go = null;
        try
        {
            PlanExecutor.Execute(plan, new IdentityMap(), scene);
            go = GameObject.Find("MaskedWriteNode");

            Assert.IsNotNull(go, "MaskedWriteNode was not created by PlanExecutor.Execute.");
            Assert.AreEqual(Vector3.one, go.transform.localScale,
                "A fully-driven Scale mask must leave localScale untouched by the op's value (component-owned).");
            Assert.AreEqual(9f, go.transform.localPosition.x, Tol, "Free X axis must still be written from the plan.");
            Assert.AreEqual(0f, go.transform.localPosition.y, Tol, "Driven Y axis must be left untouched, not overwritten to the op's value.");
            Assert.AreEqual(9f, go.transform.localPosition.z, Tol, "Free Z axis must still be written from the plan.");
        }
        finally
        {
            if (go != null) Object.DestroyImmediate(go);
            LogAssert.ignoreFailingMessages = prevIgnore;
        }
    }
}
