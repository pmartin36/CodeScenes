using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEngine;

// Pins the one gate over auto-sync arming, the PumpOnce dispatch backstop, and the four Build/Sync
// menu commands: LicenseGate.Allowed defaults true with no provider registered,
// and LicenseEnforcement's real LicenseState-backed verdict blocks every one of those sites when
// Unlicensed and re-applies with no manual step the moment a valid token is stored. Reuses
// LicenseStoreTests' signed-token recipe to drive the real provider through Licensed/Unlicensed.
public class LicenseEnforcementTests
{
    private RSA _rsa;

    private bool _hadToken;
    private string _originalToken;
    private readonly List<string> _tempDirs = new();

    [SetUp]
    public void SetUp()
    {
        _rsa = RSA.Create(2048);
        var verifier = new TokenVerifier(_rsa.ExportParameters(false));
        LicenseStore.VerifierProvider = () => verifier;

        _hadToken = EditorPrefs.HasKey(LicenseStore.TokenKey);
        _originalToken = EditorPrefs.GetString(LicenseStore.TokenKey, null);
        EditorPrefs.DeleteKey(LicenseStore.TokenKey);
        SessionState.EraseString(LicenseStore.SessionResolvedKey);
        SessionState.EraseString(LicenseStore.SessionStateKey);

        LicenseGate.ResetToDefault();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
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

        SessionState.EraseString(LicenseStore.SessionResolvedKey);
        SessionState.EraseString(LicenseStore.SessionStateKey);
        LicenseStore.VerifierProvider = () => TokenVerifier.Production;

        // Restore the real provider so no test here leaks a manually-injected gate state into any
        // other suite, then leave auto-sync in its default armed state for the next test file.
        LicenseEnforcement.Register();
        // Restore the auto-sync master toggle to its default (missing key reads ON) so a test that
        // flipped it for the menu-validate cases cannot leak an OFF state into another suite.
        EditorPrefs.DeleteKey(SceneBuilderAutoToggle.PrefKey);
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
        _rsa?.Dispose();

        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }

