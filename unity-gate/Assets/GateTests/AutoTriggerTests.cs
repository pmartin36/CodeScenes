using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SceneBuilder.Editor;
using UnityEditor;
using UnityEngine;

// Gate for SceneBuilderAutoSync — the trigger + debounce pump the whole auto-sync loop rests on
// (spec checklist #1, #2, #5). Drives the deterministic LOGIC seams directly (injectable Clock +
// explicit PumpOnce(now) + Notify*) rather than real ObjectChangeEvents/FileSystemWatcher timing,
// per research.md's design: transport is thin, logic is provable without wall-clock/async timing.
// A true mid-test domain reload cannot be exercised inside a single EditMode test; the Disarm/Arm
// test below is the documented stand-in for checklist #5 (re-subscribe survives).
public class AutoTriggerTests
{
    private readonly List<string> _tempDirs = new();

    [SetUp]
    public void SetUp()
    {
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
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
        var dir = Path.Combine(Path.GetTempPath(), "sb_autotrigger_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return Path.Combine(dir, name);
    }

    [Test]
    public void Trigger_SceneChange_SettlePast_RunsExactlyOneCycle_NoMenuClick()
    {
        var go = new GameObject("Target");
        try
        {
            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            var cycleCount = 0;
            SceneBuilderAutoSync.SceneToCodeExecutor = _ => cycleCount++;

            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(1, SceneBuilderAutoSync.SceneToCodeCycleCount,
                "A single scene change, once the settle window has passed, must run exactly one scene->code cycle with no menu click.");
            Assert.AreEqual(1, cycleCount, "The scene->code executor must be invoked exactly once.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Trigger_NRapidSceneChanges_WithinSettleWindow_RunExactlyOneCycle()
    {
        var go = new GameObject("Target");
        try
        {
            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            var cycleCount = 0;
            SceneBuilderAutoSync.SceneToCodeExecutor = _ => cycleCount++;

            for (var i = 0; i < 5; i++)
            {
                SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
                now += 0.05; // well inside the settle window; each arrival re-arms the deadline
            }
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(1, SceneBuilderAutoSync.SceneToCodeCycleCount,
                "5 rapid changes inside one settle window must coalesce into exactly ONE cycle, not 5.");
            Assert.AreEqual(1, cycleCount);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Trigger_SceneChange_BeforeSettle_DoesNotFire()
    {
        var go = new GameObject("Target");
        try
        {
            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });

            now += SceneBuilderAutoSync.SettleSeconds - 0.05; // still before the deadline
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(0, SceneBuilderAutoSync.SceneToCodeCycleCount,
                "A pump tick before the settle deadline must not run a cycle.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Trigger_SuppressedSceneWrite_IsDropped_NoCycle()
    {
        var go = new GameObject("Target");
        try
        {
            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;

            using (SuppressionScope.SuppressScene())
            {
                SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
            }

            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(0, SceneBuilderAutoSync.SceneToCodeCycleCount,
                "A scene change notified while SuppressionScope.SceneWriteSuppressed is true (our own write) must be dropped, never scheduling a cycle.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Trigger_ExternalSourceWrite_RunsOneCodeToSceneCycle()
    {
        var path = TempFilePath("Demo.cs");
        File.WriteAllText(path, "// external edit, never recorded via WriteIfChanged");

        var now = 100.0;
        SceneBuilderAutoSync.Clock = () => now;
        var cycleCount = 0;
        SceneBuilderAutoSync.CodeToSceneExecutor = _ => cycleCount++;

        SceneBuilderAutoSync.NotifySourceChanged(path);
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        Assert.AreEqual(1, SceneBuilderAutoSync.CodeToSceneCycleCount,
            "An external (unrecorded) source write must run exactly one code->scene cycle.");
        Assert.AreEqual(1, cycleCount);
    }

    [Test]
    public void Trigger_OwnSourceWrite_InRegistry_IsDropped_NoCycle()
    {
        var path = TempFilePath("Demo.cs");
        SceneBuilderPaths.WriteIfChanged(path, "// our own write"); // records (path, hash) in the registry

        var now = 100.0;
        SceneBuilderAutoSync.Clock = () => now;

        SceneBuilderAutoSync.NotifySourceChanged(path);
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        Assert.AreEqual(0, SceneBuilderAutoSync.CodeToSceneCycleCount,
            "A source write already recorded in SuppressionScope's own-write registry (blocker 5) must be dropped, never scheduling a code->scene cycle.");
    }

    [Test]
    public void Trigger_Disarmed_NoCycle_Then_ReArm_Fires()
    {
        var go = new GameObject("Target");
        try
        {
            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            var cycleCount = 0;
            SceneBuilderAutoSync.SceneToCodeExecutor = _ => cycleCount++;

            SceneBuilderAutoSync.Disarm();
            Assert.IsFalse(SceneBuilderAutoSync.IsArmed, "Disarm() must clear IsArmed.");

            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);
            Assert.AreEqual(0, SceneBuilderAutoSync.SceneToCodeCycleCount,
                "While disarmed, a scene change must not schedule a cycle.");

            SceneBuilderAutoSync.Arm();
            Assert.IsTrue(SceneBuilderAutoSync.IsArmed, "Arm() must set IsArmed.");

            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);
            Assert.AreEqual(1, SceneBuilderAutoSync.SceneToCodeCycleCount,
                "Re-arming after Disarm() and a subsequent change must fire a cycle — the machine-checkable " +
                "stand-in for domain-reload survival (checklist #5); a true reload cannot run mid-test.");
            Assert.AreEqual(1, cycleCount);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Trigger_ToggleOff_ApplyToggleState_Disarms_NoCycle()
    {
        var hadKey = EditorPrefs.HasKey(SceneBuilderAutoToggle.PrefKey);
        var originalValue = EditorPrefs.GetBool(SceneBuilderAutoToggle.PrefKey, true);
        try
        {
            SceneBuilderAutoToggle.Enabled = false;
            SceneBuilderAutoSync.ApplyToggleState();

            Assert.IsFalse(SceneBuilderAutoSync.IsArmed,
                "ApplyToggleState() with the master toggle OFF must disarm the loop.");

            var go = new GameObject("Target");
            try
            {
                var now = 100.0;
                SceneBuilderAutoSync.Clock = () => now;
                SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
                now += SceneBuilderAutoSync.SettleSeconds + 0.01;
                SceneBuilderAutoSync.PumpOnce(now);

                Assert.AreEqual(0, SceneBuilderAutoSync.SceneToCodeCycleCount,
                    "With auto toggled OFF, a scene change must not schedule a cycle.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
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
    public void Trigger_BothDirectionsPending_EachSettlesIndependently()
    {
        var go = new GameObject("Target");
        var path = TempFilePath("Demo.cs");
        File.WriteAllText(path, "// external");
        try
        {
            var sceneCycles = 0;
            var sourceCycles = 0;
            SceneBuilderAutoSync.SceneToCodeExecutor = _ => sceneCycles++;
            SceneBuilderAutoSync.CodeToSceneExecutor = _ => sourceCycles++;

            var sceneArm = 100.0;
            SceneBuilderAutoSync.Clock = () => sceneArm;
            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });

            var sourceArm = 100.1;
            SceneBuilderAutoSync.Clock = () => sourceArm;
            SceneBuilderAutoSync.NotifySourceChanged(path);

            // Scene direction's deadline (armed at t=100) elapses before source's (armed at t=100.1).
            var now = sceneArm + SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);
            Assert.AreEqual(1, sceneCycles, "Scene direction settles first and must run its cycle.");
            Assert.AreEqual(0, sourceCycles, "Source direction has not reached its own deadline yet.");

            now = sourceArm + SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);
            Assert.AreEqual(1, sceneCycles, "Scene direction must not re-fire on a later pump.");
            Assert.AreEqual(1, sourceCycles, "Source direction settles independently on its own later deadline.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
