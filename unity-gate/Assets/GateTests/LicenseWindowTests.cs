using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEngine;

// Pins LicenseWindowModel, the controller behind the CodeScenes/License activation window:
// a successful activate persists BOTH the raw key and the token and reports the seat count; the
// activate request carries the raw (un-hashed) machine id plus label/os; a seat_limit reject
// populates seat management from the reject body with no second transport call and stores no
// token; Remove issues `release` with the retained key and seat hash and a subsequent activate
// reuses that key; Buy opens the Gumroad product URL; a trial token's remaining days surface on
// the no-license view; opening the window while Licensed loads its seat list from `machines`
// (not the reject-only `seats` field); every rejection reason routes to the no-license view with
// a message and stores nothing. Also pins LicenseWindow's structural surface: the
// "CodeScenes/License" menu entry point and a CreateGUI that builds without throwing.
public class LicenseWindowTests
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

    private string SignToken(string sub, long iat, long exp, string kind = "license")
    {
        var payload = new LicenseToken { v = 1, kind = kind, sub = sub, lic = "LIC-1", iat = iat, exp = exp };
        string json = JsonUtility.ToJson(payload);
        string head = Base64UrlEncode(Encoding.UTF8.GetBytes(json));
        byte[] sig = _rsa.SignData(Encoding.ASCII.GetBytes(head), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return head + "." + Base64UrlEncode(sig);
    }

    private long ToUnix(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    // Captures every request the model sends and replays a queued response per call, so a test
    // can both script a multi-step flow (activate -> remove -> activate) and assert on the raw
    // request JSON without touching LicenseWire's private DTOs.
    private sealed class ScriptedTransport : ILicenseTransport
    {
        private readonly Queue<LicenseHttpResponse> _responses = new Queue<LicenseHttpResponse>();
        public readonly List<string> Requests = new List<string>();

        public void Enqueue(LicenseHttpResponse response) => _responses.Enqueue(response);

        public Task<LicenseHttpResponse> SendAsync(string requestJson, CancellationToken ct = default)
        {
            Requests.Add(requestJson);
            LicenseHttpResponse response = _responses.Count > 0 ? _responses.Dequeue() : LicenseHttpResponse.Unreachable();
            return Task.FromResult(response);
        }
    }

    // Field names span every request shape (activate/release); JsonUtility leaves fields absent
    // from a given request's JSON at their default, so one probe type reads any of them.
    [Serializable]
    private class RequestProbe
    {
        public string action;
        public string licenseKey;
        public string machineId;
        public string label;
        public string os;
        public string hash;
    }

    [Test]
    public void ActivateAsync_Success_PersistsKeyAndToken_ShowsLicensedWithSeatCount()
    {
        var transport = new ScriptedTransport();
        string token = SignToken(MachineIdentity.Hash, ToUnix(_baseNow) - 60, ToUnix(_baseNow) + 86400 * 14);
        transport.Enqueue(LicenseHttpResponse.Completed(200,
            "{\"ok\":true,\"token\":\"" + token + "\",\"seatsUsed\":2,\"seatsTotal\":3}"));
        var model = new LicenseWindowModel { Transport = transport };

        model.ActivateAsync("KEY-1").GetAwaiter().GetResult();

        Assert.AreEqual(LicenseWindowView.Licensed, model.View);
        Assert.That(model.Message, Does.Contain("2 of 3 seats in use"));
        Assert.AreEqual("KEY-1", LicenseStore.CurrentLicenseKey,
            "a successful activate must persist the raw key, or the daily refresh can never re-activate.");
        Assert.AreEqual(LicenseState.Licensed, LicenseStore.Current);
    }

    [Test]
    public void ActivateAsync_SendsRawMachineIdLabelAndOs_NeverTheHash()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(LicenseHttpResponse.Unreachable());
        var model = new LicenseWindowModel { Transport = transport };

        model.ActivateAsync("KEY-1").GetAwaiter().GetResult();

        Assert.AreEqual(1, transport.Requests.Count);
        var probe = JsonUtility.FromJson<RequestProbe>(transport.Requests[0]);
        Assert.AreEqual("activate", probe.action);
        Assert.AreEqual(MachineIdentity.Raw, probe.machineId);
        Assert.AreNotEqual(MachineIdentity.Hash, probe.machineId,
            "the server hashes the machine id itself; the client must send it un-rehashed.");
        Assert.AreEqual(MachineIdentity.Label, probe.label);
        Assert.AreEqual(MachineIdentity.Os, probe.os);
    }

    [Test]
    public void ActivateAsync_SeatLimit_PopulatesSeatManagementFromRejectWithNoSecondCall()
    {
        string myHash = MachineIdentity.Hash;
        var transport = new ScriptedTransport();
        transport.Enqueue(LicenseHttpResponse.Completed(200,
            "{\"ok\":false,\"reason\":\"seat_limit\",\"seatsTotal\":3,\"seats\":["
            + "{\"hash\":\"" + myHash + "\",\"label\":\"This Mac\",\"os\":\"macOS\"},"
            + "{\"hash\":\"h2\",\"label\":\"PC\",\"os\":\"Windows\"},"
            + "{\"hash\":\"h3\",\"label\":\"Linux Box\",\"os\":\"Linux\"}]}"));
        var model = new LicenseWindowModel { Transport = transport };

        model.ActivateAsync("KEY-1").GetAwaiter().GetResult();

        Assert.AreEqual(LicenseWindowView.SeatManagement, model.View);
        Assert.AreEqual(3, model.Seats.Length);
        Assert.AreEqual(1, transport.Requests.Count,
            "the seat_limit reject already carries the seat list; no second round trip.");
        Assert.IsTrue(model.IsCurrentMachine(model.Seats[0]));
        Assert.IsFalse(model.IsCurrentMachine(model.Seats[1]));
        Assert.AreEqual(LicenseState.Unlicensed, LicenseStore.Current,
            "a rejected activate must not store a token.");
    }

    [Test]
    public void RemoveSeatAsync_IssuesReleaseWithTheActiveKeyAndSeatHash()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(LicenseHttpResponse.Completed(200,
            "{\"ok\":false,\"reason\":\"seat_limit\",\"seatsTotal\":3,\"seats\":["
            + "{\"hash\":\"h1\",\"label\":\"Mac 1\",\"os\":\"macOS\"},"
            + "{\"hash\":\"h2\",\"label\":\"PC\",\"os\":\"Windows\"},"
            + "{\"hash\":\"h3\",\"label\":\"Linux Box\",\"os\":\"Linux\"}]}"));
        transport.Enqueue(LicenseHttpResponse.Completed(200,
            "{\"ok\":true,\"machines\":["
            + "{\"hash\":\"h2\",\"label\":\"PC\",\"os\":\"Windows\"},"
            + "{\"hash\":\"h3\",\"label\":\"Linux Box\",\"os\":\"Linux\"}]}"));
        var model = new LicenseWindowModel { Transport = transport };
        model.ActivateAsync("KEY-1").GetAwaiter().GetResult();

        model.RemoveSeatAsync("h1").GetAwaiter().GetResult();

        Assert.AreEqual(2, transport.Requests.Count);
        var probe = JsonUtility.FromJson<RequestProbe>(transport.Requests[1]);
        Assert.AreEqual("release", probe.action);
        Assert.AreEqual("KEY-1", probe.licenseKey,
            "the key was entered but never persisted (activation failed on seat_limit); it must be retained in-memory.");
        Assert.AreEqual("h1", probe.hash);
        Assert.AreEqual(2, model.Seats.Length);
    }

    [Test]
    public void ActivateAsync_RetryAfterRemove_ReusesTheActiveKeyAndShowsLicensed()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(LicenseHttpResponse.Completed(200,
            "{\"ok\":false,\"reason\":\"seat_limit\",\"seatsTotal\":3,\"seats\":["
            + "{\"hash\":\"h1\",\"label\":\"Mac 1\",\"os\":\"macOS\"},"
            + "{\"hash\":\"h2\",\"label\":\"PC\",\"os\":\"Windows\"},"
            + "{\"hash\":\"h3\",\"label\":\"Linux Box\",\"os\":\"Linux\"}]}"));
        transport.Enqueue(LicenseHttpResponse.Completed(200,
            "{\"ok\":true,\"machines\":["
            + "{\"hash\":\"h2\",\"label\":\"PC\",\"os\":\"Windows\"},"
            + "{\"hash\":\"h3\",\"label\":\"Linux Box\",\"os\":\"Linux\"}]}"));
        string token = SignToken(MachineIdentity.Hash, ToUnix(_baseNow) - 60, ToUnix(_baseNow) + 86400 * 14);
        transport.Enqueue(LicenseHttpResponse.Completed(200,
            "{\"ok\":true,\"token\":\"" + token + "\",\"seatsUsed\":3,\"seatsTotal\":3}"));
        var model = new LicenseWindowModel { Transport = transport };
        model.ActivateAsync("KEY-1").GetAwaiter().GetResult();
        model.RemoveSeatAsync("h1").GetAwaiter().GetResult();

        model.ActivateAsync("KEY-1").GetAwaiter().GetResult();

        Assert.AreEqual(LicenseWindowView.Licensed, model.View);
        Assert.That(model.Message, Does.Contain("3 of 3 seats in use"));
        Assert.AreEqual("KEY-1", LicenseStore.CurrentLicenseKey);
    }

    [Test]
    public void Buy_InvokesOpenUrlWithTheGumroadProductUrl()
    {
        string captured = null;
        var model = new LicenseWindowModel { OpenUrl = url => captured = url };

        model.Buy();

        Assert.AreEqual(LicenseWindowModel.GumroadProductUrl, captured);
    }

    [Test]
    public void Initialize_Unlicensed_ShowsNoLicenseViewWithNoTrialDays()
    {
        var model = new LicenseWindowModel();

        model.Initialize();

        Assert.AreEqual(LicenseWindowView.NoLicense, model.View);
        Assert.AreEqual(0, model.TrialDaysRemaining);
    }

    [Test]
    public void Initialize_TrialTokenFiveDaysRemaining_ShowsNoLicenseViewWithTrialDaysRemaining()
    {
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, ToUnix(_baseNow) - 60, ToUnix(_baseNow) + 86400 * 5, kind: "trial"));
        var model = new LicenseWindowModel();

        model.Initialize();

        Assert.AreEqual(LicenseWindowView.NoLicense, model.View);
        Assert.AreEqual(5, model.TrialDaysRemaining);
    }

    [Test]
    public void Initialize_Licensed_LoadsSeatsFromMachinesNotSeats()
    {
        string token = SignToken(MachineIdentity.Hash, ToUnix(_baseNow) - 60, ToUnix(_baseNow) + 86400 * 14);
        LicenseStore.StoreToken(token);
        LicenseStore.StoreLicenseKey("KEY-1");
        var transport = new ScriptedTransport();
        transport.Enqueue(LicenseHttpResponse.Completed(200,
            "{\"ok\":true,\"machines\":["
            + "{\"hash\":\"h1\",\"label\":\"Mac 1\",\"os\":\"macOS\"},"
            + "{\"hash\":\"h2\",\"label\":\"PC\",\"os\":\"Windows\"}]}"));
        var model = new LicenseWindowModel { Transport = transport };

        model.Initialize();

        Assert.AreEqual(LicenseWindowView.Licensed, model.View);
        Assert.AreEqual(2, model.Seats.Length,
            "a `seats`/`release` success list is carried in `machines`, not the reject-only `seats` field.");
        Assert.AreEqual("Mac 1", model.Seats[0].label);
    }

    [Test]
    public void ActivateAsync_InvalidKeyReject_ShowsNoLicenseWithMessageAndStoresNothing()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(LicenseHttpResponse.Completed(200, "{\"ok\":false,\"reason\":\"invalid_key\"}"));
        var model = new LicenseWindowModel { Transport = transport };

        model.ActivateAsync("BAD-KEY").GetAwaiter().GetResult();

        Assert.AreEqual(LicenseWindowView.NoLicense, model.View);
        Assert.IsNotEmpty(model.Message);
        Assert.AreEqual(LicenseState.Unlicensed, LicenseStore.Current);
        Assert.IsNull(LicenseStore.CurrentLicenseKey);
    }

    [Test]
    public void ActivateAsync_TransportUnreachable_ShowsOfflineMessageAndStoresNothing()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(LicenseHttpResponse.Unreachable());
        var model = new LicenseWindowModel { Transport = transport };

        model.ActivateAsync("KEY-1").GetAwaiter().GetResult();

        Assert.AreEqual(LicenseWindowView.NoLicense, model.View);
        Assert.IsNotEmpty(model.Message);
        Assert.AreEqual(LicenseState.Unlicensed, LicenseStore.Current);
    }

    [Test]
    public void LicenseExpiry_TryGetRemaining_NoStoredToken_ReturnsFalse()
    {
        bool result = LicenseExpiry.TryGetRemaining(out TimeSpan remaining);

        Assert.IsFalse(result);
    }

    [Test]
    public void LicenseWindow_Open_IsRegisteredAsMenuItemAtCodeScenesLicense()
    {
        MethodInfo method = typeof(LicenseWindow).GetMethod("Open", BindingFlags.Public | BindingFlags.Static);
        Assert.IsNotNull(method, "LicenseWindow must declare a static Open() entry point for the menu.");

        var attr = method.GetCustomAttribute<MenuItem>();
        Assert.IsNotNull(attr,
            "Open() must carry [MenuItem(\"CodeScenes/License\")]; LicenseBanner opens this exact path.");
        Assert.AreEqual("CodeScenes/License", attr.menuItem);
    }

    [Test]
    public void LicenseWindow_CreateGUI_BuildsWithoutThrowing()
    {
        var window = ScriptableObject.CreateInstance<LicenseWindow>();
        try
        {
            MethodInfo method = typeof(LicenseWindow).GetMethod("CreateGUI", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "LicenseWindow must declare CreateGUI(), Unity's UI Toolkit window entry point.");
            Assert.DoesNotThrow(() => method.Invoke(window, null),
                "CreateGUI must build the model, bind the UXML tree, and wire callbacks without throwing.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(window);
        }
    }
}
