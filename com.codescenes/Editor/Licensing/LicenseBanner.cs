using System;
using UnityEditor;
using UnityEngine;

namespace SceneBuilder.Editor.Licensing
{
    public enum LicenseBannerKind
    {
        None,
        NearExpiry,
        Activate,
        OfflineNoTrial
    }

    public readonly struct LicenseBannerState
    {
        public readonly LicenseBannerKind Kind;
        public readonly int DaysRemaining;
        public readonly string Message;

        public LicenseBannerState(LicenseBannerKind kind, int daysRemaining, string message)
        {
            Kind = kind;
            DaysRemaining = daysRemaining;
            Message = message;
        }
    }

    // Non-modal editor banner state, derived live from LicenseStore/TokenVerifier/LicenseClock/
    // TrialClaim: no state of its own, no memo, so every Current read reflects the latest
    // license/trial state. Mirrors ConflictSurfacing's static-ctor duringSceneGui subscription +
    // best-effort draw, with the computed state as the test-observable seam.
    [InitializeOnLoad]
    public static class LicenseBanner
    {
        internal const string ActivationMenuPath = "CodeScenes/License";

        internal static Action OpenActivation = () => EditorApplication.ExecuteMenuItem(ActivationMenuPath);

        static LicenseBanner()
        {
            SceneView.duringSceneGui += OnSceneGui;
            LicenseStore.Changed += Repaint;
            TrialClaim.OutcomeChanged += Repaint;
        }

        public static LicenseBannerState Current => Compute();

        internal static void RaiseActivate() => OpenActivation?.Invoke();

        private static void Repaint() => SceneView.RepaintAll();

        private static LicenseBannerState Compute()
        {
            LicenseState state = LicenseStore.Current;

            if (state == LicenseState.Unlicensed)
            {
                if (TrialClaim.LastOutcome == TrialClaimOutcome.OfflineNoTrial)
                {
                    return new LicenseBannerState(LicenseBannerKind.OfflineNoTrial, 0,
                        "No internet on first run, so the trial could not start. Connect and reopen to begin.");
                }

                return new LicenseBannerState(LicenseBannerKind.Activate, 0,
                    "CodeScenes is not activated. Activate to resume sync.");
            }

            if (!LicenseExpiry.TryGetRemaining(out TimeSpan remaining))
            {
                return new LicenseBannerState(LicenseBannerKind.None, 0, null);
            }

            if (remaining <= LicenseClock.NearExpiryWarningWindow)
            {
                int daysRemaining = LicenseExpiry.DaysRemaining(remaining);
                return new LicenseBannerState(LicenseBannerKind.NearExpiry, daysRemaining,
                    $"CodeScenes license expires in {daysRemaining} day(s). Reconnect to renew.");
            }

            return new LicenseBannerState(LicenseBannerKind.None, 0, null);
        }

        // Best-effort draw only; Current (the test-observable seam) is computed regardless of
        // whether this ever paints anything (e.g. headless batchmode has no SceneView).
        private static void OnSceneGui(SceneView view)
        {
            var state = Current;
            if (state.Kind == LicenseBannerKind.None)
            {
                return;
            }

            Handles.BeginGUI();
            GUILayout.Label(state.Message);
            if (state.Kind == LicenseBannerKind.Activate || state.Kind == LicenseBannerKind.OfflineNoTrial)
            {
                if (GUILayout.Button("Activate"))
                {
                    RaiseActivate();
                }
            }

            Handles.EndGUI();
        }
    }
}
