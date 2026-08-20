using System.Collections.Generic;
using SceneBuilder.Authoring;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Strips editor-only FitSize/SurfaceSnap/Between components from a real player build, baking
    /// their final transform first so the built object retains the driven size/position with no
    /// FitSize/SurfaceSnap/Between and no missing-script stub.
    /// </summary>
    public sealed class SpatialBuildStripper : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null)
            {
                return;
            }

            StripScene(scene);
        }

        internal static void StripScene(Scene scene)
        {
            var sizers = new List<FitSize>();
            var snappers = new List<SurfaceSnap>();
            var betweens = new List<Between>();

            foreach (var root in scene.GetRootGameObjects())
            {
                sizers.AddRange(root.GetComponentsInChildren<FitSize>(true));
                snappers.AddRange(root.GetComponentsInChildren<SurfaceSnap>(true));
                betweens.AddRange(root.GetComponentsInChildren<Between>(true));
            }

            foreach (var sizer in sizers)
            {
                sizer.Evaluate();
            }

            foreach (var snapper in snappers)
            {
                snapper.Evaluate();
            }

            // Between places itself relative to already-snapped anchors (execution order -80, after
            // SurfaceSnap's -90), so it must bake after the snappers above.
            foreach (var between in betweens)
            {
                between.Evaluate();
            }

            foreach (var sizer in sizers)
            {
                Object.DestroyImmediate(sizer);
            }

            foreach (var snapper in snappers)
            {
                Object.DestroyImmediate(snapper);
            }

            foreach (var between in betweens)
            {
                Object.DestroyImmediate(between);
            }
        }
    }
}
