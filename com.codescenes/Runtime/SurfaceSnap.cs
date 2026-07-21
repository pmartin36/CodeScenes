using UnityEngine;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// Editor-time (and play-mode-guarded) world-bounds snap. Drives <c>transform.position</c> on the
    /// set world axes so a sibling <see cref="Renderer"/>'s world bounds face lands flush against a
    /// resolved surface (raycast hit, collider-less fallback scan, or an explicit <see cref="target"/>
    /// override), independent of the object's own pivot.
    /// </summary>
    /// <remarks>
    /// Serialized field names are the real write contract — they MUST equal
    /// <c>SceneBuilder.Core.Model.SpatialComponents.SurfaceSnapFields.*</c> so Materialize's by-name write
    /// hits the right field.
    /// </remarks>
    [ExecuteAlways]
    [DefaultExecutionOrder(-90)] // after FitSize(-100): snaps the post-resize size
    public sealed class SurfaceSnap : MonoBehaviour
    {
        private const float RayMargin = 0.05f;
        private const float RayMaxDistance = 10000f;
        private const float SideEpsilon = 1e-3f;
        private const float MoveEpsilon = 1e-3f;

        // Per-axis enum fields — the live write/read/dispatch contract (SpatialComponents.
        // SurfaceSnapFields.Vertical/Horizontal/Depth + SurfaceSnapEnums mirror these type
        // FullNames/member names byte-for-byte). None MUST stay index 0 (default-value pruning
        // on read relies on it).
        public enum Vertical { None, Up, Down }
        public enum Horizontal { None, Left, Right }
        public enum Depth { None, Forward, Back }

        public Vertical vertical;
        public Horizontal horizontal;
        public Depth depth;

        public Transform target;

        /// <summary>World-unit drag distance (measured on snapped axes only) beyond which a manual move
        /// is treated as an intentional detach rather than a re-snap. Sticky: once detached the component
        /// disables itself (see <see cref="Evaluate"/>) until re-enabled.</summary>
        public float captureThreshold = 2.5f;

        private bool _loggedError;

        /// <summary>The last surface this component snapped against — used only to gate recompute
        /// (re-snap when the surface itself moves) without a raycast every idle frame.</summary>
        private Transform _lastSurface;
        private bool _needsSnap = true;

        /// <summary>The last position WE wrote — used to discriminate our own writes from a manual
        /// drag. Sentinel (NaN.x) means "never written".</summary>
        private Vector3 _lastWritten = new Vector3(float.NaN, float.NaN, float.NaN);

        private bool HasWrittenBefore => !float.IsNaN(_lastWritten.x);

        private void Update()
        {
            if (_needsSnap || transform.hasChanged || (_lastSurface != null && _lastSurface.hasChanged))
            {
                Evaluate();
            }
        }

        private void OnEnable() => ResetBaseline();

        /// <summary>Forgets the last-self-write baseline (NaN sentinel) and forces a fresh snap on the
        /// next <see cref="Evaluate"/>. Called on enable, and by <c>PlanExecutor</c> (code-&gt;scene)
        /// right after it writes <c>m_LocalPosition</c> directly on this object's <see cref="Transform"/>
        /// (materialize always writes the full authored transform per spec 23, including a frozen
        /// driven-channel placeholder — that write is the plugin's own, not a user drag, so it must not
        /// count toward <see cref="captureThreshold"/>; the very next Evaluate() re-derives from this
        /// fresh baseline instead of sticky-detaching off a stale in-memory baseline).</summary>
        public void ResetBaseline()
        {
            _lastWritten = new Vector3(float.NaN, float.NaN, float.NaN);
            _needsSnap = true;
        }

        private void OnValidate()
        {
            _loggedError = false;
            _needsSnap = true;
            Evaluate();
        }

        /// <summary>Recompute the position of each set axis so the corresponding bounds face lands
        /// flush against the resolved surface for that axis (target override &gt; raycast &gt;
        /// collider-less fallback scan). Free (unset) axes are left untouched.</summary>
        public void Evaluate()
        {
            if (Application.isPlaying) return;
            if (!isActiveAndEnabled) return;

            var r = GetComponent<Renderer>();
            if (r == null)
            {
                if (!_loggedError)
                {
                    Debug.LogError($"[CodeScenes] SurfaceSnap on '{name}' has no Renderer/mesh bounds to snap.", this);
                    _loggedError = true;
                }
                return;
            }

            bool axis0Snapped = horizontal != Horizontal.None;
            bool axis1Snapped = vertical != Vertical.None;
            bool axis2Snapped = depth != Depth.None;

            if (HasWrittenBefore)
            {
                Vector3 current = transform.position;
                float dx = axis0Snapped ? current.x - _lastWritten.x : 0f;
                float dy = axis1Snapped ? current.y - _lastWritten.y : 0f;
                float dz = axis2Snapped ? current.z - _lastWritten.z : 0f;
                float dragSq = dx * dx + dy * dy + dz * dz;

                if (dragSq > MoveEpsilon * MoveEpsilon)
                {
                    if (dragSq > captureThreshold * captureThreshold)
                    {
                        // Sticky detach: leave the object where it was dragged and stop driving it.
                        enabled = false;
                        _needsSnap = false;
                        transform.hasChanged = false;
                        return;
                    }

                    // Within threshold: fall through and re-snap (constraint wins).
                }
            }

            Physics.SyncTransforms();

            Bounds bounds = r.bounds;
            Vector3 pos = transform.position;
            Transform lastSurface = null;

            if (vertical == Vertical.Down) ResolveAndApplyAxis(bounds, 1, -1, ref pos, ref lastSurface);
            if (vertical == Vertical.Up) ResolveAndApplyAxis(bounds, 1, 1, ref pos, ref lastSurface);
            if (horizontal == Horizontal.Left) ResolveAndApplyAxis(bounds, 0, -1, ref pos, ref lastSurface);
            if (horizontal == Horizontal.Right) ResolveAndApplyAxis(bounds, 0, 1, ref pos, ref lastSurface);
            if (depth == Depth.Forward) ResolveAndApplyAxis(bounds, 2, 1, ref pos, ref lastSurface);
            if (depth == Depth.Back) ResolveAndApplyAxis(bounds, 2, -1, ref pos, ref lastSurface);

            transform.position = pos;
            _lastWritten = pos;
            if (lastSurface != null) _lastSurface = lastSurface;
            _needsSnap = false;
            transform.hasChanged = false;
        }

        /// <summary>Resolves the surface for one axis/direction and applies the flush delta to
        /// <paramref name="pos"/> on that axis only (the whole world AABB translates by it, so the
        /// face lands exactly on the surface regardless of pivot). No move if no surface resolves.</summary>
        private void ResolveAndApplyAxis(Bounds bounds, int axis, int dirSign, ref Vector3 pos, ref Transform lastSurface)
        {
            float faceCoord = dirSign < 0 ? bounds.min[axis] : bounds.max[axis];
            float? surface = null;
            Transform surfaceTransform = null;

            if (target != null)
            {
                var targetRenderer = target.GetComponent<Renderer>();
                if (targetRenderer != null)
                {
                    Bounds tb = targetRenderer.bounds;
                    surface = dirSign < 0 ? tb.max[axis] : tb.min[axis];
                    surfaceTransform = target;
                }
            }

            if (surface == null)
            {
                surface = RaycastSurface(bounds, axis, dirSign, out surfaceTransform);
            }

            if (surface == null)
            {
                surface = FallbackScanSurface(bounds, axis, dirSign, faceCoord, out surfaceTransform);
            }

            if (surface.HasValue)
            {
                Vector3 delta = Vector3.zero;
                delta[axis] = surface.Value - faceCoord;
                pos += delta;
                lastSurface = surfaceTransform;
            }
        }

        /// <summary>Casts a small grid (face centre + 4 face corners) along the outward direction, from
        /// an origin offset back beyond the object's opposite face so the ray does not start inside/on
        /// its own collider. Self/descendant hits are filtered out; among the remainder the flush-closest
        /// hit (nearest contact, so the object rests without penetrating it) wins.</summary>
        private float? RaycastSurface(Bounds bounds, int axis, int dirSign, out Transform hitTransform)
        {
            hitTransform = null;

            int a1 = (axis + 1) % 3;
            int a2 = (axis + 2) % 3;

            float faceOpposite = dirSign < 0 ? bounds.max[axis] : bounds.min[axis];
            float originAxisCoord = faceOpposite - dirSign * RayMargin;

            Vector3 dir = Vector3.zero;
            dir[axis] = dirSign;

            Vector3 basePoint = bounds.center;
            basePoint[axis] = originAxisCoord;

            Vector3[] grid = new Vector3[5];
            grid[0] = basePoint;

            Vector3 p = basePoint;
            p[a1] = bounds.min[a1]; p[a2] = bounds.min[a2];
            grid[1] = p;

            p = basePoint;
            p[a1] = bounds.min[a1]; p[a2] = bounds.max[a2];
            grid[2] = p;

            p = basePoint;
            p[a1] = bounds.max[a1]; p[a2] = bounds.min[a2];
            grid[3] = p;

            p = basePoint;
            p[a1] = bounds.max[a1]; p[a2] = bounds.max[a2];
            grid[4] = p;

            float? best = null;
            foreach (var origin in grid)
            {
                var hits = Physics.RaycastAll(origin, dir, RayMaxDistance);
                foreach (var hit in hits)
                {
                    var hitTf = hit.collider.transform;
                    if (hitTf == transform || hitTf.IsChildOf(transform)) continue;

                    float coord = hit.point[axis];
                    bool better = best == null || (dirSign < 0 ? coord > best.Value : coord < best.Value);
                    if (better)
                    {
                        best = coord;
                        hitTransform = hitTf;
                    }
                }
            }

            return best;
        }

        /// <summary>No non-self ray hit and no explicit target: scan every <see cref="Renderer"/> in the
        /// scene (excluding self/descendants), prefer candidates on the correct side of the face whose
        /// lateral extent overlaps the face; if none overlap laterally, fall back to the nearest
        /// correct-side candidate regardless of lateral extent. Approximate (AABB), per spec §Risks.</summary>
        private float? FallbackScanSurface(Bounds bounds, int axis, int dirSign, float faceCoord, out Transform hitTransform)
        {
            hitTransform = null;

            int a1 = (axis + 1) % 3;
            int a2 = (axis + 2) % 3;

            float? bestOverlap = null;
            Transform bestOverlapTf = null;
            float? bestAny = null;
            Transform bestAnyTf = null;

            // Unity 6000.5 deprecated EVERY FindObjectsByType overload that takes FindObjectsSortMode;
            // the fallback scan picks the nearest surface by distance, so sort order is irrelevant.
            var renderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude);
            foreach (var other in renderers)
            {
                var otherTf = other.transform;
                if (otherTf == transform || otherTf.IsChildOf(transform)) continue;

                Bounds ob = other.bounds;
                float candidate = dirSign < 0 ? ob.max[axis] : ob.min[axis];

                bool correctSide = dirSign < 0
                    ? candidate <= faceCoord + SideEpsilon
                    : candidate >= faceCoord - SideEpsilon;
                if (!correctSide) continue;

                if (bestAny == null || (dirSign < 0 ? candidate > bestAny.Value : candidate < bestAny.Value))
                {
                    bestAny = candidate;
                    bestAnyTf = otherTf;
                }

                bool overlapsLaterally =
                    !(ob.max[a1] < bounds.min[a1] || ob.min[a1] > bounds.max[a1]) &&
                    !(ob.max[a2] < bounds.min[a2] || ob.min[a2] > bounds.max[a2]);
                if (overlapsLaterally &&
                    (bestOverlap == null || (dirSign < 0 ? candidate > bestOverlap.Value : candidate < bestOverlap.Value)))
                {
                    bestOverlap = candidate;
                    bestOverlapTf = otherTf;
                }
            }

            if (bestOverlap.HasValue)
            {
                hitTransform = bestOverlapTf;
                return bestOverlap;
            }

            hitTransform = bestAnyTf;
            return bestAny;
        }
    }
}
