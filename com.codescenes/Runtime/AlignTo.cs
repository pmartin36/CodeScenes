using System.ComponentModel;
using UnityEngine;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// Editor-time (and play-mode-guarded) alignment. Drives <c>transform.position</c> on the set
    /// axes so the combined bounds of every <see cref="Renderer"/> in the object's hierarchy (root and
    /// descendants) land against a resolved surface — an explicit <see cref="target"/>'s extent
    /// (abut/align, per axis, itself resolved from the target's own combined hierarchy bounds), or
    /// (with no target) a raycast hit / collider-less fallback scan — independent of the object's own
    /// pivot.
    /// </summary>
    /// <remarks>
    /// Add it from a builder with <see cref="NodeHandle.AlignTo"/> or in the inspector. It runs in
    /// edit mode only, and re-snaps when the target/frame underneath moves. Drag the object further
    /// than <see cref="captureThreshold"/> to detach it deliberately. Axes you leave unset are never
    /// touched, so an object can align down to a floor while staying free to move horizontally. Each
    /// axis resolves in the target's local space by default, a <see cref="frame"/> override's local
    /// space, or world space (<see cref="AlignSpace.World"/>).
    /// </remarks>
    // Serialized field names are the write contract: they must equal
    // SceneBuilder.Core.Model.SpatialComponents.AlignToFields.* so Materialize's by-name write
    // hits the right field.
    [ExecuteAlways]
    [DefaultExecutionOrder(-90)] // after FitSize(-100): snaps the post-resize size
    public sealed class AlignTo : MonoBehaviour, IPositionDriver
    {
        private const float RayMargin = 0.05f;
        private const float RayMaxDistance = 10000f;
        private const float SideEpsilon = 1e-3f;
        private const float MoveEpsilon = 1e-3f;

        // The per-axis alignment mode — the live write/read/dispatch contract
        // (SpatialComponents.AlignToEnums mirrors this type FullName/member names byte-for-byte).
        // None MUST stay index 0 (default-value pruning on read relies on it). AlignMin/AlignMax/
        // AlignCenter resolve against a target's extent (see ApplyAxis) and are a no-op with no
        // target set.
        public enum Mode { None, AbutMin, AbutMax, AlignMin, AlignMax, AlignCenter }

        public Mode xMode, yMode, zMode;
        public float xOffset, yOffset, zOffset;

        public Transform target;

        /// <summary>Overrides which transform's local axes an alignment resolves in (default: the
        /// target's own local axes). Ignored when <see cref="space"/> is <see cref="AlignSpace.World"/>.</summary>
        public Transform frame;

        /// <summary>The reference frame each axis resolves in: the target's (or <see cref="frame"/>'s)
        /// local axes, or world axes.</summary>
        public AlignSpace space;

        /// <summary>World-unit drag distance (measured on aligned axes only) beyond which a manual move
        /// is treated as an intentional detach rather than a re-align. Sticky: once detached the
        /// component disables itself (see <see cref="Evaluate"/>) until re-enabled.</summary>
        public float captureThreshold = 2.5f;

        private bool _loggedError;
        private bool _loggedConflict;

        // Explicit impl: no new public/authoring member, so no DocGen/reflection-contract change.
        AxisFlags IPositionDriver.ClaimedWorldAxes()
        {
            AxisFlags claim = AxisFlags.None;
            if (xMode != Mode.None) claim |= AxisFlags.X;
            if (yMode != Mode.None) claim |= AxisFlags.Y;
            if (zMode != Mode.None) claim |= AxisFlags.Z;
            return claim;
        }
        int IPositionDriver.PriorityOrder => -90;

        /// <summary>The last surface this component aligned against — used only to gate recompute
        /// (re-align when the surface itself moves) without a raycast every idle frame.</summary>
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

        // Plugin-internal coordination hook, public only because the editor assembly calls it across
        // the asmdef boundary. Forgets the last-self-write baseline and forces a fresh align on the
        // next Evaluate. Called on enable, and by PlanExecutor right after it writes m_LocalPosition
        // directly on this object's Transform (materialize always writes the full authored transform
        // per spec 23, including a frozen driven-channel placeholder — that write is the plugin's own,
        // not a user drag, so it must not count toward captureThreshold; the very next Evaluate
        // re-derives from this fresh baseline instead of sticky-detaching off a stale one).
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void ResetBaseline()
        {
            _lastWritten = new Vector3(float.NaN, float.NaN, float.NaN);
            _needsSnap = true;
        }

        private void OnValidate()
        {
            _loggedError = false;
            _loggedConflict = false;
            _needsSnap = true;
            Evaluate();
        }

        /// <summary>Recompute the position of each set axis so the corresponding bounds feature lands
        /// against the resolved surface for that axis (target extent alignment, or — with no target —
        /// raycast &gt; collider-less fallback scan, Abut modes only). Free (unset) axes are left
        /// untouched.</summary>
        public void Evaluate()
        {
            if (Application.isPlaying) return;
            if (!isActiveAndEnabled) return;

            if (!ProjectedExtent.TryCombinedWorldBounds(transform, out Bounds bounds))
            {
                if (!_loggedError)
                {
                    Debug.LogError($"[CodeScenes] AlignTo on '{name}' has no Renderer/mesh bounds to align.", this);
                    _loggedError = true;
                }
                return;
            }

            // Resolve which claimed axes are actually ours before the drag-detection math below, so a
            // yielded axis is neither counted toward the drag/detach distance nor written.
            var owned = PositionAuthority.ResolveOwned(this, this, out bool yielded);
            if (yielded && !_loggedConflict)
            {
                Debug.LogWarning($"[CodeScenes] AlignTo on '{name}' yields a contested axis to a higher-priority driver on the same object.", this);
                _loggedConflict = true;
            }

            bool axis0Set = xMode != Mode.None && (owned & AxisFlags.X) != 0;
            bool axis1Set = yMode != Mode.None && (owned & AxisFlags.Y) != 0;
            bool axis2Set = zMode != Mode.None && (owned & AxisFlags.Z) != 0;

            if (HasWrittenBefore)
            {
                Vector3 current = transform.position;
                float dx = axis0Set ? current.x - _lastWritten.x : 0f;
                float dy = axis1Set ? current.y - _lastWritten.y : 0f;
                float dz = axis2Set ? current.z - _lastWritten.z : 0f;
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

                    // Within threshold: fall through and re-align (constraint wins).
                }
            }

            Physics.SyncTransforms();

            Vector3 pos = transform.position;
            Transform lastSurface = null;

            if (axis0Set) ApplyAxis(bounds, 0, xMode, xOffset, ref pos, ref lastSurface);
            if (axis1Set) ApplyAxis(bounds, 1, yMode, yOffset, ref pos, ref lastSurface);
            if (axis2Set) ApplyAxis(bounds, 2, zMode, zOffset, ref pos, ref lastSurface);

            transform.position = pos;
            _lastWritten = pos;
            if (lastSurface != null) _lastSurface = lastSurface;
            _needsSnap = false;
            transform.hasChanged = false;
        }

        /// <summary>Resolves one axis: extent alignment against <see cref="target"/> along the
        /// resolved frame axis when a target is set, else the no-target world-axis raycast/fallback
        /// scan (Abut modes only). Applies the flush-plus-offset delta to <paramref name="pos"/> on
        /// that axis only.</summary>
        private void ApplyAxis(Bounds bounds, int axis, Mode mode, float offset, ref Vector3 pos, ref Transform lastSurface)
        {
            if (target != null)
            {
                Vector3 d = FrameAxisDir(axis);
                if (!ProjectedExtent.TryCombinedProjection(transform, d, out float cSelf, out float hs)) return;
                if (!ProjectedExtent.TryCombinedProjection(target, d, out float cTgt, out float ht)) return;

                float cNew = mode switch
                {
                    Mode.AbutMin => cTgt - ht - hs, // my MAX face -> target MIN face
                    Mode.AbutMax => cTgt + ht + hs, // my MIN face -> target MAX face
                    Mode.AlignMin => cTgt - ht + hs, // my MIN face -> target MIN face (near faces flush)
                    Mode.AlignMax => cTgt + ht - hs, // my MAX face -> target MAX face (far faces flush)
                    Mode.AlignCenter => cTgt, // my center -> target center
                    _ => cSelf,
                };
                cNew += offset;

                pos += (cNew - cSelf) * d;
                lastSurface = target;
                return;
            }

            // No target: Align* modes need a target's extent to resolve against and are inert without
            // one (authored scenes can never reach this — the recognizer rejects Align*-without-target
            // — kept here so a directly-constructed component with no target stays total).
            if (mode is Mode.AlignMin or Mode.AlignMax or Mode.AlignCenter) return;

            // Abut modes only, unchanged world-axis raycast/fallback-scan surface resolution (AbutMin
            // looks in the world's +axis direction, AbutMax in -axis).
            int dirSign = mode == Mode.AbutMin ? 1 : -1;
            ResolveAndApplyAxisWorld(bounds, axis, dirSign, offset, ref pos, ref lastSurface);
        }

        /// <summary>The unit direction axis <paramref name="axis"/> (0=X, 1=Y, 2=Z) resolves along:
        /// world axes when <see cref="space"/> is <see cref="AlignSpace.World"/>; else the local axes
        /// of <see cref="frame"/> when set, else <see cref="target"/>'s own local axes; else world
        /// (no target, no frame — unreachable from <see cref="ApplyAxis"/>'s target-set branch, kept
        /// as a safe fallback).</summary>
        private Vector3 FrameAxisDir(int axis)
        {
            if (space == AlignSpace.World) return AxisDir(axis);

            Transform fr = frame != null ? frame : target;
            if (fr != null) return axis == 0 ? fr.right : axis == 1 ? fr.up : fr.forward;

            return AxisDir(axis);
        }

        /// <summary>Resolves the surface for one axis/direction via raycast (falling back to a
        /// collider-less scene scan) and applies the flush-plus-offset delta to <paramref name="pos"/>
        /// on that axis only (the whole world AABB translates by it, so the face lands exactly on the
        /// surface regardless of pivot). No move if no surface resolves.</summary>
        private void ResolveAndApplyAxisWorld(Bounds bounds, int axis, int dirSign, float offset, ref Vector3 pos, ref Transform lastSurface)
        {
            ProjectedExtent.TryCombinedProjection(transform, AxisDir(axis), out _, out float half);
            float faceCoord = bounds.center[axis] + dirSign * half;

            float? surface = RaycastSurface(bounds, axis, dirSign, out var surfaceTransform);

            if (surface == null)
            {
                surface = FallbackScanSurface(bounds, axis, dirSign, faceCoord, out surfaceTransform);
            }

            if (surface.HasValue)
            {
                Vector3 delta = Vector3.zero;
                // Offset is a uniform world-unit push in the frame axis's POSITIVE direction,
                // regardless of which side the surface was found on.
                delta[axis] = surface.Value - faceCoord + offset;
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

        private static Vector3 AxisDir(int axis) => axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;

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
