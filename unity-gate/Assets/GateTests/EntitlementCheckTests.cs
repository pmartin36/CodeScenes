using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

// Pins EntitlementCheck's off-reload entitlement flow: fail-open while offline (a token
// rides its own exp), immediate reject on invalid_key/refunded, a successful refresh
// restoring the full offline budget, the once-per-calendar-day throttle, a clock rollback
// forcing a check despite that throttle, the silent no-token/no-op startup path, and a
// benign reject leaving state untouched.
public class EntitlementCheckTests
{
    private RSA _rsa;

    private bool _hadToken;
    private string _originalToken;
    private bool _hadBaseline;
    private string _originalBaseline;
    private bool _hadLicenseKey;
    private string _originalLicenseKey;
    private bool _hadLastAttempt;
    private string _originalLastAttempt;

    [SetUp]
    public void SetUp()
    {
        _rsa = RSA.Create(2048);
        var verifier = new TokenVerifier(_rsa.ExportParameters(false));
        LicenseStore.VerifierProvider = () => verifier;

        _hadToken = EditorPrefs.HasKey(LicenseStore.TokenKey);
        _originalToken = EditorPrefs.GetString(LicenseStore.TokenKey, null);
        _hadBaseline = EditorPrefs.HasKey(LicenseStore.BaselineKey);
        _originalBaseline = EditorPrefs.GetString(LicenseStore.BaselineKey, null);
        _hadLicenseKey = EditorPrefs.HasKey(LicenseStore.LicenseKeyKey);
        _originalLicenseKey = EditorPrefs.GetString(LicenseStore.LicenseKeyKey, null);
        _hadLastAttempt = EditorPrefs.HasKey(LicenseStore.LastAttemptKey);
        _originalLastAttempt = EditorPrefs.GetString(LicenseStore.LastAttemptKey, null);

        EditorPrefs.DeleteKey(LicenseStore.TokenKey);
        EditorPrefs.DeleteKey(LicenseStore.BaselineKey);
        EditorPrefs.DeleteKey(LicenseStore.LicenseKeyKey);
        EditorPrefs.DeleteKey(LicenseStore.LastAttemptKey);
        SessionState.EraseString(LicenseStore.SessionResolvedKey);
        SessionState.EraseString(LicenseStore.SessionStateKey);

        LicenseClock.UtcNow = () => DateTime.UtcNow;
        EntitlementCheck.Transport = new LicenseTransport();
    }

    [TearDown]
    public void TearDown()
    {
        Restore(LicenseStore.TokenKey, _hadToken, _originalToken);
        Restore(LicenseStore.BaselineKey, _hadBaseline, _originalBaseline);
        Restore(LicenseStore.LicenseKeyKey, _hadLicenseKey, _originalLicenseKey);
        Restore(LicenseStore.LastAttemptKey, _hadLastAttempt, _originalLastAttempt);

        SessionState.EraseString(LicenseStore.SessionResolvedKey);
        SessionState.EraseString(LicenseStore.SessionStateKey);
        LicenseStore.VerifierProvider = () => TokenVerifier.Production;
        LicenseClock.UtcNow = () => DateTime.UtcNow;
        EntitlementCheck.Transport = new LicenseTransport();
        _rsa?.Dispose();
    }

