using System.ComponentModel;
using UnityEngine;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// Editor-time (and play-mode-guarded) world-size solver. Drives <c>transform.localScale</c> from
    /// a sibling <see cref="MeshFilter"/>'s local bounds so an authored width/height/depth (aspect-locked)
    /// or explicit per-axis <see cref="size"/> becomes an exact WORLD size, independent of the mesh's
    /// native dimensions, rotation, or a scaled parent.
    /// </summary>
    /// <remarks>
    /// Add it from a builder with <see cref="NodeHandle.FitSize(float?, float?, float?)"/> or in the
    /// inspector. It runs in edit mode only. Resize the object by hand and it reads the new world size
    /// back into <see cref="value"/> / <see cref="size"/>, so the size you drag to is the size your
    /// builder file ends up saying.
    /// </remarks>
    // Serialized field names are the write contract: they must equal
    // SceneBuilder.Core.Model.SpatialComponents.FitSizeFields.* so Materialize's by-name write hits
    // the right field.
    [ExecuteAlways]
    [DefaultExecutionOrder(-100)]
    public sealed class FitSize : MonoBehaviour
    {
        private const float Epsilon = 1e-4f;

        /// <summary>Which dimension drives the size. <c>None</c> is the default and drives nothing, so
        /// a freshly added FitSize leaves the object alone until you pick a mode.</summary>
        // None must stay index 0: default-value pruning on read relies on it.
        public enum Mode { None, Width, Height, Depth, Explicit }

        public Mode mode = Mode.None;

        /// <summary>The single authored aspect-locked dimension when <see cref="mode"/> is
        /// Width/Height/Depth. Unused (and unwritten) for Explicit/None.</summary>
        public float value;

        /// <summary>Explicit per-axis world size when <see cref="mode"/> is Explicit.</summary>
        public Vector3 size;

        /// <summary>The last <c>localScale</c> this component wrote — used to discriminate our own
        /// writes from a manual scale edit by the user. Sentinel (NaN.x) means "never written".</summary>
        private Vector3 _lastWritten = new Vector3(float.NaN, float.NaN, float.NaN);

        private bool _loggedError;
        private bool _loggedWarning;

        private bool HasWrittenBefore => !float.IsNaN(_lastWritten.x);

        private void Update() => Evaluate();

        private void OnEnable() => ResetBaseline();

        // Plugin-internal coordination hook, public only because the editor assembly calls it across
        // the asmdef boundary. Forgets the last-self-write baseline so the next Evaluate re-derives
        // localScale from the intent instead of mistaking a fresh write for a manual rescale. Called
        // on enable, and by PlanExecutor right after it writes m_LocalScale directly on this object's
        // Transform (materialize always writes the full authored transform per spec 23 — that write is
        // the plugin's own, not a user drag, so it must not be back-solved into source as a wrong
        // value/size). Mirrors AlignTo.ResetBaseline for the m_LocalPosition case.
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void ResetBaseline() => _lastWritten = new Vector3(float.NaN, float.NaN, float.NaN);

        private void OnValidate()
        {
            _loggedError = false;
            _loggedWarning = false;
            Evaluate();
        }

        /// <summary>Recompute <c>localScale</c> from the current mesh bounds / intent, or (if the
        /// user manually changed <c>localScale</c> since our last write) back-solve the intent
        /// field(s) from the new world size instead.</summary>
        public void Evaluate()
        {
            if (Application.isPlaying) return;
            if (!isActiveAndEnabled) return;
            if (mode == Mode.None) return;

            var mf = GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
            {
                if (!_loggedError)
                {
                    Debug.LogError($"[CodeScenes] FitSize on '{name}' has no MeshFilter/mesh to size.", this);
                    _loggedError = true;
                }
                return;
            }

            Vector3 local = mf.sharedMesh.bounds.size;
            Vector3 pls = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
            int drivingAxis = DrivingAxis();

            if (HasWrittenBefore && (transform.localScale - _lastWritten).sqrMagnitude > Epsilon * Epsilon)
            {
                // The user moved localScale directly since we last drove it — treat it as a manual
                // edit and back-solve the authored intent field(s) from the new world size. The raw
                // localScale channel is never written back to source; only value/size are.
                Vector3 lossy = transform.lossyScale;
                Vector3 world = new Vector3(local.x * lossy.x, local.y * lossy.y, local.z * lossy.z);

                if (drivingAxis >= 0)
                {
                    value = world[drivingAxis];
                }
                else
                {
                    size = world;
                }

                _lastWritten = transform.localScale;
                return;
            }

            Vector3 newScale = transform.localScale;
            if (drivingAxis >= 0)
            {
                float denom = 2f * ProjectedExtent.HalfExtentAlong(mf.sharedMesh.bounds, transform.rotation, pls, AxisDir(drivingAxis));
                if (Mathf.Approximately(denom, 0f))
                {
                    WarnDegenerate();
                    return;
                }

                float s = value / denom;
                newScale = new Vector3(s, s, s);
            }
            else
            {
                bool anyDegenerate = false;
                for (int i = 0; i < 3; i++)
                {
                    float denom = 2f * ProjectedExtent.HalfExtentAlong(mf.sharedMesh.bounds, transform.rotation, pls, AxisDir(i));
                    if (Mathf.Approximately(denom, 0f))
                    {
                        anyDegenerate = true;
                        continue;
                    }

                    newScale[i] = size[i] / denom;
                }

                if (anyDegenerate) WarnDegenerate();
            }

            transform.localScale = newScale;
            _lastWritten = newScale;
        }

        /// <summary>Index of the single authored aspect-locked axis (0=width/x, 1=height/y, 2=depth/z),
        /// or -1 when <see cref="mode"/> is Explicit (or None, guarded by <see cref="Evaluate"/>'s
        /// early-return above).</summary>
        private int DrivingAxis() =>
            mode switch
            {
                Mode.Width => 0,
                Mode.Height => 1,
                Mode.Depth => 2,
                _ => -1,
            };

        private static Vector3 AxisDir(int axis) => axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;

        private void WarnDegenerate()
        {
            if (_loggedWarning) return;
            Debug.LogWarning($"[CodeScenes] FitSize on '{name}' has degenerate bounds/scale on an authored axis; skipping.", this);
            _loggedWarning = true;
        }
    }
}
