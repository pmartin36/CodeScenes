using System;
using UnityEngine;

namespace SceneBuilder.Editor.Licensing
{
    // The six rejection reasons the backend returns verbatim (spec 34; handlers.ts).
    public static class LicenseReason
    {
        public const string InvalidKey = "invalid_key";
        public const string Refunded = "refunded";
        public const string SeatLimit = "seat_limit";
        public const string RateLimited = "rate_limited";
        public const string TrialExpired = "trial_expired";
        public const string InvalidRequest = "invalid_request";
    }

    [Serializable]
    public class Seat
    {
        public string hash;
        public string label;
        public string os;
        public long activatedAt;
        public long lastSeenAt;
    }

    // Union response DTO: which fields are populated depends on `ok` and, on success,
    // which action was called (activate/trial carry token; seats/release carry
    // machines; a seat_limit reject carries seats).
    [Serializable]
    public class LicenseResponse
    {
        public bool ok;
        public string token;
        public int seatsUsed;
        public int seatsTotal;
        public string reason;
        public Seat[] seats;
        public Seat[] machines;
    }

    // Sole owner of licensing wire (de)serialization. Every request is built here and
    // every response is parsed here so field names are defined exactly once against
    // the deployed backend contract.
    public static class LicenseWire
    {
        [Serializable]
        private class ActivateRequestDto
        {
            public string action = "activate";
            public string licenseKey;
            public string machineId;
            public string label;
            public string os;
        }

        [Serializable]
        private class TrialRequestDto
        {
            public string action = "trial";
            public string machineId;
            public string label;
            public string os;
        }

        [Serializable]
        private class SeatsRequestDto
        {
            public string action = "seats";
            public string licenseKey;
        }

        [Serializable]
        private class ReleaseRequestDto
        {
            public string action = "release";
            public string licenseKey;
            public string hash;
        }

        public static string ActivateRequest(string licenseKey, string machineId, string label, string os)
        {
            return JsonUtility.ToJson(new ActivateRequestDto
            {
                licenseKey = licenseKey,
                machineId = machineId,
                label = label,
                os = os
            });
        }

        public static string TrialRequest(string machineId, string label, string os)
        {
            return JsonUtility.ToJson(new TrialRequestDto
            {
                machineId = machineId,
                label = label,
                os = os
            });
        }

        public static string SeatsRequest(string licenseKey)
        {
            return JsonUtility.ToJson(new SeatsRequestDto { licenseKey = licenseKey });
        }

        public static string ReleaseRequest(string licenseKey, string hash)
        {
            return JsonUtility.ToJson(new ReleaseRequestDto { licenseKey = licenseKey, hash = hash });
        }

        public static LicenseResponse ParseResponse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return new LicenseResponse { ok = false };
            }

            try
            {
                LicenseResponse response = JsonUtility.FromJson<LicenseResponse>(json);
                return response ?? new LicenseResponse { ok = false };
            }
            catch (Exception)
            {
                return new LicenseResponse { ok = false };
            }
        }
    }
}
