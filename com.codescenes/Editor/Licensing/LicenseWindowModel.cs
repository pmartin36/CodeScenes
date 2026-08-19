using System;
using System.Threading.Tasks;
using UnityEngine;

namespace SceneBuilder.Editor.Licensing
{
    public enum LicenseWindowView
    {
        NoLicense,
        Activating,
        Licensed,
        SeatManagement
    }

    // Presentation-agnostic controller behind the CodeScenes/License window. All activate/
    // seat-management behavior lives here so it is testable without a rendered UI; the view
    // (LicenseWindow) only binds UXML elements to this model's observable state.
    internal sealed class LicenseWindowModel
    {
        internal const string GumroadProductUrl = "https://paulmartindev.gumroad.com/l/codescenes";
        internal const int SeatCap = 3;

        private const string OfflineMessage = "Could not reach the license server. Check your connection and try again.";

        internal ILicenseTransport Transport = new LicenseTransport();
        internal Action<string> OpenUrl = Application.OpenURL;

        // The key that was entered on activate, or loaded from LicenseStore when the window is
        // opened already-Licensed. Retained even when activation fails (seat_limit) so Remove and
        // a retry activate do not require the user to re-type the key.
        private string _activeKey;

        public LicenseWindowView View { get; private set; }
        public string Message { get; private set; }
        public Seat[] Seats { get; private set; } = Array.Empty<Seat>();
        public int SeatsUsed { get; private set; }
        public int SeatsTotal { get; private set; }
        public int TrialDaysRemaining { get; private set; }

        public event Action ModelChanged;

        public bool IsCurrentMachine(Seat s) => s != null && s.hash == MachineIdentity.Hash;

        public void Initialize()
        {
            switch (LicenseStore.Current)
            {
                case LicenseState.Licensed:
                    _activeKey = LicenseStore.CurrentLicenseKey;
                    View = LicenseWindowView.Licensed;
                    TrialDaysRemaining = 0;
                    _ = LoadSeatsAsync();
                    break;

                case LicenseState.Trial:
                    View = LicenseWindowView.NoLicense;
                    TrialDaysRemaining = LicenseExpiry.TryGetRemaining(out TimeSpan remaining)
                        ? LicenseExpiry.DaysRemaining(remaining)
                        : 0;
                    break;

                default:
                    View = LicenseWindowView.NoLicense;
                    TrialDaysRemaining = 0;
                    break;
            }

            Raise();
        }

        public async Task ActivateAsync(string key)
        {
            _activeKey = key;
            View = LicenseWindowView.Activating;
            Raise();

            LicenseHttpResponse response = await Transport.SendAsync(
                LicenseWire.ActivateRequest(key, MachineIdentity.Raw, MachineIdentity.Label, MachineIdentity.Os));

            if (response.Outcome == TransportOutcome.Unreachable)
            {
                Message = OfflineMessage;
                View = LicenseWindowView.NoLicense;
                Raise();
                return;
            }

            LicenseResponse parsed = LicenseWire.ParseResponse(response.Body);

            if (parsed.ok && !string.IsNullOrEmpty(parsed.token))
            {
                LicenseStore.StoreLicenseKey(key);
                LicenseStore.StoreToken(parsed.token);
                SeatsUsed = parsed.seatsUsed;
                SeatsTotal = parsed.seatsTotal > 0 ? parsed.seatsTotal : SeatCap;
                View = LicenseWindowView.Licensed;
                Message = $"Activated. {SeatsUsed} of {SeatsTotal} seats in use.";
                Raise();
                _ = LoadSeatsAsync();
                return;
            }

            if (!parsed.ok && parsed.reason == LicenseReason.SeatLimit)
            {
                Seats = parsed.seats ?? Array.Empty<Seat>();
                SeatsUsed = Seats.Length;
                SeatsTotal = parsed.seatsTotal > 0 ? parsed.seatsTotal : SeatCap;
                View = LicenseWindowView.SeatManagement;
                Message = $"This key is already on {Seats.Length} machines. Remove one to activate.";
                Raise();
                return;
            }

            View = LicenseWindowView.NoLicense;
            Message = ReasonMessage(parsed.reason);
            Raise();
        }

        public async Task RemoveSeatAsync(string hash)
        {
            LicenseHttpResponse response = await Transport.SendAsync(LicenseWire.ReleaseRequest(_activeKey, hash));

            if (response.Outcome == TransportOutcome.Unreachable)
            {
                Message = OfflineMessage;
                Raise();
                return;
            }

            LicenseResponse parsed = LicenseWire.ParseResponse(response.Body);

            if (parsed.ok)
            {
                Seats = parsed.machines ?? Array.Empty<Seat>();
                SeatsUsed = Seats.Length;
                Message = "Seat removed.";
            }
            else
            {
                Message = ReasonMessage(parsed.reason);
            }

            Raise();
        }

        public async Task LoadSeatsAsync()
        {
            string key = LicenseStore.CurrentLicenseKey;
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            LicenseHttpResponse response = await Transport.SendAsync(LicenseWire.SeatsRequest(key));
            if (response.Outcome != TransportOutcome.Completed)
            {
                return;
            }

            LicenseResponse parsed = LicenseWire.ParseResponse(response.Body);
            if (!parsed.ok)
            {
                return;
            }

            Seats = parsed.machines ?? Array.Empty<Seat>();
            SeatsUsed = Seats.Length;
            SeatsTotal = SeatsTotal > 0 ? SeatsTotal : SeatCap;
            Raise();
        }

        public void Buy() => OpenUrl(GumroadProductUrl);

        private void Raise() => ModelChanged?.Invoke();

        private static string ReasonMessage(string reason)
        {
            switch (reason)
            {
                case LicenseReason.InvalidKey:
                    return "That key was not recognized.";
                case LicenseReason.Refunded:
                    return "This purchase was refunded.";
                case LicenseReason.RateLimited:
                    return "Too many attempts. Try again shortly.";
                case LicenseReason.TrialExpired:
                    return "Your trial has ended.";
                default:
                    return "Activation failed.";
            }
        }
    }
}
