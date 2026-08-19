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

// Pins LicenseBanner.Current: the near-expiry threshold (inclusive at the warning window,
// exclusive just outside it), the steady-state no-banner case for a comfortably-valid
// license or trial token, the persistent Activate banner when Unlicensed, the
// offline-no-trial banner surfacing TrialClaim's outcome, the Activate button wiring,
// and live recompute across a state change with no stale memo.
public class LicenseBannerTests
{
    private RSA _rsa;
    private DateTime _baseNow;

    private bool _hadToken;
    private string _originalToken;
    private bool _hadBaseline;
    private string _originalBaseline;
    private bool _hadLicenseKey;
    private string _originalLicenseKey;
    private bool _hadLastAttempt;
    private string _originalLastAttempt;
    private Action _originalOpenActivation;

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

        _baseNow = DateTime.UtcNow;
        LicenseClock.UtcNow = () => _baseNow;
        TrialClaim.ResetForTests();
        _originalOpenActivation = LicenseBanner.OpenActivation;
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
        TrialClaim.ResetForTests();
        LicenseBanner.OpenActivation = _originalOpenActivation;
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

    private long ToUnix(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    private sealed class UnreachableTransport : ILicenseTransport
    {
        public Task<LicenseHttpResponse> SendAsync(string requestJson, CancellationToken ct = default) =>
            Task.FromResult(LicenseHttpResponse.Unreachable());
    }

    [Test]
    public void NearExpiryToken_TwoDaysRemaining_ShowsNearExpiryWithDaysRemaining()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, ToUnix(_baseNow) - 60, ToUnix(_baseNow) + 86400 * 2));

        var state = LicenseBanner.Current;

        Assert.AreEqual(LicenseBannerKind.NearExpiry, state.Kind);
        Assert.AreEqual(2, state.DaysRemaining);
        Assert.That(state.Message, Does.Contain("2"));
    }

    [Test]
    public void ExpiryExactlyAtWarningWindow_ShowsNearExpiry()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, ToUnix(_baseNow) - 60,
            ToUnix(_baseNow) + (long)LicenseClock.NearExpiryWarningWindow.TotalSeconds));

        Assert.AreEqual(LicenseBannerKind.NearExpiry, LicenseBanner.Current.Kind,
            "The warning window boundary is inclusive.");
    }

    [Test]
    public void ExpiryJustOutsideWarningWindow_ShowsNoBanner()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, ToUnix(_baseNow) - 60,
            ToUnix(_baseNow) + (long)LicenseClock.NearExpiryWarningWindow.TotalSeconds + 3600));

        Assert.AreEqual(LicenseBannerKind.None, LicenseBanner.Current.Kind,
            "Just outside the warning window must show no banner.");
    }

    [Test]
    public void ComfortablyValidLicenseToken_ShowsNoBanner()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, ToUnix(_baseNow) - 60, ToUnix(_baseNow) + 86400 * 10));

        Assert.AreEqual(LicenseBannerKind.None, LicenseBanner.Current.Kind,
            "A licensed user in steady state must see no banner.");
    }

    [Test]
    public void NearExpiryTrialToken_ShowsNearExpiry()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, ToUnix(_baseNow) - 60, ToUnix(_baseNow) + 86400 * 2, kind: "trial"));

        var state = LicenseBanner.Current;

        Assert.AreEqual(LicenseBannerKind.NearExpiry, state.Kind,
            "The near-expiry warning applies uniformly to a trial token, not only a license token.");
        Assert.AreEqual(2, state.DaysRemaining);
    }

    [Test]
    public void Unlicensed_NoTrialOutcome_ShowsActivateBanner()
    {
        Assert.AreEqual(LicenseBannerKind.Activate, LicenseBanner.Current.Kind);
    }

    [Test]
    public void ActivateBanner_RaiseActivate_InvokesOpenActivationExactlyOnce()
    {
        int calls = 0;
        LicenseBanner.OpenActivation = () => calls++;

        Assert.AreEqual(LicenseBannerKind.Activate, LicenseBanner.Current.Kind);
        LicenseBanner.RaiseActivate();

        Assert.AreEqual(1, calls);
    }

    [Test]
    public void OfflineNoTrialOutcome_ShowsOfflineNoTrialBanner()
    {
        TrialClaim.ClaimAsync(new UnreachableTransport()).GetAwaiter().GetResult();
        LogAssert.Expect(LogType.Warning, new Regex(".*"));

        var state = LicenseBanner.Current;

        Assert.AreEqual(LicenseBannerKind.OfflineNoTrial, state.Kind);
        Assert.That(state.Message.ToLowerInvariant(), Does.Contain("trial").Or.Contain("offline"));
    }

    [Test]
    public void StateChangesFromUnlicensedToLicensed_CurrentRecomputesWithoutStaleMemo()
    {
        Assert.AreEqual(LicenseBannerKind.Activate, LicenseBanner.Current.Kind, "starts Unlicensed");

        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, ToUnix(_baseNow) - 60, ToUnix(_baseNow) + 86400 * 10));
        Assert.AreEqual(LicenseBannerKind.None, LicenseBanner.Current.Kind,
            "must flip to steady state with no explicit recompute call");

        LicenseStore.Clear();
        Assert.AreEqual(LicenseBannerKind.Activate, LicenseBanner.Current.Kind, "must flip back to Activate");
    }
}
