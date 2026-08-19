using System;

namespace SceneBuilder.Editor.Licensing
{
    // Single owner of "time remaining until the current stored token's exp", shared by
    // LicenseBanner's near-expiry computation and the activation window's trial-days display.
    public static class LicenseExpiry
    {
        public static bool TryGetRemaining(out TimeSpan remaining)
        {
            remaining = default;

            string token = LicenseStore.CurrentToken;
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            TokenVerifyResult result = LicenseStore.VerifierProvider().Verify(token, MachineIdentity.Hash);
            if (result.Status != TokenVerifyStatus.Valid)
            {
                return false;
            }

            DateTime expUtc = DateTimeOffset.FromUnixTimeSeconds(result.Token.exp).UtcDateTime;
            remaining = expUtc - LicenseClock.UtcNow();
            return true;
        }

        public static int DaysRemaining(TimeSpan remaining) => (int)Math.Ceiling(remaining.TotalDays);
    }
}
