using NUnit.Framework;
using SceneBuilder.Editor;
using UnityEditor;
using UnityEngine;

// Gate for the play-mode gate (spec checklist #12): auto pauses in Play mode and resumes on
// return to edit mode without losing the toggle state. Per research.md, a true mid-test domain
// reload into Play mode cannot be exercised inside a single EditMode test, so these drive the
// LOGIC seam directly (SceneBuilderAutoSync.OnPlayModeStateChanged) — the same documented
// stand-in AutoTriggerTests.cs uses for checklist #5 (re-subscribe survives).
public class AutoPlayModeTests
{
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
    }

    [Test]
    public void PlayMode_EnteredPlayMode_Disarms_SceneEditDoesNotSync()
    {
        var go = new GameObject("Target");
        try
        {
            Assert.IsTrue(SceneBuilderAutoSync.IsArmed, "Precondition: ResetForTests() leaves the loop armed.");

            SceneBuilderAutoSync.OnPlayModeStateChanged(PlayModeStateChange.EnteredPlayMode);
            Assert.IsFalse(SceneBuilderAutoSync.IsArmed, "EnteredPlayMode must disarm the loop.");

            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            var cycleCount = 0;
            SceneBuilderAutoSync.SceneToCodeExecutor = _ => cycleCount++;

            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(0, SceneBuilderAutoSync.SceneToCodeCycleCount,
                "A scene edit made while in Play mode must not sync.");
            Assert.AreEqual(0, cycleCount);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void PlayMode_ExitToEditMode_ReArms_SceneEditSyncs()
    {
        var go = new GameObject("Target");
        try
        {
            SceneBuilderAutoSync.OnPlayModeStateChanged(PlayModeStateChange.EnteredPlayMode);
            Assert.IsFalse(SceneBuilderAutoSync.IsArmed, "Precondition: entering Play mode disarms.");

            SceneBuilderAutoSync.OnPlayModeStateChanged(PlayModeStateChange.EnteredEditMode);
            Assert.IsTrue(SceneBuilderAutoSync.IsArmed,
                "EnteredEditMode must re-arm when the master toggle is ON.");

            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            var cycleCount = 0;
            SceneBuilderAutoSync.SceneToCodeExecutor = _ => cycleCount++;

            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(1, SceneBuilderAutoSync.SceneToCodeCycleCount,
                "A scene edit after returning to edit mode must sync exactly once.");
            Assert.AreEqual(1, cycleCount);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void PlayMode_ExitToEditMode_WithToggleOff_StaysDisarmed()
    {
        var hadKey = EditorPrefs.HasKey(SceneBuilderAutoToggle.PrefKey);
        var originalValue = EditorPrefs.GetBool(SceneBuilderAutoToggle.PrefKey, true);
        try
        {
            SceneBuilderAutoToggle.Enabled = false;
            SceneBuilderAutoSync.ApplyToggleState();
            Assert.IsFalse(SceneBuilderAutoSync.IsArmed, "Precondition: toggle OFF disarms.");

            SceneBuilderAutoSync.OnPlayModeStateChanged(PlayModeStateChange.EnteredPlayMode);
            SceneBuilderAutoSync.OnPlayModeStateChanged(PlayModeStateChange.EnteredEditMode);

            Assert.IsFalse(SceneBuilderAutoSync.IsArmed,
                "EnteredEditMode must NOT re-arm when the master toggle is OFF — toggle state is preserved " +
                "across the play-mode round trip.");
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
}
