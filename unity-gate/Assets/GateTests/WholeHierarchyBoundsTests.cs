using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using SceneBuilder.Authoring;

// Whole-hierarchy combined bounds: FitSize/AlignTo/Between size and place by the union of every
// renderer/mesh in an object's hierarchy, not just a renderer/mesh sitting on the solver's own
// GameObject, and a single-renderer root's result stays byte-identical to the pre-aggregation value.
public class WholeHierarchyBoundsTests
{
    private const float Tol = 1e-3f;

    private static GameObject NewCube(string name, Transform parent, Vector3 localPos)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, worldPositionStays: false);
        go.transform.localPosition = localPos;
        return go;
    }

    private static Bounds CombinedRendererWorldBounds(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>();
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }

    [Test]
    public void FitSize_MultiRendererChildHierarchy_SizesByCombinedExtent()
    {
        var root = new GameObject("Prop");
        NewCube("Left", root.transform, new Vector3(-1f, 0f, 0f));
        NewCube("Right", root.transform, new Vector3(1f, 0f, 0f));

        var fitSize = root.AddComponent<FitSize>();
        fitSize.mode = FitSize.Mode.Width;
        fitSize.value = 2f;

        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            fitSize.Evaluate();
        }
        finally
        {
            LogAssert.ignoreFailingMessages = prevIgnore;
        }

        float combinedWidth = CombinedRendererWorldBounds(root).size.x;
        Assert.AreEqual(2f, combinedWidth, Tol,
            "FitSize must drive the WHOLE hierarchy's combined world width to the authored value, not leave an unsized root untouched.");

        UnityEngine.Object.DestroyImmediate(root);
    }

    [Test]
    public void SingleMeshRoot_CombinedLocalMeshBounds_ByteIdenticalToMeshFilterBounds()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            go.transform.localScale = new Vector3(2f, 3f, 4f);
            go.transform.rotation = Quaternion.Euler(0f, 30f, 15f);
            var mf = go.GetComponent<MeshFilter>();

            bool hasLocal = ProjectedExtent.TryCombinedLocalMeshBounds(go.transform, out Bounds localBounds);

            Assert.IsTrue(hasLocal, "A single-mesh root must report a combined local mesh bounds.");
            Assert.AreEqual(mf.sharedMesh.bounds.min, localBounds.min,
                "A single-mesh root's combined LOCAL mesh bounds must be byte-identical to its own MeshFilter.sharedMesh.bounds.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void SingleMeshRoot_CombinedWorldBounds_ByteIdenticalToRendererBounds()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            go.transform.localScale = new Vector3(2f, 3f, 4f);
            go.transform.rotation = Quaternion.Euler(0f, 30f, 15f);
            var r = go.GetComponent<Renderer>();

            bool hasWorld = ProjectedExtent.TryCombinedWorldBounds(go.transform, out Bounds worldBounds);

            Assert.IsTrue(hasWorld, "A single-renderer root must report a combined world bounds.");
            Assert.AreEqual(r.bounds.min, worldBounds.min,
                "A single-renderer root's combined world bounds must be byte-identical to its own Renderer.bounds.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void SingleMeshRoot_CombinedProjection_ByteIdenticalToHalfExtentAlong()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            go.transform.localScale = new Vector3(2f, 3f, 4f);
            go.transform.rotation = Quaternion.Euler(0f, 30f, 15f);
            var r = go.GetComponent<Renderer>();
            var d = new Vector3(0.3f, 0.6f, 0.74f).normalized;

            bool hasProjection = ProjectedExtent.TryCombinedProjection(go.transform, d, out float center, out float halfExtent);

            Assert.IsTrue(hasProjection, "A single-renderer root must report a combined projection.");
            Assert.AreEqual(ProjectedExtent.HalfExtentAlong(r, d), halfExtent, Tol,
                "A single-renderer root's combined projection half-extent must be byte-identical to HalfExtentAlong(r, d).");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void AlignTo_MultiRendererChildHierarchy_RestsByCombinedBottom()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Floor";
        floor.transform.localScale = new Vector3(10f, 1f, 10f);
        floor.transform.position = Vector3.zero;

        var prop = new GameObject("Prop");
        NewCube("Top", prop.transform, new Vector3(0f, 1f, 0f));
        NewCube("Bottom", prop.transform, new Vector3(0f, -1f, 0f));
        prop.transform.position = new Vector3(0f, 5f, 0f);

        var alignTo = prop.AddComponent<AlignTo>();
        alignTo.target = floor.transform;
        alignTo.yMode = AlignTo.Mode.AbutMax;

        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            alignTo.Evaluate();
        }
        finally
        {
            LogAssert.ignoreFailingMessages = prevIgnore;
        }

        float floorTop = floor.GetComponent<Renderer>().bounds.max.y;
        float combinedBottom = CombinedRendererWorldBounds(prop).min.y;

        Assert.AreEqual(floorTop, combinedBottom, Tol,
            "AlignTo must rest the WHOLE hierarchy's combined bottom flush on the target, not leave a Renderer-less root untouched.");

        UnityEngine.Object.DestroyImmediate(floor);
        UnityEngine.Object.DestroyImmediate(prop);
    }

    [Test]
    public void AlignTo_NestedMeshTarget_ResolvesAgainstTargetChildBounds()
    {
        var target = new GameObject("Target");
        NewCube("TargetMesh", target.transform, new Vector3(0f, 2f, 0f));
        target.transform.position = Vector3.zero;

        var mover = GameObject.CreatePrimitive(PrimitiveType.Cube);
        mover.name = "Mover";
        mover.transform.position = new Vector3(0f, 20f, 0f);

        var alignTo = mover.AddComponent<AlignTo>();
        alignTo.target = target.transform;
        alignTo.yMode = AlignTo.Mode.AbutMin; // mover's max face -> target's combined min face

        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            alignTo.Evaluate();
        }
        finally
        {
            LogAssert.ignoreFailingMessages = prevIgnore;
        }

        float targetCombinedBottom = CombinedRendererWorldBounds(target).min.y;
        float moverTop = mover.GetComponent<Renderer>().bounds.max.y;

        Assert.AreEqual(targetCombinedBottom, moverTop, Tol,
            "AlignTo must resolve against the TARGET's combined hierarchy bounds when the target's own root carries no Renderer.");

        UnityEngine.Object.DestroyImmediate(target);
        UnityEngine.Object.DestroyImmediate(mover);
    }

    [Test]
    public void Between_SelfNestedMeshHierarchy_PlacesByCombinedSelfExtent()
    {
        var from = GameObject.CreatePrimitive(PrimitiveType.Cube);
        from.name = "From";
        from.transform.position = new Vector3(-10f, 0f, 0f);

        var to = GameObject.CreatePrimitive(PrimitiveType.Cube);
        to.name = "To";
        to.transform.position = new Vector3(10f, 0f, 0f);

        var self = new GameObject("Self");
        NewCube("SelfMeshA", self.transform, new Vector3(-1f, 0f, 0f));
        NewCube("SelfMeshB", self.transform, new Vector3(1f, 0f, 0f));
        self.transform.position = new Vector3(0f, 5f, 0f);

        var between = self.AddComponent<Between>();
        between.from = from.transform;
        between.to = to.transform;
        between.axis = Between.Axis.X;
        between.fraction = 0f; // self's combined MIN face flush against `from`'s MAX face

        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            between.Evaluate();
        }
        finally
        {
            LogAssert.ignoreFailingMessages = prevIgnore;
        }

        float fromRightFace = from.GetComponent<Renderer>().bounds.max.x;
        float selfCombinedMin = CombinedRendererWorldBounds(self).min.x;

        Assert.AreEqual(fromRightFace, selfCombinedMin, Tol,
            "Between must place by self's COMBINED hierarchy extent, not leave a Renderer-less self untouched.");

        UnityEngine.Object.DestroyImmediate(from);
        UnityEngine.Object.DestroyImmediate(to);
        UnityEngine.Object.DestroyImmediate(self);
    }

    [Test]
    public void YawRotatedNestedMesh_SolvesScaleFromTrueOrientedExtent()
    {
        // Fixture: two unit cubes at local (-1,0,0)/(1,0,0) give a combined LOCAL (unscaled, unrotated)
        // box of half-extents (1.5, 0.5, 0.5) centered at the root. Yawing the root 45 degrees about Y
        // mixes the local X and Z half-extents into world X by the standard oriented-projection formula
        // (independent of this suite's own ProjectedExtent code, so it is a true external reference),
        // which FitSize must solve width:2 against. The solved uniform scale is cross-checked on the
        // Y axis, which the yaw leaves untouched, so this independently confirms the combined bounds
        // magnitude (not merely that the driven X axis happened to hit its own target).
        var root = new GameObject("Prop");
        NewCube("Left", root.transform, new Vector3(-1f, 0f, 0f));
        NewCube("Right", root.transform, new Vector3(1f, 0f, 0f));
        root.transform.rotation = Quaternion.Euler(0f, 45f, 0f);

        var fitSize = root.AddComponent<FitSize>();
        fitSize.mode = FitSize.Mode.Width;
        fitSize.value = 2f;

        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            fitSize.Evaluate();
        }
        finally
        {
            LogAssert.ignoreFailingMessages = prevIgnore;
        }

        float theta = 45f * Mathf.Deg2Rad;
        float halfProjXAtUnitScale = Mathf.Abs(1.5f * Mathf.Cos(theta)) + Mathf.Abs(0.5f * Mathf.Sin(theta));
        float expectedScale = 2f / (2f * halfProjXAtUnitScale);
        float expectedHeight = 1f * expectedScale; // world Y extent is yaw-about-Y-invariant, at unit local height 1

        var combined = CombinedRendererWorldBounds(root);

        Assert.AreEqual(expectedHeight, combined.size.y, Tol,
            "FitSize must solve the applied uniform scale from the combined hierarchy's TRUE oriented extent (independently computed here), not leave the object unscaled.");

        UnityEngine.Object.DestroyImmediate(root);
    }
}
