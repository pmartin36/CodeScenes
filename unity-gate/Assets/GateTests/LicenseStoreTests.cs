using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEngine;

// Pins LicenseStore's derived-state contract: LicenseState always comes from the one persisted
// token via TokenVerifier (never an independent state-set path); a domain reload reads the
// SessionState-cached verdict instead of re-verifying; and no file lands under Assets/ or the
// project directory as a side effect of storing a token or reloading.
public class LicenseStoreTests
{
    private RSA _rsa;
    private int _verifyCount;

    private bool _hadToken;
    private string _originalToken;
    private bool _hadBaseline;
    private string _originalBaseline;

    [SetUp]
    public void SetUp()
    {
        _rsa = RSA.Create(2048);
        _verifyCount = 0;
        var verifier = new TokenVerifier(_rsa.ExportParameters(false));
        LicenseStore.VerifierProvider = () =>
        {
            _verifyCount++;
            return verifier;
        };

        _hadToken = EditorPrefs.HasKey(LicenseStore.TokenKey);
        _originalToken = EditorPrefs.GetString(LicenseStore.TokenKey, null);
        _hadBaseline = EditorPrefs.HasKey(LicenseStore.BaselineKey);
        _originalBaseline = EditorPrefs.GetString(LicenseStore.BaselineKey, null);
        EditorPrefs.DeleteKey(LicenseStore.TokenKey);
        EditorPrefs.DeleteKey(LicenseStore.BaselineKey);
        SessionState.EraseString(LicenseStore.SessionResolvedKey);
        SessionState.EraseString(LicenseStore.SessionStateKey);
    }

