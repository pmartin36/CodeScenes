using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SceneBuilder.Editor.Licensing
{
    // Async off-reload entitlement check: daily throttle, fail-open on an unreachable
    // server, immediate reject on invalid_key/refunded, and a clock-rollback that forces
    // a check regardless of the daily cap.
    [InitializeOnLoad]
    public static class EntitlementCheck
    {
        static EntitlementCheck()
        {
            EditorApplication.delayCall += ScheduleStartupCheck;
        }

        // Test seam: a mock swaps this before driving RunCheckAsync directly.
        internal static ILicenseTransport Transport = new LicenseTransport();

        internal static void ScheduleStartupCheck()
        {
            if (string.IsNullOrEmpty(LicenseStore.CurrentToken) && Application.isBatchMode)
            {
                return;
            }

            RunCheckSafe();
        }

        private static async void RunCheckSafe()
        {
            try
            {
                await RunCheckAsync();
            }
            catch
            {
                // Silent: the console stays quiet on the happy path.
            }
        }

        // The testable core. force bypasses the daily throttle for test convenience only;
        // the rollback deliverable is driven by real rollback state, not by passing force.
        internal static async Task RunCheckAsync(bool force = false)
        {
            DateTime now = LicenseClock.UtcNow();
            bool rolledBack = LicenseClock.IsClockRolledBack(LicenseStore.LastObservedUtc);

            if (!force && !rolledBack && LicenseStore.LastAttemptUtc.Date == now.Date)
            {
                return;
            }

            LicenseStore.RecordAttemptUtc(now);

            if (now > LicenseStore.LastObservedUtc)
            {
                LicenseStore.RecordObservedUtc(now);
            }

            string requestJson;
            switch (LicenseStore.Current)
            {
                case LicenseState.Licensed:
                    string key = LicenseStore.CurrentLicenseKey;
                    if (string.IsNullOrEmpty(key))
                    {
                        return;
                    }

                    requestJson = LicenseWire.ActivateRequest(key, MachineIdentity.Raw, MachineIdentity.Label, MachineIdentity.Os);
                    break;

                case LicenseState.Trial:
                    requestJson = LicenseWire.TrialRequest(MachineIdentity.Raw, MachineIdentity.Label, MachineIdentity.Os);
                    break;

                default:
                    if (string.IsNullOrEmpty(LicenseStore.CurrentToken))
                    {
                        await TrialClaim.ClaimAsync(Transport);
                    }

                    return;
            }

            LicenseHttpResponse response = await Transport.SendAsync(requestJson);
            if (response.Outcome == TransportOutcome.Unreachable)
            {
                return;
            }

            LicenseResponse parsed = LicenseWire.ParseResponse(response.Body);
            if (parsed.ok && !string.IsNullOrEmpty(parsed.token))
            {
                LicenseStore.StoreToken(parsed.token);
            }
            else if (!parsed.ok && (parsed.reason == LicenseReason.InvalidKey || parsed.reason == LicenseReason.Refunded))
            {
                LicenseStore.Clear();
            }
        }
    }
}
