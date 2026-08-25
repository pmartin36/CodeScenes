#nullable enable
using System;
using UnityEditor;

namespace SceneBuilder.Editor
{
    // Play-mode re-arm reliability: the deferred re-arm + self-healing watchdog that keep the pump
    // live after every play-mode and probe round trip, split out of SceneBuilderAutoSync.cs per the
    // project's file-size budget (exact precedent: SceneBuilderAutoSync.Prefab.cs).
    public static partial class SceneBuilderAutoSync
    {
        /// <summary>Test seam: the "are we still (about to be) playing" read <see cref="ApplyToggleState"/> gates on.</summary>
        internal static Func<bool> IsPlayingProbe = DefaultIsPlaying;

        /// <summary>Test seam: how the re-arm after EnteredEditMode is scheduled. Defaults to <see cref="EditorApplication.delayCall"/>.</summary>
        internal static Action<Action> ScheduleReArm = DefaultScheduleReArm;

        private static bool DefaultIsPlaying() => EditorApplication.isPlayingOrWillChangePlaymode;

        private static void DefaultScheduleReArm(Action action) => EditorApplication.delayCall += () => action();

        /// <summary>
        /// Arm-only self-heal: re-arms a dead pump whenever the master toggle is on, the license
        /// allows it, and play mode is off but the pump is not subscribed — recovering a probe round
        /// trip whose deferred re-arm callback never runs. Never disarms.
        /// </summary>
        internal static void PumpWatchdog()
        {
            if (!IsArmed && SceneBuilderAutoToggle.Enabled && !IsPlayingProbe() && LicenseGate.Allowed)
            {
                Arm();
            }
        }
    }
}
