using System;
using System.Threading.Tasks;
using UnityEngine;

namespace SceneBuilder.Editor.Licensing
{
    // Outcome of the most recent trial-claim attempt, observed by a banner and by tests.
    public enum TrialClaimOutcome
    {
        None,
        Claimed,
        OfflineNoTrial,
        Unavailable
    }

    // First-run trial claim: no license, no stored token. Requires network — an offline
    // first run surfaces OfflineNoTrial rather than fabricating a trial locally.
    public static class TrialClaim
    {
        public static TrialClaimOutcome LastOutcome { get; private set; }

        public static event Action OutcomeChanged;

        internal static async Task ClaimAsync(ILicenseTransport transport)
        {
            string requestJson = LicenseWire.TrialRequest(MachineIdentity.Raw, MachineIdentity.Label, MachineIdentity.Os);
            LicenseHttpResponse response = await transport.SendAsync(requestJson);

            if (response.Outcome == TransportOutcome.Unreachable)
            {
                SetOutcome(TrialClaimOutcome.OfflineNoTrial);
                return;
            }

            LicenseResponse parsed = LicenseWire.ParseResponse(response.Body);
            if (parsed.ok && !string.IsNullOrEmpty(parsed.token))
            {
                LicenseStore.StoreToken(parsed.token);
                SetOutcome(TrialClaimOutcome.Claimed);
            }
            else
            {
                SetOutcome(TrialClaimOutcome.Unavailable);
            }
        }

        private static void SetOutcome(TrialClaimOutcome outcome)
        {
            LastOutcome = outcome;

            if (outcome == TrialClaimOutcome.OfflineNoTrial)
            {
                Debug.LogWarning(
                    "CodeScenes: no internet on first run, so the 14-day trial could not be started. " +
                    "Connect and reopen to begin your trial.");
            }

            OutcomeChanged?.Invoke();
        }
    }
}
