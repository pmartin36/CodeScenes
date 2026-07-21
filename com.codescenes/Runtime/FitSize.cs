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
    /// Serialized field names are the real write contract — they MUST equal
    /// <c>SceneBuilder.Core.Model.SpatialComponents.FitSizeFields.*</c> so Materialize's by-name write
    /// hits the right field.
    /// </remarks>
    [ExecuteAlways]
    [DefaultExecutionOrder(-100)]
    public sealed class FitSize : MonoBehaviour
    {
        private const float Epsilon = 1e-4f;

        /// <summary>The mode-enum discriminator for which dimension(s) drive <c>localScale</c>. None
        /// MUST be index 0 (default == inert; <see cref="Evaluate"/> early-returns and a freshly-added
        /// FitSize drives nothing).</summary>
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
                float denom = local[drivingAxis] * pls[drivingAxis];
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
                    float denom = local[i] * pls[i];
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

        private void WarnDegenerate()
        {
            if (_loggedWarning) return;
            Debug.LogWarning($"[CodeScenes] FitSize on '{name}' has degenerate bounds/scale on an authored axis; skipping.", this);
            _loggedWarning = true;
        }
    }
}
