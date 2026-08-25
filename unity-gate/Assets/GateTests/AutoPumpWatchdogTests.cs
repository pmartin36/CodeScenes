#nullable enable
using System;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEngine;

// Gate for the pump-survives-play-mode hardening: a play-mode round trip whose deferred re-arm
// callback has not yet run leaves the pump armed only once the transition settles, and a
// self-healing watchdog re-arms a dead pump on the SAME class-lifetime hook without depending on
// the update pump it restores.
public class AutoPumpWatchdogTests
{
    [SetUp]
    public void SetUp()
    {
        LicenseGate.ResetToDefault();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        SceneBuilderAutoSync.IsPlayingProbe = () => EditorApplication.isPlayingOrWillChangePlaymode;
        SceneBuilderAutoSync.ScheduleReArm = action => EditorApplication.delayCall += () => action();
        LicenseEnforcement.Register();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
    }

    [Test]
    public void DeferredReArm_IsPlayingStillTrueAtCallback_ArmsOnceTransitionSettles_SceneEditSyncs()
    {
        var go = new GameObject("Target");
        try
        {
            SceneBuilderAutoSync.IsPlayingProbe = () => true;
            Action? captured = null;
            SceneBuilderAutoSync.ScheduleReArm = cb => captured = cb;

            SceneBuilderAutoSync.OnPlayModeStateChanged(PlayModeStateChange.EnteredPlayMode);
            Assert.IsFalse(SceneBuilderAutoSync.IsArmed, "Precondition: entering Play mode disarms.");

            SceneBuilderAutoSync.OnPlayModeStateChanged(PlayModeStateChange.EnteredEditMode);
            Assert.IsFalse(SceneBuilderAutoSync.IsArmed,
                "A still-playing read at the EnteredEditMode callback must not arm synchronously; " +
                "the re-arm defers until the transition settles.");

            SceneBuilderAutoSync.IsPlayingProbe = () => false;
            Assert.IsNotNull(captured, "EnteredEditMode must schedule the deferred re-arm.");
            captured!();
            Assert.IsTrue(SceneBuilderAutoSync.IsArmed,
                "Once the deferred callback runs after the transition settles, the pump re-arms.");

            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(1, SceneBuilderAutoSync.SceneToCodeCycleCount,
                "A scene edit after the deferred re-arm settles must sync exactly once.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Watchdog_DirectCall_DeadPump_ReArms_AndSceneEditSyncs()
    {
        var go = new GameObject("Target");
        try
        {
            SceneBuilderAutoSync.IsPlayingProbe = () => false;
            SceneBuilderAutoSync.Disarm();
            Assert.IsFalse(SceneBuilderAutoSync.IsArmed, "Precondition: pump is dead.");

            SceneBuilderAutoSync.PumpWatchdog();
            Assert.IsTrue(SceneBuilderAutoSync.IsArmed,
                "The watchdog re-arms a dead pump when the toggle is on, the license allows it, and play mode is off.");

            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(1, SceneBuilderAutoSync.SceneToCodeCycleCount,
                "A scene edit after the watchdog re-arms must sync exactly once.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Watchdog_ToggleOff_DoesNotReArm()
    {
        var hadKey = EditorPrefs.HasKey(SceneBuilderAutoToggle.PrefKey);
        var originalValue = EditorPrefs.GetBool(SceneBuilderAutoToggle.PrefKey, true);
        try
        {
            SceneBuilderAutoToggle.Enabled = false;
            SceneBuilderAutoSync.IsPlayingProbe = () => false;
            SceneBuilderAutoSync.Disarm();

            SceneBuilderAutoSync.PumpWatchdog();

            Assert.IsFalse(SceneBuilderAutoSync.IsArmed,
                "The watchdog must not re-arm while the master toggle is off.");
        }
        finally
        {
            if (hadKey)
            {
                EditorPrefs.SetBool(SceneBuilderAutoToggle.PrefKey, originalValue);
            }
            else
            {
                EditorPrefs.DeleteKey(SceneBuilderAutoToggle.PrefKey);
            }
        }
    }

    [Test]
    public void Watchdog_LicenseDenied_DoesNotReArm()
    {
        try
        {
            LicenseGate.SetProvider(() => false);
            SceneBuilderAutoSync.IsPlayingProbe = () => false;
            SceneBuilderAutoSync.Disarm();

            SceneBuilderAutoSync.PumpWatchdog();

            Assert.IsFalse(SceneBuilderAutoSync.IsArmed,
                "The watchdog must not re-arm while the license gate denies it.");
        }
        finally
        {
            LicenseGate.ResetToDefault();
        }
    }

    [Test]
    public void Watchdog_WhilePlaying_DoesNotReArm()
    {
        SceneBuilderAutoSync.IsPlayingProbe = () => true;
        SceneBuilderAutoSync.Disarm();

        SceneBuilderAutoSync.PumpWatchdog();

        Assert.IsFalse(SceneBuilderAutoSync.IsArmed,
            "The watchdog must not re-arm while play mode is still active.");
    }

    [Test]
    public void Watchdog_AlreadyArmed_NoDoubleArm()
    {
        var go = new GameObject("Target");
        try
        {
            SceneBuilderAutoSync.IsPlayingProbe = () => false;
            Assert.IsTrue(SceneBuilderAutoSync.IsArmed, "Precondition: ResetForTests() leaves the loop armed.");

            SceneBuilderAutoSync.PumpWatchdog();
            SceneBuilderAutoSync.PumpWatchdog();

            Assert.IsTrue(SceneBuilderAutoSync.IsArmed);

            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(1, SceneBuilderAutoSync.SceneToCodeCycleCount,
                "Calling the watchdog on an already-armed pump must not subscribe a second time and double-fire a cycle.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
