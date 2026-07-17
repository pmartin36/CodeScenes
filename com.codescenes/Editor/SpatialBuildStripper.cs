using System.Collections.Generic;
using SceneBuilder.Authoring;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Strips editor-only Sizer/Snapper components from a real player build, baking their final
    /// transform first so the built object retains the driven size/position with no Sizer/Snapper
    /// and no missing-script stub.
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
            var sizers = new List<Sizer>();
            var snappers = new List<Snapper>();

            foreach (var root in scene.GetRootGameObjects())
            {
                sizers.AddRange(root.GetComponentsInChildren<Sizer>(true));
                snappers.AddRange(root.GetComponentsInChildren<Snapper>(true));
            }

            foreach (var sizer in sizers)
            {
                sizer.Evaluate();
            }

            foreach (var snapper in snappers)
            {
                snapper.Evaluate();
            }

            foreach (var sizer in sizers)
            {
                Object.DestroyImmediate(sizer);
            }

            foreach (var snapper in snappers)
            {
                Object.DestroyImmediate(snapper);
            }
        }
    }
}
