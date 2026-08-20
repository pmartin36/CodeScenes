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
            Bounds lb = r.localBounds;
            Vector3 ls = t.lossyScale;

            Vector3 axisX = t.right * (lb.extents.x * ls.x);
            Vector3 axisY = t.up * (lb.extents.y * ls.y);
            Vector3 axisZ = t.forward * (lb.extents.z * ls.z);

            return Mathf.Abs(Vector3.Dot(axisX, d))
                 + Mathf.Abs(Vector3.Dot(axisY, d))
                 + Mathf.Abs(Vector3.Dot(axisZ, d));
        }
    }
}
