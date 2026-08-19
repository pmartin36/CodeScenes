using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

// Pins the "Two builds" contract from the channel side: with CODESCENES_ASSET_STORE absent (as in
// this gate project) licensing compiles in, a no-token state fails closed, and the license menu
// item / transport / activation window are declared only inside the excludable licensing assembly
// so an Asset-Store build (where that assembly is gone) never ships them. LicenseChannel is the
// single C# owner of the define name the licensing asmdef's defineConstraint mirrors as JSON.
public class LicenseChannelTests
{
    private bool _hadToken;
    private string _originalToken;

    [SetUp]
    public void SetUp()
    {
        _hadToken = EditorPrefs.HasKey(LicenseStore.TokenKey);
        _originalToken = EditorPrefs.GetString(LicenseStore.TokenKey, null);
        EditorPrefs.DeleteKey(LicenseStore.TokenKey);
        SessionState.EraseString(LicenseStore.SessionResolvedKey);
        SessionState.EraseString(LicenseStore.SessionStateKey);

        LicenseGate.ResetToDefault();
        SceneBuilderAutoSync.ResetForTests();
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

        // Restore the real provider so no test here leaks a manually-set gate state into a
        // later suite.
        LicenseEnforcement.Register();
        SceneBuilderAutoSync.ResetForTests();
    }

    [Test]
    public void ChannelDefine_NamesTheAssetStoreExclusionSymbol()
    {
        Assert.AreEqual("CODESCENES_ASSET_STORE", LicenseChannel.AssetStoreDefine,
            "LicenseChannel.AssetStoreDefine is the single C# owner of the define the licensing " +
            "asmdef's defineConstraint mirrors; it must name the exact same symbol.");
    }

    [Test]
    public void IsAssetStoreBuild_IsFalseWhenTheChannelDefineIsAbsent()
    {
        // This gate project never sets CODESCENES_ASSET_STORE, so the Gumroad-channel value is
        // the only one observable here.
        Assert.IsFalse(LicenseChannel.IsAssetStoreBuild,
            "With CODESCENES_ASSET_STORE absent this is the Gumroad channel, not the Asset-Store one.");
    }

    [Test]
    public void LicensingAsmdef_CarriesTheExcludeOnAssetStoreConstraint()
    {
        string text = LoadLicensingAsmdefText();

        Assert.IsTrue(text.Contains("!" + LicenseChannel.AssetStoreDefine),
            "The licensing asmdef's defineConstraints must exclude the assembly when " +
            "LicenseChannel.AssetStoreDefine is set, so an Asset-Store build never compiles it in.");
    }

    [Test]
    public void FailClosed_NoTokenBlocksTheRealGate()
    {
        LicenseEnforcement.Register();

        Assert.AreEqual(LicenseState.Unlicensed, LicenseStore.Current,
            "No stored token must resolve Unlicensed.");
        Assert.IsFalse(LicenseGate.Allowed,
            "With the real provider registered and no token, the gate must block sync.");

        SceneBuilderAutoSync.ApplyToggleState();
        Assert.IsFalse(SceneBuilderAutoSync.IsArmed,
            "Unlicensed must not arm auto-sync.");

        bool ran = false;
        LicenseGate.RunGuarded(() => ran = true);
        Assert.IsFalse(ran, "RunGuarded must not execute its body when the gate is blocked.");
    }

    [Test]
    public void DefaultAllowed_NoProviderRegisteredAllowsSyncEvenWithNoToken()
    {
        // Emulates an Asset-Store build, where the licensing assembly (and its
        // LicenseEnforcement.Register() call) is excluded from compilation entirely, so
        // LicenseGate never receives anything but its own default-allowed provider.
        LicenseGate.ResetToDefault();

        Assert.IsFalse(EditorPrefs.HasKey(LicenseStore.TokenKey), "Precondition: no token stored.");
        Assert.IsTrue(LicenseGate.Allowed,
            "With no provider registered, the gate must default to allowed regardless of license state.");

        bool ran = false;
        LicenseGate.RunGuarded(() => ran = true);
        Assert.IsTrue(ran, "RunGuarded must execute its body when the gate defaults to allowed.");
    }

    [Test]
    public void TransportType_IsDeclaredOnlyInTheLicensingAssembly()
    {
        Assert.AreEqual("SceneBuilder.Licensing.Editor", typeof(ILicenseTransport).Assembly.GetName().Name,
            "ILicenseTransport must live in the excludable licensing assembly, never the main one.");
    }

    [Test]
    public void ActivationWindowType_IsDeclaredOnlyInTheLicensingAssembly()
    {
        Assert.AreEqual("SceneBuilder.Licensing.Editor", typeof(LicenseWindow).Assembly.GetName().Name,
            "LicenseWindow must live in the excludable licensing assembly, never the main one.");
    }

    [Test]
    public void LicenseMenuItem_IsDeclaredOnlyInTheLicensingAssembly()
    {
        var licensingHit = FindMenuItemCommand(typeof(ILicenseTransport).Assembly, "CodeScenes/License");
        Assert.IsNotNull(licensingHit,
            "Expected a [MenuItem(\"CodeScenes/License\")] command declared in the licensing assembly.");

        var mainHit = FindMenuItemCommand(typeof(SceneBuilderAutoSync).Assembly, "CodeScenes/License");
        Assert.IsNull(mainHit,
            "The main assembly must declare no CodeScenes/License menu item — an Asset-Store build " +
            "(licensing assembly excluded) must not expose this command.");
    }

    private static string LoadLicensingAsmdefText()
    {
        string path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName("SceneBuilder.Licensing.Editor");
        Assert.IsNotNull(path, "SceneBuilder.Licensing.Editor must exist in the editor compilation graph.");

        var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
        Assert.IsNotNull(asset, $"Expected to load the licensing asmdef at {path}");
        return asset.text;
    }

    private static MethodInfo FindMenuItemCommand(System.Reflection.Assembly assembly, string menuItem)
    {
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                foreach (var raw in method.GetCustomAttributes(typeof(MenuItem), false))
                {
                    var attr = (MenuItem)raw;
                    if (!attr.validate && string.Equals(attr.menuItem, menuItem, StringComparison.Ordinal))
                    {
                        return method;
                    }
                }
            }
        }

        return null;
    }
}
