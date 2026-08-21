using System.ComponentModel;
using UnityEngine;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// Editor-time (and play-mode-guarded) single-axis corridor placement. Drives
    /// <c>transform.position</c> along one axis so a sibling <see cref="Renderer"/>'s bounds land flush
    /// against <see cref="from"/> at fraction 0 and flush against <see cref="to"/> at fraction 1,
    /// independent of the object's own pivot.
    /// </summary>
    // Serialized field names are the write contract: they must equal
    // SceneBuilder.Core.Model.SpatialComponents.BetweenFields.* so Materialize's by-name write hits
    // the right field.
    //
    // Split as `partial` so the nested Axis enum (Between.Axis.cs, no UnityEngine dependency) can
    // stand alone as an authoring-facing type: NodeHandle.Between's signature takes Axis directly
    // (unlike FitSize/SurfaceSnap, which take bools), and AuthoringBindHarness compiles the real
    // com.codescenes/Runtime authoring surface excluding every file that needs the UnityEngine
    // assembly it does not reference (SceneBuilder.Core.Tests/AuthoringBindHarness.cs) — keeping
    // Axis in a UnityEngine-free partial file lets that harness resolve it while this file (the
    // MonoBehaviour body) stays excluded as before.
    [ExecuteAlways]
    [DefaultExecutionOrder(-80)] // after SurfaceSnap(-90): places relative to already-snapped anchors
    public sealed partial class Between : MonoBehaviour, IPositionDriver
    {
        private const float Epsilon = 1e-4f;
        private const float AxisEps = 1e-4f;

        public Transform from;
        public Transform to;
        public Transform orientation;
        public float fraction;
        public Axis axis;

        /// <summary>The last <c>position</c> this component wrote — used to discriminate our own
        /// writes from a manual drag. Sentinel (NaN.x) means "never written".</summary>
        private Vector3 _lastWritten = new Vector3(float.NaN, float.NaN, float.NaN);

        private bool _needsEval = true;
        private bool _loggedError;
        private bool _loggedWarning;
        private bool _loggedConflict;

        // Explicit impl: no new public/authoring member, so no DocGen/reflection-contract change.
        AxisFlags IPositionDriver.ClaimedWorldAxes() => ComputeClaim();
        int IPositionDriver.PriorityOrder => -80;

        private bool HasWrittenBefore => !float.IsNaN(_lastWritten.x);

        private void Update()
        {
            if (_needsEval || transform.hasChanged
                || (from != null && from.hasChanged)
                || (to != null && to.hasChanged)
                || (orientation != null && orientation.hasChanged))
            {
                Evaluate();
            }
        }

        private void OnEnable() => ResetBaseline();

        // Plugin-internal coordination hook, public only because the editor assembly calls it across
        // the asmdef boundary. Forgets the last-self-write baseline and forces a fresh eval on the
        // next Evaluate. Called on enable, and by PlanExecutor right after it writes m_LocalPosition
        // directly on this object's Transform (materialize always writes the full authored transform
        // per spec 23 — that write is the plugin's own, not a user drag, mirroring
        // SurfaceSnap.ResetBaseline for the same m_LocalPosition case).
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void ResetBaseline()
        {
            _lastWritten = new Vector3(float.NaN, float.NaN, float.NaN);
            _needsEval = true;
        }

        private void OnValidate()
        {
            _loggedError = false;
            _loggedWarning = false;
            _loggedConflict = false;
            _needsEval = true;
            Evaluate();
        }

        /// <summary>Recompute <c>transform.position</c> along the configured axis so self's bounds
        /// land flush against <see cref="from"/> at fraction 0 and flush against <see cref="to"/> at
        /// fraction 1 (unclamped past either end). The axis is a world direction by default, or the
        /// local axis of <see cref="orientation"/> when set. Only the position component along that
        /// axis is touched.</summary>
        public void Evaluate()
        {
            if (Application.isPlaying) return;
            if (!isActiveAndEnabled) return;

            // Not yet configured (e.g. immediately after AddComponent, before an anchor is assigned):
            // a no-op, mirroring FitSize's Mode.None default — from/to are required to place anything.
            if (from == null || to == null) return;

            if (from == transform || to == transform || orientation == transform)
            {
                if (!_loggedError)
                {
                    Debug.LogError($"[CodeScenes] Between on '{name}' references itself as an anchor/orientation.", this);
                    _loggedError = true;
                }
                return;
            }

            var selfRenderer = GetComponent<Renderer>();
            var fromRenderer = from != null ? from.GetComponent<Renderer>() : null;
            var toRenderer = to != null ? to.GetComponent<Renderer>() : null;

            if (selfRenderer == null || fromRenderer == null || toRenderer == null)
            {
                if (!_loggedError)
                {
                    Debug.LogError($"[CodeScenes] Between on '{name}' has no Renderer/mesh bounds on itself or an anchor.", this);
                    _loggedError = true;
                }
                return;
            }

            Vector3 d = AxisDirection();

            float cf = Vector3.Dot(fromRenderer.bounds.center, d);
            float ct = Vector3.Dot(toRenderer.bounds.center, d);
            float cself = Vector3.Dot(selfRenderer.bounds.center, d);

            float ef = ProjectedExtent.HalfExtentAlong(fromRenderer, d);
            float et = ProjectedExtent.HalfExtentAlong(toRenderer, d);
            float es = ProjectedExtent.HalfExtentAlong(selfRenderer, d);

            float s = ct >= cf ? 1f : -1f;

            float fromInnerFace = cf + s * ef;
            float toInnerFace = ct - s * et;

            float ps0 = fromInnerFace + s * es;
            float ps1 = toInnerFace - s * es;

            // Between moves self along a single vector `d`; if it lost ANY world axis it claimed to a
            // higher-priority sibling driver it cannot cleanly place, so it fully defers -- no forward
            // write AND no back-solve, so the sibling's write to the shared axis is never misread as a
            // user drag and never corrupts `fraction`.
            PositionAuthority.ResolveOwned(this, this, out bool yielded);
            if (yielded)
            {
                if (!_loggedConflict)
                {
                    Debug.LogWarning($"[CodeScenes] Between on '{name}' yields its position axis to a higher-priority driver on the same object; not placing.", this);
                    _loggedConflict = true;
                }
                _needsEval = false;
                transform.hasChanged = false;
                return;
            }

            if (HasWrittenBefore
                && (transform.position - _lastWritten).sqrMagnitude > Epsilon * Epsilon)
            {
                // User dragged since our last write. Back-solve `fraction` from the axis projection;
                // the perpendicular components are ignored by the projection, so an off-axis drag
                // leaves `fraction` unchanged and its free position untouched (never re-driven, never
                // written to source). Only `fraction` is written back — the raw axis channel is
                // driven-masked out of scene->code by Reconciler.MaskDriven, exactly as FitSize
                // back-solves value/size rather than scale.
                float travel = ps1 - ps0;
                if (!Mathf.Approximately(travel, 0f))
                {
                    fraction = (cself - ps0) / travel;
                }

                _lastWritten = transform.position;
                _needsEval = false;
                transform.hasChanged = false;
                return;
            }

            float corridorLength = Mathf.Abs(ct - cf) - ef - et;
            float selfFullExtent = 2f * es;
            if (corridorLength < selfFullExtent && !_loggedWarning)
            {
                Debug.LogWarning($"[CodeScenes] Between on '{name}' has a degenerate corridor (self's extent exceeds the gap between anchors); placing anyway.", this);
                _loggedWarning = true;
            }

            float targetCenterProj = ps0 + fraction * (ps1 - ps0);
            float delta = targetCenterProj - cself;

            transform.position += delta * d;

            _lastWritten = transform.position;
            _needsEval = false;
            transform.hasChanged = false;
        }

        /// <summary>The world axes this Between will write on its next Evaluate: a world placement
        /// (no <see cref="orientation"/>) claims only the single named <see cref="axis"/>; an
        /// orientation-following placement claims every world axis its tilt-carrying direction
        /// actually touches (<see cref="AxisDirection"/>), which can differ from the named axis once
        /// tilted (e.g. a yawed frame's local X reaching into world X and Z).</summary>
        private AxisFlags ComputeClaim()
        {
            if (from == null || to == null) return AxisFlags.None;

            if (orientation == null)
            {
                switch (axis)
                {
                    case Axis.X: return AxisFlags.X;
                    case Axis.Y: return AxisFlags.Y;
                    default: return AxisFlags.Z;
                }
            }

            Vector3 d = AxisDirection();
            AxisFlags claim = AxisFlags.None;
            if (Mathf.Abs(d.x) > AxisEps) claim |= AxisFlags.X;
            if (Mathf.Abs(d.y) > AxisEps) claim |= AxisFlags.Y;
            if (Mathf.Abs(d.z) > AxisEps) claim |= AxisFlags.Z;
            return claim;
        }

        /// <summary>The unit world direction the configured <see cref="axis"/> names: a world axis by
        /// default, or the matching local axis of <see cref="orientation"/> when set.</summary>
        private Vector3 AxisDirection()
        {
            if (orientation != null)
            {
                switch (axis)
                {
                    case Axis.X: return orientation.right;
                    case Axis.Y: return orientation.up;
                    default: return orientation.forward;
                }
            }

            switch (axis)
            {
                case Axis.X: return Vector3.right;
                case Axis.Y: return Vector3.up;
                default: return Vector3.forward;
            }
        }
    }
}
