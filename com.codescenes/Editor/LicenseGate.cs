using System;

namespace SceneBuilder.Editor
{
    // The ONE decision owner every sync entry point (auto-sync arming, the pump backstop, the
    // four Build/Sync menu commands) consults. Lives in the main assembly so an Asset-Store build
    // with the licensing assembly excluded never references licensing; with no provider
    // registered it defaults to allowed. The licensing assembly registers the real
    // LicenseState-backed verdict and re-applies it on every LicenseStore.Changed notification.
    public static class LicenseGate
    {
        public static readonly Func<bool> DefaultAllowed = () => true;

        private static Func<bool> _provider = DefaultAllowed;

        public static void SetProvider(Func<bool> provider) => _provider = provider ?? DefaultAllowed;

        public static void ResetToDefault() => _provider = DefaultAllowed;

        public static bool Allowed => _provider();

        public static void RunGuarded(Action command)
        {
            if (!Allowed)
            {
                return;
            }

            command();
        }
    }
}
