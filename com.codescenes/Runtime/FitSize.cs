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
        /// <summary>Sentinel meaning "not authored" — never a legitimate aspect-locked dimension (must be &gt; 0).</summary>
        private const float Unset = float.NaN;

        private const float Epsilon = 1e-4f;

        public float width = Unset;
        public float height = Unset;
        public float depth = Unset;
        public Vector3 size;

        /// <summary>The last <c>localScale</c> this component wrote — used to discriminate our own
        /// writes from a manual scale edit by the user. Sentinel (NaN.x) means "never written".</summary>
        private Vector3 _lastWritten = new Vector3(float.NaN, float.NaN, float.NaN);

        private bool _loggedError;
        private bool _loggedWarning;

        private static bool IsSet(float v) => !float.IsNaN(v);

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
                // localScale channel is never written back to source; only width/height/depth/size are.
                Vector3 lossy = transform.lossyScale;
                Vector3 world = new Vector3(local.x * lossy.x, local.y * lossy.y, local.z * lossy.z);

                if (drivingAxis >= 0)
                {
                    SetAxisField(drivingAxis, world[drivingAxis]);
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

                float s = GetAxisField(drivingAxis) / denom;
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
        /// or -1 when none is set (explicit <see cref="size"/> mode). Priority width &gt; height &gt;
        /// depth if more than one is (mis-)authored.</summary>
        private int DrivingAxis()
        {
            if (IsSet(width)) return 0;
            if (IsSet(height)) return 1;
            if (IsSet(depth)) return 2;
            return -1;
        }

        private float GetAxisField(int axis)
        {
            switch (axis)
            {
                case 0: return width;
                case 1: return height;
                default: return depth;
            }
        }

        private void SetAxisField(int axis, float value)
        {
            switch (axis)
            {
                case 0: width = value; break;
                case 1: height = value; break;
                default: depth = value; break;
            }
        }

        private void WarnDegenerate()
        {
            if (_loggedWarning) return;
            Debug.LogWarning($"[CodeScenes] FitSize on '{name}' has degenerate bounds/scale on an authored axis; skipping.", this);
            _loggedWarning = true;
        }
    }
}
