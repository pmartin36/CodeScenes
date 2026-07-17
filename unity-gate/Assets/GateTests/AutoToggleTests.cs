using System;
using NUnit.Framework;
using SceneBuilder.Editor;
using UnityEditor;

// Gate for SceneBuilderAutoToggle — the single persisted master boolean governing BOTH sync
// directions (spec checklist #11, persistence half; #1, default-on). EditorPrefs is real
// machine-global state, so every test restores whatever was there before it ran.
public class AutoToggleTests
{
    private bool _hadKey;
    private bool _originalValue;

    [SetUp]
    public void SetUp()
    {
        _hadKey = EditorPrefs.HasKey(SceneBuilderAutoToggle.PrefKey);
        _originalValue = EditorPrefs.GetBool(SceneBuilderAutoToggle.PrefKey, true);
        EditorPrefs.DeleteKey(SceneBuilderAutoToggle.PrefKey);
    }

    [TearDown]
    public void TearDown()
    {
        if (_hadKey)
        {
            EditorPrefs.SetBool(SceneBuilderAutoToggle.PrefKey, _originalValue);
        }
        else
        {
            EditorPrefs.DeleteKey(SceneBuilderAutoToggle.PrefKey);
        }
    }

    [Test]
    public void Auto_DefaultsOn_WhenKeyUnset()
    {
        Assert.IsTrue(SceneBuilderAutoToggle.Enabled,
            "A fresh project (no EditorPrefs key) must read auto-on with no setup.");
    }

    [Test]
    public void Auto_RoundTrips_FalseThenTrue_AcrossReReads()
    {
        SceneBuilderAutoToggle.Enabled = false;
        Assert.IsFalse(SceneBuilderAutoToggle.Enabled,
            "Setting false then re-reading (simulated restart) must return false.");

        SceneBuilderAutoToggle.Enabled = true;
        Assert.IsTrue(SceneBuilderAutoToggle.Enabled,
            "Setting true then re-reading (simulated restart) must return true.");
    }

    [Test]
    public void Auto_Toggle_FlipsValue_AndChecksMenuItem()
    {
        SceneBuilderAutoToggle.Enabled = true;

        SceneBuilderAutoToggle.Toggle();

        Assert.IsFalse(SceneBuilderAutoToggle.Enabled,
            "Toggle() must flip the persisted value.");
        Assert.IsFalse(Menu.GetChecked(SceneBuilderAutoToggle.MenuPath),
            "Toggle() must sync the menu checkmark to the new value.");

        SceneBuilderAutoToggle.Toggle();

        Assert.IsTrue(SceneBuilderAutoToggle.Enabled,
            "A second Toggle() must flip it back.");
        Assert.IsTrue(Menu.GetChecked(SceneBuilderAutoToggle.MenuPath),
            "The menu checkmark must track the flipped-back value.");
    }

    [Test]
    public void Auto_PrefKey_IsStable_AndPrefixed()
    {
        var key1 = SceneBuilderAutoToggle.PrefKey;
        var key2 = SceneBuilderAutoToggle.PrefKey;

        Assert.AreEqual(key1, key2, "PrefKey must be stable across calls for the same project.");
        Assert.IsTrue(key1.StartsWith("SceneBuilder.Auto.Enabled::", StringComparison.Ordinal),
            "PrefKey must carry the spec-named prefix.");
    }
}
