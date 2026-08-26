using System.Collections.Generic;
using SceneBuilder.Authoring;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Strips editor-only FitSize/AlignTo/Between components from a real player build, baking
    /// their final transform first so the built object retains the driven size/position with no
    /// FitSize/AlignTo/Between and no missing-script stub.
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
            var aligners = new List<AlignTo>();
            var betweens = new List<Between>();

            foreach (var root in scene.GetRootGameObjects())
            {
                sizers.AddRange(root.GetComponentsInChildren<FitSize>(true));
                aligners.AddRange(root.GetComponentsInChildren<AlignTo>(true));
                betweens.AddRange(root.GetComponentsInChildren<Between>(true));
            }

            foreach (var sizer in sizers)
            {
                sizer.Evaluate();
            }

            foreach (var aligner in aligners)
            {
                aligner.Evaluate();
            }

            // Between places itself relative to already-aligned anchors (execution order -80, after
            // AlignTo's -90), so it must bake after the aligners above.
            foreach (var between in betweens)
            {
                between.Evaluate();
            }

            foreach (var sizer in sizers)
            {
                Object.DestroyImmediate(sizer);
            }

            foreach (var aligner in aligners)
            {
                Object.DestroyImmediate(aligner);
            }

            foreach (var between in betweens)
            {
                Object.DestroyImmediate(between);
            }
        }
    }
}