        _tempDirs.Clear();
    }

    private string TempFilePath(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb_licenseenforcement_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return Path.Combine(dir, name);
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

    private void StoreLicense() =>
        LicenseStore.StoreToken(SignToken(MachineIdentity.Hash, UnixNow() - 60, UnixNow() + 3600, "license"));

    [Test]
    public void ApplyToggleState_UnlicensedRealProvider_LeavesDisarmed()
    {
        LicenseEnforcement.Register();

        Assert.IsFalse(SceneBuilderAutoSync.IsArmed,
            "With no stored token the real provider resolves Unlicensed; ApplyToggleState must leave auto-sync disarmed.");
    }

    [Test]
    public void PumpOnce_UnlicensedRealProvider_DispatchesNoCycle()
    {
        var go = new GameObject("Target");
        try
        {
            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;

            LicenseEnforcement.Register();
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(0, SceneBuilderAutoSync.SceneToCodeCycleCount,
                "A pending scene change must not dispatch a cycle while the real provider resolves Unlicensed.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Changed_LicensedToUnlicensed_DisarmsWithNoManualApplyToggleStateCall()
    {
        StoreLicense();
        LicenseEnforcement.Register();
        Assert.IsTrue(SceneBuilderAutoSync.IsArmed, "Precondition: a valid license token must arm auto-sync.");

        LicenseStore.Clear();

        Assert.IsFalse(SceneBuilderAutoSync.IsArmed,
            "Clearing the token must fire LicenseStore.Changed -> LicenseEnforcement.Apply -> disarm, with no manual ApplyToggleState call in the test.");
    }

    [Test]
    public void Changed_UnlicensedToLicensed_ReArmsWithNoManualStep()
    {
        LicenseEnforcement.Register();
        Assert.IsFalse(SceneBuilderAutoSync.IsArmed, "Precondition: no token resolves Unlicensed and leaves auto-sync disarmed.");

        StoreLicense();

        Assert.IsTrue(SceneBuilderAutoSync.IsArmed,
            "Storing a valid token must fire Changed -> Apply -> re-arm with no manual step.");
    }

    [Test]
    public void MidSyncConsistency_SceneToCode_TransitionToUnlicensedDuringCycle_LeavesCompletedCycleFullyAppliedAndBlocksTheNext()
    {
        var go = new GameObject("Target");
        try
        {
            StoreLicense();
            LicenseEnforcement.Register();

            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            var applied = 0;
            SceneBuilderAutoSync.SceneToCodeExecutor = _ => applied++;

            // In-flight cycle: due while still Licensed, so it must run to completion.
            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);
            Assert.AreEqual(1, applied, "The in-flight scene->code cycle due while Licensed must complete, not be preempted mid-write.");

            LicenseStore.Clear();

            // A change arriving AFTER the transition must not dispatch a second cycle.
            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(1, applied,
                "Once Unlicensed, a scene change arriving after the transition must not dispatch a new cycle; the one completed cycle stays the only one applied.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void MidSyncConsistency_CodeToScene_TransitionToUnlicensedDuringCycle_LeavesCompletedCycleFullyAppliedAndBlocksTheNext()
    {
        var path = TempFilePath("Demo.cs");
        File.WriteAllText(path, "// external edit, first cycle");

        StoreLicense();
        LicenseEnforcement.Register();

        var now = 100.0;
        SceneBuilderAutoSync.Clock = () => now;
        var applied = 0;
        SceneBuilderAutoSync.CodeToSceneExecutor = _ => applied++;

        // In-flight cycle: due while still Licensed, so it must run to completion.
        SceneBuilderAutoSync.NotifySourceChanged(path);
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);
        Assert.AreEqual(1, applied, "The in-flight code->scene cycle due while Licensed must complete, not be preempted mid-write.");

        LicenseStore.Clear();

        // A change arriving AFTER the transition must not dispatch a second cycle.
        File.WriteAllText(path, "// external edit, second cycle");
        SceneBuilderAutoSync.NotifySourceChanged(path);
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        Assert.AreEqual(1, applied,
            "Once Unlicensed, a source change arriving after the transition must not dispatch a new cycle; the one completed cycle stays the only one applied.");
    }

    [Test]
    public void MenuValidates_UnlicensedRealProvider_AllFourReturnFalse()
    {
        LicenseEnforcement.Register();

        foreach (var menuItem in BuildSyncCommandPaths)
        {
            var validate = FindValidateMethod(menuItem);
            Assert.IsNotNull(validate, $"{menuItem} must have a paired [MenuItem(path, true)] validate method.");
            Assert.IsFalse((bool)validate.Invoke(null, null),
                $"{menuItem}'s validate must return false while the real provider resolves Unlicensed.");
        }
    }

    [Test]
    public void MenuValidates_LicensedAndAutoOff_AllFourReturnTrue()
    {
        StoreLicense();
        LicenseEnforcement.Register();
        SceneBuilderAutoToggle.Enabled = false; // manual mode: the debug Build/Sync commands are usable

        foreach (var menuItem in BuildSyncCommandPaths)
        {
            var validate = FindValidateMethod(menuItem);
            Assert.IsNotNull(validate, $"{menuItem} must have a paired [MenuItem(path, true)] validate method.");
            Assert.IsTrue((bool)validate.Invoke(null, null),
                $"{menuItem}'s validate must return true while Licensed and auto-sync is off.");
        }
    }

    [Test]
    public void MenuValidates_LicensedButAutoOn_AllFourReturnFalse()
    {
        StoreLicense();
        LicenseEnforcement.Register();
        SceneBuilderAutoToggle.Enabled = true; // auto-sync handles both directions; manual commands are redundant

        foreach (var menuItem in BuildSyncCommandPaths)
        {
            var validate = FindValidateMethod(menuItem);
            Assert.IsNotNull(validate, $"{menuItem} must have a paired [MenuItem(path, true)] validate method.");
            Assert.IsFalse((bool)validate.Invoke(null, null),
                $"{menuItem}'s validate must return false while auto-sync is on, so the manual command is disabled.");
        }
    }

    // Reflection guard (invariant, not a per-command test): every command [MenuItem] under
    // "CodeScenes/" other than the standing settings-toggle denylist entry must have a paired
    // validate, so a future sync entry point that skips the gate fails this check.
    [Test]
    public void EveryCodeScenesCommandMenuItem_HasAPairedValidate()
    {
        var denylist = new HashSet<string> { "CodeScenes/Auto-sync" };
        var commands = new List<(string path, MethodInfo method)>();
        var validates = new Dictionary<string, MethodInfo>();
        CollectCodeScenesMenuItems(commands, validates);

        Assert.IsTrue(commands.Count > 0,
            "Precondition: the SceneBuilder.Editor assembly must declare at least one CodeScenes/ command menu item.");

        foreach (var (path, _) in commands)
        {
            if (denylist.Contains(path))
            {
                continue;
            }

            Assert.IsTrue(validates.ContainsKey(path),
                $"{path} has no paired [MenuItem(path, true)] validate; a command that skips the gate must fail this check.");
        }
    }

    private static readonly string[] BuildSyncCommandPaths =
    {
        "CodeScenes/Build/Current Scene",
        "CodeScenes/Build/All Scenes",
        "CodeScenes/Sync/Current Scene",
        "CodeScenes/Sync/All Scenes",
    };

    private static MethodInfo FindValidateMethod(string menuItem)
    {
        var commands = new List<(string path, MethodInfo method)>();
        var validates = new Dictionary<string, MethodInfo>();
        CollectCodeScenesMenuItems(commands, validates);
        return validates.TryGetValue(menuItem, out var method) ? method : null;
    }

    private static void CollectCodeScenesMenuItems(List<(string path, MethodInfo method)> commands, Dictionary<string, MethodInfo> validates)
    {
        var assembly = typeof(SceneBuilderAutoSync).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                foreach (var raw in method.GetCustomAttributes(typeof(MenuItem), false))
                {
                    var attr = (MenuItem)raw;
                    if (!attr.menuItem.StartsWith("CodeScenes/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (attr.validate)
                    {
                        validates[attr.menuItem] = method;
                    }
                    else
                    {
                        commands.Add((attr.menuItem, method));
                    }
                }
            }
        }
    }
}
