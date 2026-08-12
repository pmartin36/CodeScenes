using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using SceneBuilder.Editor;
using SceneBuilder.Core.Reconcile;

// The adapter's SceneSnapshot.MemberSpellings producer: every serialized UnityEvent field of every
// component in a snapshot gets its public C# spelling derived via SerializedMemberMap and carried on
// the snapshot, so the Core reconciler can reconcile a scene-authored listener on ANY UnityEvent
// field (not only Button.m_OnClick) into `.OnEvent(...)` source without misreporting it as
// UnsyncableListener. A UnityEvent field with no compiling public spelling still correctly reports
// UnsyncableListener.
public class MemberSpellingsProducerTests
{
    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [Test]
    public void OnPlainVoidListener_ReconcilesIntoSource_NoUnsyncableNote()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        var door = scene.Add(\"Door\").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();\n" +
                "        scene.Add(\"PingHost\").Component<Pinger>(_ => { });\n",
            mutateScene: ctx =>
            {
                var pinger = ctx.RequireRoot("PingHost").GetComponent<Pinger>();
                var door = ctx.RequireRoot("Door").GetComponent<DoorOpener>();
                UnityEventTools.AddVoidPersistentListener(pinger.onPinged, door.Open);
                EditorUtility.SetDirty(pinger);
            },
            assertSource: ctx =>
            {
                StringAssert.IsMatch(
                    @"\.OnEvent\(x => x\.onPinged,\s*door,\s*\w+\s*=>\s*\w+\.Open\(\)\)", ctx.Source,
                    "A listener added on Pinger.onPinged did not reconcile into an .OnEvent(...) statement.\n" + ctx.Source);

                var unsyncable = ctx.Sync.Notes.Where(n => n.Kind == ConflictKind.UnsyncableListener).ToArray();
                Assert.IsEmpty(
                    unsyncable,
                    "Adding a listener on a spellable UnityEvent field must not report UnsyncableListener.\n"
                        + string.Join("\n", unsyncable.Select(n => n.Reason)));
            });
    }

    [Test]
    public void OnFloatDynamicListener_ReconcilesIntoSource()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        var mixer = scene.Add(\"MixerHost\").Component<Mixer>(_ => { }).Ref<Mixer>();\n" +
                "        scene.Add(\"PingHost\").Component<Pinger>(_ => { });\n",
            mutateScene: ctx =>
            {
                var pinger = ctx.RequireRoot("PingHost").GetComponent<Pinger>();
                var mixer = ctx.RequireRoot("MixerHost").GetComponent<Mixer>();
                UnityEventTools.AddPersistentListener<float>(pinger.onLevelChanged, mixer.SetVol);
                EditorUtility.SetDirty(pinger);
            },
            assertSource: ctx =>
            {
                StringAssert.IsMatch(
                    @"\.OnEvent\(x => x\.onLevelChanged,\s*mixer,\s*\w+\s*=>\s*\w+\.SetVol,\s*dynamic:\s*true\)",
                    ctx.Source,
                    "A dynamic listener added on Pinger.onLevelChanged did not reconcile into a dynamic .OnEvent(...) statement.\n"
                        + ctx.Source);

                var unsyncable = ctx.Sync.Notes.Where(n => n.Kind == ConflictKind.UnsyncableListener).ToArray();
                Assert.IsEmpty(
                    unsyncable,
                    "Adding a dynamic listener on a spellable UnityEvent field must not report UnsyncableListener.\n"
                        + string.Join("\n", unsyncable.Select(n => n.Reason)));
            });
    }

    [Test]
    public void SnapshotReaderRead_PopulatesMemberSpellings_Directly()
    {
        new GameObject("PingHost").AddComponent<Pinger>();

        var snapshot = SceneSnapshotReader.Read(EditorSceneManager.GetActiveScene(), resolveSceneRef: null);

        var entry = snapshot.MemberSpellings.FirstOrDefault(m =>
            m.Type.FullName == typeof(Pinger).FullName && m.SerializedPath == "onPinged");

        Assert.IsNotNull(
            entry,
            "SceneSnapshotReader.Read produced no MemberSpelling entry for Pinger.onPinged. Entries: "
                + string.Join(", ", snapshot.MemberSpellings.Select(m => $"{m.Type.FullName}.{m.SerializedPath}->{m.PublicName}")));
        Assert.AreEqual("onPinged", entry.PublicName);
    }

    [Test]
    public void MixedScene_ButtonReconciles_UnspellableStillReports()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        var door = scene.Add(\"Door\").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();\n" +
                "        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>(_ => { });\n" +
                "        scene.Add(\"HiddenHost\").Component<HiddenEventHost>(_ => { });\n",
            mutateScene: ctx =>
            {
                var door = ctx.RequireRoot("Door").GetComponent<DoorOpener>();

                var button = ctx.RequireRoot("Btn").GetComponent<Button>();
                UnityEventTools.AddVoidPersistentListener(button.onClick, door.Open);
                EditorUtility.SetDirty(button);

                var hiddenHost = ctx.RequireRoot("HiddenHost").GetComponent<HiddenEventHost>();
                var field = typeof(HiddenEventHost).GetField("hiddenEvent", BindingFlags.NonPublic | BindingFlags.Instance);
                var hiddenEvent = (UnityEvent)field.GetValue(hiddenHost);
                UnityEventTools.AddVoidPersistentListener(hiddenEvent, door.Open);
                EditorUtility.SetDirty(hiddenHost);
            },
            assertSource: ctx =>
            {
                StringAssert.IsMatch(
                    @"\.OnClick\(door,\s*\w+\s*=>\s*\w+\.Open\(\)\)", ctx.Source,
                    "The Button.m_OnClick listener did not reconcile.\n" + ctx.Source);

                var note = ctx.Sync.Notes.SingleOrDefault(n =>
                    n.Kind == ConflictKind.UnsyncableListener
                    && n.Located != null
                    && n.Located.ComponentTypeFullName == typeof(HiddenEventHost).FullName);
                Assert.IsNotNull(
                    note,
                    "A UnityEvent field with no compiling public spelling must still report UnsyncableListener.");
                StringAssert.Contains("no public C# spelling", note.Reason);
            });
    }
}
