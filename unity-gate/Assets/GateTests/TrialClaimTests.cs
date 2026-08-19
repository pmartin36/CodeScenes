using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

// Pins TrialClaim's first-run trial flow: a reachable never-seen machine claims a
// server-stamped trial, an unreachable first run surfaces an observable offline
// message instead of fabricating a trial, a reinstalled trial reissues the
// server's remaining time rather than a fresh offline budget, and a benign
// reject (trial_expired) leaves state untouched without throwing.
public class TrialClaimTests
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

    private string SignToken(string sub, long iat, long exp, string kind = "trial")
    {
        var payload = new LicenseToken { v = 1, kind = kind, sub = sub, lic = "LIC-1", iat = iat, exp = exp };
        string json = JsonUtility.ToJson(payload);
        string head = Base64UrlEncode(Encoding.UTF8.GetBytes(json));
        byte[] sig = _rsa.SignData(Encoding.ASCII.GetBytes(head), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return head + "." + Base64UrlEncode(sig);
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private sealed class CannedTransport : ILicenseTransport
    {
        private readonly LicenseHttpResponse _response;

        public CannedTransport(LicenseHttpResponse response)
        {
            _response = response;
        }

        public Task<LicenseHttpResponse> SendAsync(string requestJson, CancellationToken ct = default)
        {
            return Task.FromResult(_response);
        }
    }

    [Test]
    public void FirstRun_ReachableNeverSeenMachine_ClaimsTrialWithServerExpiry()
    {
        long now = UnixNow();
        string token = SignToken(MachineIdentity.Hash, now, now + 86400 * 14);
        var transport = new CannedTransport(
            LicenseHttpResponse.Completed(200, "{\"ok\":true,\"token\":\"" + token + "\"}"));

        int changed = 0;
        LicenseStore.Changed += Handler;
        try
        {
            TrialClaim.ClaimAsync(transport).GetAwaiter().GetResult();
        }
        finally
        {
            LicenseStore.Changed -= Handler;
        }

        Assert.AreEqual(LicenseState.Trial, LicenseStore.Current,
            "A reachable server that has never seen this machine must transition to Trial.");
        Assert.AreEqual(TrialClaimOutcome.Claimed, TrialClaim.LastOutcome);
        Assert.AreEqual(1, changed, "Storing the claimed trial token must fire LicenseStore.Changed.");
        return;

        void Handler() => changed++;
    }

    [Test]
    public void FirstRun_Unreachable_SurfacesOfflineMessageAndFabricatesNoTrial()
    {
        var transport = new CannedTransport(LicenseHttpResponse.Unreachable());
        LogAssert.Expect(LogType.Warning, new Regex(".*"));

        TrialClaim.ClaimAsync(transport).GetAwaiter().GetResult();

        Assert.AreEqual(LicenseState.Unlicensed, LicenseStore.Current,
            "An offline first run must not fabricate a trial.");
        Assert.IsTrue(string.IsNullOrEmpty(LicenseStore.CurrentToken));
        Assert.AreEqual(TrialClaimOutcome.OfflineNoTrial, TrialClaim.LastOutcome);
    }

    [Test]
    public void Reinstall_TrialStartedTenDaysAgo_ReissuesRemainingTimeNotFresh14Days()
    {
        long now = UnixNow();
        long originalStart = now - 86400 * 10;
        string token = SignToken(MachineIdentity.Hash, originalStart, now + 86400 * 4);
        var transport = new CannedTransport(
            LicenseHttpResponse.Completed(200, "{\"ok\":true,\"token\":\"" + token + "\"}"));

        TrialClaim.ClaimAsync(transport).GetAwaiter().GetResult();

        Assert.AreEqual(LicenseState.Trial, LicenseStore.Current);
        var result = LicenseStore.VerifierProvider().Verify(LicenseStore.CurrentToken, MachineIdentity.Hash);
        Assert.AreEqual(TokenVerifyStatus.Valid, result.Status);
        Assert.That(result.Token.exp, Is.LessThan(now + 86400 * 5),
            "A reinstalled trial must reissue the server's remaining time, not a fresh 14-day budget.");
    }

    [Test]
    public void TrialExpiredOnReinstall_StaysUnlicensedAndDoesNotThrow()
    {
        var transport = new CannedTransport(
            LicenseHttpResponse.Completed(200, "{\"ok\":false,\"reason\":\"trial_expired\"}"));

        Assert.DoesNotThrowAsync(async () => await TrialClaim.ClaimAsync(transport));

        Assert.AreEqual(LicenseState.Unlicensed, LicenseStore.Current);
        Assert.IsTrue(string.IsNullOrEmpty(LicenseStore.CurrentToken));
        Assert.AreEqual(TrialClaimOutcome.Unavailable, TrialClaim.LastOutcome);
    }
}
