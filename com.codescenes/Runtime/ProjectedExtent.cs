using UnityEngine;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// Shared projected-extent kernel: how far a <see cref="Renderer"/>'s local bounds reach along an
    /// arbitrary world unit direction, accounting for the object's own rotation and scale. Degenerates
    /// to the world-AABB face half-extent for a world axis on an unrotated object.
    /// </summary>
    public static class ProjectedExtent
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
    }
}
