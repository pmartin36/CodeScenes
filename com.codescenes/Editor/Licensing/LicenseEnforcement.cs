using UnityEditor;

namespace SceneBuilder.Editor.Licensing
{
    // Registers the real LicenseState-backed verdict onto LicenseGate and re-applies it on every
    // LicenseStore.Changed notification (a token stored/cleared by activation or the daily
    // entitlement check). Runs only when this assembly is compiled in (absent in an Asset-Store
    // build, where LicenseGate keeps its allowed-by-default provider).
    [InitializeOnLoad]
    public static class LicenseEnforcement
    {
        static LicenseEnforcement()
        {
            Register();
        }

        // Re-runs the static ctor's registration. Exposed so a test can restore the real provider
        // after a test overrides LicenseGate's provider directly, without leaking an allowed gate
        // into a later fail-closed assertion.
        public static void Register()
        {
            LicenseStore.Changed -= Apply;
            LicenseStore.Changed += Apply;
            LicenseGate.SetProvider(() => LicenseStore.Current != LicenseState.Unlicensed);
            Apply();
        }

        internal static void Apply() => SceneBuilderAutoSync.ApplyToggleState();
    }
}
