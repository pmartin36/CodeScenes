using UnityEngine;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// Shared projected-extent kernel: how far a <see cref="Renderer"/>'s local bounds reach along an
    /// arbitrary world unit direction, accounting for the object's own rotation and scale. Degenerates
    /// to the world-AABB face half-extent for a world axis on an unrotated object. Also provides
    /// whole-hierarchy aggregation (root and all descendants) so a solver sizes/aligns/places by the
    /// combined bounds of a multi-mesh object, not just a mesh/renderer on its own root GameObject.
    /// </summary>
    internal static class ProjectedExtent
    {
        /// <summary>Half-extent of <paramref name="r"/>'s local bounds projected onto the unit world
        /// direction <paramref name="d"/>.</summary>
        public static float HalfExtentAlong(Renderer r, Vector3 d)
        {
            Transform t = r.transform;
            return HalfExtentAlong(r.localBounds, t.rotation, t.lossyScale, d);
        }

        /// <summary>Half-extent of a mesh's <paramref name="localBounds"/> — under an explicit
        /// <paramref name="rotation"/>/<paramref name="scale"/> rather than a live <see cref="Renderer"/> —
        /// projected onto the unit world direction <paramref name="d"/>. Lets a caller (e.g. FitSize's
        /// mesh-bounds solve) measure at unit localScale without dividing by the object's own scale.</summary>
        public static float HalfExtentAlong(Bounds localBounds, Quaternion rotation, Vector3 scale, Vector3 d)
        {
            Vector3 axisX = (rotation * Vector3.right) * (localBounds.extents.x * scale.x);
            Vector3 axisY = (rotation * Vector3.up) * (localBounds.extents.y * scale.y);
            Vector3 axisZ = (rotation * Vector3.forward) * (localBounds.extents.z * scale.z);

            return Mathf.Abs(Vector3.Dot(axisX, d))
                 + Mathf.Abs(Vector3.Dot(axisY, d))
                 + Mathf.Abs(Vector3.Dot(axisZ, d));
        }

        /// <summary>Union of every <see cref="MeshFilter"/>.sharedMesh.bounds in <paramref name="root"/>'s
        /// hierarchy (root included), each transformed into ROOT-LOCAL space, encapsulated into one
        /// <see cref="Bounds"/>. Independent of root's own rotation/scale/position. Returns false iff no
        /// MeshFilter with a non-null mesh exists anywhere in the hierarchy.</summary>
        public static bool TryCombinedLocalMeshBounds(Transform root, out Bounds localBounds)
        {
            var meshFilters = root.GetComponentsInChildren<MeshFilter>();
            localBounds = default;
            bool any = false;

            foreach (var mf in meshFilters)
            {
                if (mf.sharedMesh == null) continue;

                Bounds meshLocal = mf.sharedMesh.bounds;
                // The mesh's own MeshFilter is the root itself: the root-local transform is the
                // identity, so use the mesh bounds directly rather than round-tripping it through
                // world space (keeps a single-mesh root's result byte-identical to sharedMesh.bounds).
                Bounds inRootLocal = mf.transform == root ? meshLocal : TransformBoundsIntoRootLocal(root, mf.transform, meshLocal);

                if (!any)
                {
                    localBounds = inRootLocal;
                    any = true;
                }
                else
                {
                    localBounds.Encapsulate(inRootLocal);
                }
            }

            return any;
        }

        /// <summary>Transforms <paramref name="meshLocalBounds"/> (in <paramref name="child"/>'s own
        /// local space) into <paramref name="root"/>'s local space by encapsulating its 8 corners under
        /// <c>root.worldToLocalMatrix * child.localToWorldMatrix</c>.</summary>
        private static Bounds TransformBoundsIntoRootLocal(Transform root, Transform child, Bounds meshLocalBounds)
        {
            Matrix4x4 childToRootLocal = root.worldToLocalMatrix * child.localToWorldMatrix;
            Vector3 c = meshLocalBounds.center;
            Vector3 e = meshLocalBounds.extents;

            Bounds result = default;
            bool first = true;
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Vector3 corner = c + new Vector3(sx * e.x, sy * e.y, sz * e.z);
                Vector3 transformed = childToRootLocal.MultiplyPoint3x4(corner);
                if (first)
                {
                    result = new Bounds(transformed, Vector3.zero);
                    first = false;
                }
                else
                {
                    result.Encapsulate(transformed);
                }
            }

            return result;
        }

        /// <summary>Rotation-aware union projection of every <see cref="Renderer"/> in <paramref name="root"/>'s
        /// hierarchy (root included) onto the unit world direction <paramref name="d"/>: the union of each
        /// renderer's own projected interval. Returns false iff no Renderer exists anywhere in the
        /// hierarchy.</summary>
        public static bool TryCombinedProjection(Transform root, Vector3 d, out float center, out float halfExtent)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            center = 0f;
            halfExtent = 0f;
            if (renderers.Length == 0) return false;

            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;

            foreach (var r in renderers)
            {
                float c = Vector3.Dot(r.bounds.center, d);
                float h = HalfExtentAlong(r, d);
                float lo = c - h;
                float hi = c + h;
                if (lo < min) min = lo;
                if (hi > max) max = hi;
            }

            center = (min + max) * 0.5f;
            halfExtent = (max - min) * 0.5f;
            return true;
        }

        /// <summary>Encapsulating world-space AABB of every <see cref="Renderer"/> in <paramref name="root"/>'s
        /// hierarchy (root included). Returns false iff no Renderer exists anywhere in the hierarchy.</summary>
        public static bool TryCombinedWorldBounds(Transform root, out Bounds worldBounds)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            worldBounds = default;
            if (renderers.Length == 0) return false;

            worldBounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) worldBounds.Encapsulate(renderers[i].bounds);
            return true;
        }
    }
}