    [TearDown]
    public void TearDown()
    {
        if (_hadToken)
        {
            EditorPrefs.SetString(LicenseStore.TokenKey, _originalToken);
        }
        else
        {
            EditorPrefs.DeleteKey(LicenseStore.TokenKey);
        }

        if (_hadBaseline)
        {
            EditorPrefs.SetString(LicenseStore.BaselineKey, _originalBaseline);
        }
        else
        {
            EditorPrefs.DeleteKey(LicenseStore.BaselineKey);
        }

        SessionState.EraseString(LicenseStore.SessionResolvedKey);
        SessionState.EraseString(LicenseStore.SessionStateKey);
        LicenseStore.VerifierProvider = () => TokenVerifier.Production;
        _rsa?.Dispose();
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

    [Test]
    public void StoreToken_ValidLicenseToken_ResolvesLicensed()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 3600, "license"));

        Assert.AreEqual(LicenseState.Licensed, LicenseStore.Current,
            "A stored token that verifies with kind \"license\" must resolve Licensed.");
    }

    [Test]
    public void StoreToken_ValidTrialToken_ResolvesTrial()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 3600, "trial"));

        Assert.AreEqual(LicenseState.Trial, LicenseStore.Current,
            "A stored token that verifies with kind \"trial\" must resolve Trial.");
    }

    [Test]
    public void NoStoredToken_ResolvesUnlicensed()
    {
        Assert.AreEqual(LicenseState.Unlicensed, LicenseStore.Current,
            "With no stored token there is nothing to verify, so Current must resolve Unlicensed.");
    }

    [Test]
    public void Clear_AfterStoringValidToken_ResolvesUnlicensed()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 3600));

        LicenseStore.Clear();

        Assert.AreEqual(LicenseState.Unlicensed, LicenseStore.Current,
            "Clear() deletes the persisted token, so Current must fall back to Unlicensed.");
    }

    [Test]
    public void StoreToken_ExpiredToken_ResolvesUnlicensed()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 7200, UnixNow() - 3600));

        Assert.AreEqual(LicenseState.Unlicensed, LicenseStore.Current,
            "A stored token that fails verification (here: expired) must resolve Unlicensed, not throw.");
    }

    [Test]
    public void ReloadCache_TenSimulatedReloads_VerifyOnlyOnStore()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 3600));
        Assert.AreEqual(1, _verifyCount, "Storing a token performs exactly one verification.");

        for (int i = 0; i < 10; i++)
        {
            LicenseStore.SimulateDomainReload();
            Assert.AreEqual(LicenseState.Licensed, LicenseStore.Current,
                $"Reload #{i + 1} must still resolve Licensed from the SessionState cache.");
        }

        Assert.AreEqual(1, _verifyCount,
            "Ten simulated reloads after the store must perform zero additional verifications; the SessionState cache, not the verifier, must answer each one.");
    }

    [Test]
    public void StoreTokenAndReloads_CreateNoFileUnderAssetsOrProjectRoot()
    {
        string assetsRoot = Application.dataPath;
        string projectRoot = SceneBuilderPaths.ProjectRoot;
        var assetsBefore = Directory.GetFiles(assetsRoot, "*", SearchOption.AllDirectories).ToHashSet();
        var rootFilesBefore = Directory.GetFiles(projectRoot, "*", SearchOption.TopDirectoryOnly).ToHashSet();

        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 3600));
        for (int i = 0; i < 10; i++)
        {
            LicenseStore.SimulateDomainReload();
            _ = LicenseStore.Current;
        }

        var assetsAfter = Directory.GetFiles(assetsRoot, "*", SearchOption.AllDirectories).ToHashSet();
        var rootFilesAfter = Directory.GetFiles(projectRoot, "*", SearchOption.TopDirectoryOnly).ToHashSet();

        Assert.IsTrue(assetsAfter.SetEquals(assetsBefore),
            "Storing a token and cycling reloads must not create or remove any file under Assets/.");
        Assert.IsTrue(rootFilesAfter.SetEquals(rootFilesBefore),
            "Storing a token and cycling reloads must not create or remove any top-level file in the project directory; the token and baseline live in EditorPrefs, not on disk here.");
    }

    [Test]
    public void Changed_FiresOnce_WhenStoreTokenTransitionsUnlicensedToLicensed()
    {
        int fired = 0;
        Action handler = () => fired++;
        LicenseStore.Changed += handler;
        try
        {
            LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 3600));
        }
        finally
        {
            LicenseStore.Changed -= handler;
        }

        Assert.AreEqual(1, fired,
            "Changed must fire exactly once when StoreToken moves the resolved state from Unlicensed to Licensed.");
    }

    [Test]
    public void Changed_DoesNotFire_WhenStoreTokenResolvesSameState()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 3600));

        int fired = 0;
        Action handler = () => fired++;
        LicenseStore.Changed += handler;
        try
        {
            LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 30, UnixNow() + 7200));
        }
        finally
        {
            LicenseStore.Changed -= handler;
        }

        Assert.AreEqual(0, fired,
            "Changed must not fire when a second StoreToken resolves to the same LicenseState.");
    }

    [Test]
    public void Changed_FiresOnClear_WhenStateTransitionsLicensedToUnlicensed()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 3600));

        int fired = 0;
        Action handler = () => fired++;
        LicenseStore.Changed += handler;
        try
        {
            LicenseStore.Clear();
        }
        finally
        {
            LicenseStore.Changed -= handler;
        }

        Assert.AreEqual(1, fired,
            "Changed must fire exactly once when Clear() moves the resolved state from Licensed to Unlicensed.");
    }

    [Test]
    public void ObservedUtc_RoundTripsThroughEditorPrefs()
    {
        var utc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        LicenseStore.RecordObservedUtc(utc);

        Assert.AreEqual(utc, LicenseStore.LastObservedUtc,
            "LastObservedUtc must round-trip the exact UTC value passed to RecordObservedUtc.");
    }

    [Test]
    public void ObservedUtc_Unset_ReturnsDateTimeMinValue()
    {
        Assert.AreEqual(DateTime.MinValue, LicenseStore.LastObservedUtc,
            "With no baseline ever recorded, LastObservedUtc must return DateTime.MinValue.");
    }

    [Test]
    public void TokenKey_ContainsMachineHash()
    {
        StringAssert.Contains(MachineIdentity.Hash, LicenseStore.TokenKey,
            "TokenKey must scope the EditorPrefs entry to this machine's hash.");
    }
}
