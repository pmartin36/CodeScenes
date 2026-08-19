using System;
using System.Globalization;
using UnityEditor;

namespace SceneBuilder.Editor.Licensing
{
    // Single accessor for the resolved LicenseState, derived ONLY from the one persisted token
    // (never an independent state-set path). The token and clock-tamper baseline are persisted
    // per machine + per user in EditorPrefs (never a file under Assets/ or the project
    // directory); the resolved verdict is cached in SessionState so a domain reload reads the
    // cache instead of re-verifying.
    public static class LicenseStore
    {
        public const string SessionResolvedKeyConst = "CodeScenes.License.Resolved";
        public const string SessionStateKeyConst = "CodeScenes.License.State";

        public static string SessionResolvedKey => SessionResolvedKeyConst;
        public static string SessionStateKey => SessionStateKeyConst;

        private static string Scope => MachineIdentity.Hash + "::" + Environment.UserName;

        public static string TokenKey => "CodeScenes.License.Token::" + Scope;
        public static string BaselineKey => "CodeScenes.License.Baseline::" + Scope;
        public static string LicenseKeyKey => "CodeScenes.License.Key::" + Scope;
        public static string LastAttemptKey => "CodeScenes.License.LastAttempt::" + Scope;

        // Test seam: invoked once per real resolution (never on a SessionState cache hit).
        public static Func<TokenVerifier> VerifierProvider = () => TokenVerifier.Production;

        public static event Action Changed;

        private static LicenseState? _memo;

        public static LicenseState Current
        {
            get
            {
                // Both session keys are stored (and read back) as strings so that
                // SessionState.EraseString actually clears them on Invalidate(): SessionState
                // keeps separate stores per value type, and Erase only exists for the string one.
                // The memo is trusted only while SessionState still reports itself resolved, so an
                // external SessionState wipe (e.g. a test isolating itself) invalidates the memo
                // along with the cache it mirrors, not just a call through Invalidate().
                bool sessionResolved = SessionState.GetString(SessionResolvedKey, null) == "1";

                if (_memo.HasValue && sessionResolved)
                {
                    return _memo.Value;
                }

                if (sessionResolved)
                {
                    var cached = (LicenseState)int.Parse(
                        SessionState.GetString(SessionStateKey, ((int)LicenseState.Unlicensed).ToString(CultureInfo.InvariantCulture)),
                        CultureInfo.InvariantCulture);
                    _memo = cached;
                    return cached;
                }

                var resolved = Resolve();
                SessionState.SetString(SessionResolvedKey, "1");
                SessionState.SetString(SessionStateKey, ((int)resolved).ToString(CultureInfo.InvariantCulture));
                _memo = resolved;
                return resolved;
            }
        }

        public static string CurrentToken => EditorPrefs.GetString(TokenKey, null);

        public static DateTime LastObservedUtc
        {
            get
            {
                string raw = EditorPrefs.GetString(BaselineKey, null);
                if (string.IsNullOrEmpty(raw) || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
                {
                    return DateTime.MinValue;
                }

                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        public static void StoreToken(string token)
        {
            var old = Current;
            Invalidate();
            EditorPrefs.SetString(TokenKey, token);
            var updated = Current;
            if (updated != old)
            {
                Changed?.Invoke();
            }
        }

        public static void Clear()
        {
            var old = Current;
            Invalidate();
            EditorPrefs.DeleteKey(TokenKey);
            var updated = Current;
            if (updated != old)
            {
                Changed?.Invoke();
            }
        }

        public static void RecordObservedUtc(DateTime utc)
        {
            EditorPrefs.SetString(BaselineKey, utc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        }

        // Raw license key persistence (never present for a trial): the daily refresh
        // and refund-detection re-`activate` calls need it because the persisted token
        // carries only the hashed `lic`, not the key itself. EditorPrefs.GetString marshals a
        // null default to an empty string rather than echoing it back, so an unset key is
        // normalized back to null here rather than at each caller.
        public static string CurrentLicenseKey => EditorPrefs.HasKey(LicenseKeyKey) ? EditorPrefs.GetString(LicenseKeyKey) : null;

        public static void StoreLicenseKey(string key) => EditorPrefs.SetString(LicenseKeyKey, key);

        public static DateTime LastAttemptUtc
        {
            get
            {
                string raw = EditorPrefs.GetString(LastAttemptKey, null);
                if (string.IsNullOrEmpty(raw) || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
                {
                    return DateTime.MinValue;
                }

                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        public static void RecordAttemptUtc(DateTime utc)
        {
            EditorPrefs.SetString(LastAttemptKey, utc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        }

        // Models a domain reload for tests: clears ONLY the in-memory memo. SessionState and
        // EditorPrefs are left intact, matching a real reload's statics-gone-but-SessionState-
        // survives contract.
        public static void SimulateDomainReload()
        {
            _memo = null;
        }

        private static LicenseState Resolve()
        {
            string token = EditorPrefs.GetString(TokenKey, null);
            if (string.IsNullOrEmpty(token))
            {
                return LicenseState.Unlicensed;
            }

            var result = VerifierProvider().Verify(token, MachineIdentity.Hash);
            if (result.Status != TokenVerifyStatus.Valid)
            {
                return LicenseState.Unlicensed;
            }

            return result.Token.kind == "trial" ? LicenseState.Trial : LicenseState.Licensed;
        }

        private static void Invalidate()
        {
            _memo = null;
            SessionState.EraseString(SessionResolvedKey);
            SessionState.EraseString(SessionStateKey);
        }
    }
}