    private static void Restore(string key, bool had, string original)
    {
        if (had)
        {
            EditorPrefs.SetString(key, original);
        }
        else
        {
            EditorPrefs.DeleteKey(key);
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private string SignToken(string sub, long iat, long exp, string kind = "license")
    {
        var payload = new LicenseToken { v = 1, kind = kind, sub = sub, lic = "LIC-1", iat = iat, exp = exp };
        string json = JsonUtility.ToJson(payload);
        string head = Base64UrlEncode(Encoding.UTF8.GetBytes(json));
        byte[] sig = _rsa.SignData(Encoding.ASCII.GetBytes(head), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return head + "." + Base64UrlEncode(sig);
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private sealed class CountingTransport : ILicenseTransport
    {
        public int CallCount;
        public Func<string, LicenseHttpResponse> Respond = _ => LicenseHttpResponse.Unreachable();

        public Task<LicenseHttpResponse> SendAsync(string requestJson, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(Respond(requestJson));
        }
    }

    [Test]
    public void UnreachableServer_ValidCachedToken_StaysLicensedNoStateChange()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 86400));
        LicenseStore.StoreLicenseKey("KEY-1");
        EntitlementCheck.Transport = new CountingTransport { Respond = _ => LicenseHttpResponse.Unreachable() };

        int changed = 0;
        LicenseStore.Changed += Handler;
        try
        {
            EntitlementCheck.RunCheckAsync(force: true).GetAwaiter().GetResult();
        }
        finally
        {
            LicenseStore.Changed -= Handler;
        }

        Assert.AreEqual(LicenseState.Licensed, LicenseStore.Current,
            "An unreachable server must fail open: a cached token whose exp is future stays valid on its own signature.");
        Assert.AreEqual(0, changed, "An unreachable check must not change the resolved state.");
        return;

        void Handler() => changed++;
    }

    [Test]
    public void NoValidToken_RunCheckAsync_DoesNotCallTransport()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 7200, UnixNow() - 3600));
        var transport = new CountingTransport();
        EntitlementCheck.Transport = transport;

        EntitlementCheck.RunCheckAsync(force: true).GetAwaiter().GetResult();

        Assert.AreEqual(0, transport.CallCount,
            "With no valid token to refresh, RunCheckAsync must no-op without contacting the transport.");
    }

    [Test]
    public void ValidLicenseToken_RefundedResponse_ResolvesUnlicensedImmediately()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 864000));
        LicenseStore.StoreLicenseKey("KEY-1");
        EntitlementCheck.Transport = new CountingTransport
        {
            Respond = _ => LicenseHttpResponse.Completed(200, "{\"ok\":false,\"reason\":\"refunded\"}")
        };

        EntitlementCheck.RunCheckAsync(force: true).GetAwaiter().GetResult();

        Assert.AreEqual(LicenseState.Unlicensed, LicenseStore.Current,
            "A reachable server rejecting with refunded must move to Unlicensed immediately, even with exp far in the future.");
    }

    [Test]
    public void ValidLicenseToken_InvalidKeyResponse_ResolvesUnlicensedImmediately()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 864000));
        LicenseStore.StoreLicenseKey("KEY-1");
        EntitlementCheck.Transport = new CountingTransport
        {
            Respond = _ => LicenseHttpResponse.Completed(200, "{\"ok\":false,\"reason\":\"invalid_key\"}")
        };

        EntitlementCheck.RunCheckAsync(force: true).GetAwaiter().GetResult();

        Assert.AreEqual(LicenseState.Unlicensed, LicenseStore.Current,
            "A reachable server rejecting with invalid_key must move to Unlicensed immediately.");
    }

    [Test]
    public void Day13LicenseToken_SuccessfulRefresh_RestoresFullOfflineBudget()
    {
        long now = UnixNow();
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, now - 86400 * 13, now + 86400));
        LicenseStore.StoreLicenseKey("KEY-1");
        string refreshed = SignToken(MachineIdentity.Hash, now, now + 86400 * 14);
        EntitlementCheck.Transport = new CountingTransport
        {
            Respond = _ => LicenseHttpResponse.Completed(200, "{\"ok\":true,\"token\":\"" + refreshed + "\"}")
        };

        EntitlementCheck.RunCheckAsync(force: true).GetAwaiter().GetResult();

        var result = LicenseStore.VerifierProvider().Verify(LicenseStore.CurrentToken, MachineIdentity.Hash);
        Assert.AreEqual(TokenVerifyStatus.Valid, result.Status);
        Assert.That(result.Token.exp, Is.GreaterThan(now + 86400 * 13),
            "A successful refresh must reissue a token expiring a full 14 days out, not merely extend the old one.");
    }

    [Test]
    public void ClockRolledBack_ForcesCheckDespiteDailyThrottle()
    {
        DateTime now = LicenseClock.UtcNow();
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 864000));
        LicenseStore.StoreLicenseKey("KEY-1");
        LicenseStore.RecordObservedUtc(now.AddDays(30));
        LicenseStore.RecordAttemptUtc(now);
        var transport = new CountingTransport();
        EntitlementCheck.Transport = transport;

        EntitlementCheck.RunCheckAsync(force: false).GetAwaiter().GetResult();

        Assert.AreEqual(1, transport.CallCount,
            "A baseline ahead of the current clock (rollback) must force a check even though today's attempt was already recorded.");
    }

    [Test]
    public void OffReloadStartup_ValidTokenNetworkUnreachable_RecordsAttemptAndLogsNothing()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 864000));
        LicenseStore.StoreLicenseKey("KEY-1");
        EntitlementCheck.Transport = new CountingTransport { Respond = _ => LicenseHttpResponse.Unreachable() };

        EntitlementCheck.ScheduleStartupCheck();

        Assert.AreEqual(LicenseState.Licensed, LicenseStore.Current,
            "Scheduling the startup check with the network unreachable must leave a validly cached token Licensed.");
        Assert.That((DateTime.UtcNow - LicenseStore.LastAttemptUtc).TotalMinutes, Is.LessThan(1),
            "The startup check must record the attempt even when the server could not be reached.");
        LogAssert.NoUnexpectedReceived();
    }

    [Test]
    public void Throttle_SecondCallSameDay_DoesNotCallTransport_NextDayCallsAgain()
    {
        DateTime fixedNow = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        LicenseClock.UtcNow = () => fixedNow;
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 864000));
        LicenseStore.StoreLicenseKey("KEY-1");
        var transport = new CountingTransport();
        EntitlementCheck.Transport = transport;

        EntitlementCheck.RunCheckAsync(force: false).GetAwaiter().GetResult();
        Assert.AreEqual(1, transport.CallCount, "The first check of the day must contact the transport.");

        EntitlementCheck.RunCheckAsync(force: false).GetAwaiter().GetResult();
        Assert.AreEqual(1, transport.CallCount, "A second check the same calendar day must be throttled.");

        LicenseClock.UtcNow = () => fixedNow.AddDays(1);
        EntitlementCheck.RunCheckAsync(force: false).GetAwaiter().GetResult();
        Assert.AreEqual(2, transport.CallCount, "A check on the next calendar day must not be throttled.");
    }

    [Test]
    public void BenignRejectReasons_LeaveStateUnchangedAndDoNotThrow()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 864000));
        LicenseStore.StoreLicenseKey("KEY-1");
        EntitlementCheck.Transport = new CountingTransport
        {
            Respond = _ => LicenseHttpResponse.Completed(429, "{\"ok\":false,\"reason\":\"rate_limited\"}")
        };

        Assert.DoesNotThrowAsync(async () => await EntitlementCheck.RunCheckAsync(force: true));
        Assert.AreEqual(LicenseState.Licensed, LicenseStore.Current,
            "rate_limited is a benign reject; the token must ride its own exp with no state change.");
    }
}
