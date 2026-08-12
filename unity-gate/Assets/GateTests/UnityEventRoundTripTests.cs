using NUnit.Framework;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// Spec 09's Unity confirmation checklist (specs/09-m8-unityevents.md:462-480): the live bidirectional
// round trip for UnityEvent persistent-call listeners, driven end-to-end through
// RoundTripProofHarness (the real SceneBuilderBuild/SceneBuilderSync path) rather than any hand-built
// plan or snapshot. Each item is its own case so a failure names exactly which leg of the checklist
// broke; item 8 (idempotence) is the only one that depends on every earlier leg already converging.
public class UnityEventRoundTripTests
{
    private const string AssetDir = "Assets/UnityEventRoundTripTestsTmp";
    private const string MaterialPath = AssetDir + "/Item4Material.mat";

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(AssetDir))
        {
            AssetDatabase.DeleteAsset(AssetDir);
        }
    }

    private static SerializedProperty CallsArray(Component comp, string eventPath)
    {
        var so = new SerializedObject(comp);
        var calls = so.FindProperty($"{eventPath}.m_PersistentCalls.m_Calls");
        Assert.IsNotNull(calls, $"'{eventPath}.m_PersistentCalls.m_Calls' not found on {comp.GetType().Name}");
        return calls;
    }

    private static SerializedProperty CallAt(Component comp, string eventPath, int index)
    {
        var calls = CallsArray(comp, eventPath);
        Assert.Greater(calls.arraySize, index,
            $"Persistent call array on '{eventPath}' has {calls.arraySize} entries; expected at least {index + 1}.");
        return calls.GetArrayElementAtIndex(index);
    }

    // ---- Item 1: author a void OnClick, Materialize, the live Button carries one persistent entry ---

    [Test]
    public void Item1_AuthorVoidOnClick_LiveButtonShowsOnePersistentEntry()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var opener = scene.Add(\"Door\").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();\n" +
                "        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>(b => b.OnClick(opener, o => o.Open()));\n",
            assertScene: ctx =>
            {
                AssertOpenerEntry(ctx.RequireRoot("Btn"), ctx.RequireRoot("Door"));
            });
    }

    private static void AssertOpenerEntry(GameObject btnRoot, GameObject doorRoot)
    {
        var button = btnRoot.GetComponent<Button>();
        var doorOpener = doorRoot.GetComponent<DoorOpener>();
        var call = CallAt(button, "m_OnClick", 0);

        Assert.AreEqual(doorOpener, call.FindPropertyRelative("m_Target").objectReferenceValue,
            "m_Target must resolve to the live DoorOpener COMPONENT, not its owning GameObject");
        Assert.AreEqual("Open", call.FindPropertyRelative("m_MethodName").stringValue);
        Assert.AreEqual((int)PersistentListenerMode.Void, call.FindPropertyRelative("m_Mode").intValue);
        Assert.AreEqual((int)UnityEventCallState.RuntimeOnly, call.FindPropertyRelative("m_CallState").intValue);
    }

    // ---- Item 2: an Inspector retarget patches only the target argument, siblings untouched --------

    [Test]
    public void Item2_InspectorRetarget_SourceOnClickTargetArgUpdates_SiblingsUntouched()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        var opener = scene.Add(\"DoorA\").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();\n" +
                "        var altDoor = scene.Add(\"DoorB\").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();\n" +
                "        var lamp = scene.Add(\"LampHost\").Component<Lamp>(_ => { }).Ref<Lamp>();\n" +
                "        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>(b =>\n" +
                "        {\n" +
                "            b.OnClick(opener, o => o.Open());\n" +
                "            b.OnClick(lamp, l => l.SetLevel(3));\n" +
                "        });\n",
            mutateScene: ctx =>
            {
                var button = ctx.RequireRoot("Btn").GetComponent<Button>();
                var altDoorOpener = ctx.RequireRoot("DoorB").GetComponent<DoorOpener>();
                var call = CallAt(button, "m_OnClick", 0);
                call.FindPropertyRelative("m_Target").objectReferenceValue = altDoorOpener;
                call.serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(button);
            },
            assertSource: ctx =>
            {
                StringAssert.IsMatch(
                    @"\.OnClick\(altDoor,\s*\w+\s*=>\s*\w+\.Open\(\)\)", ctx.Source,
                    "Retargeting the live listener did not patch the source target argument.\n" + ctx.Source);
                StringAssert.DoesNotContain(
                    ".OnClick(opener,", ctx.Source,
                    "The stale pre-retarget target argument is still present in source.\n" + ctx.Source);
                StringAssert.Contains(
                    "SetLevel(3)", ctx.Source,
                    "The sibling listener (lamp.SetLevel) must be untouched by the retarget of the first listener.\n"
                        + ctx.Source);
            });
    }

    // ---- Item 3: an Inspector method+arg change patches in place ------------------------------------

    [Test]
    public void Item3_InspectorMethodToIntArg7_SourceBecomesLambdaWithLiteral7()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        var lamp = scene.Add(\"LampHost\").Component<Lamp>(_ => { }).Ref<Lamp>();\n" +
                "        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>(b => b.OnClick(lamp, l => l.SetLevel(0)));\n",
            mutateScene: ctx =>
            {
                var button = ctx.RequireRoot("Btn").GetComponent<Button>();
                var call = CallAt(button, "m_OnClick", 0);
                call.FindPropertyRelative("m_Arguments.m_IntArgument").intValue = 7;
                call.serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(button);
            },
            assertSource: ctx =>
            {
                StringAssert.IsMatch(
                    @"lamp,\s*\w+\s*=>\s*\w+\.SetLevel\(7\)", ctx.Source,
                    "The edited int argument did not patch the source to SetLevel(7).\n" + ctx.Source);
                StringAssert.DoesNotContain(
                    "SetLevel(0)", ctx.Source,
                    "The stale pre-edit argument is still present in source.\n" + ctx.Source);
            });
    }

    // ---- Item 4: an Inspector object-mode asset argument renders the typed asset reference ----------

    [Test]
    public void Item4_ObjectModeAssetArg_SourceArgBecomesAssetRefAs_AssetsGainsGuid()
    {
        if (!AssetDatabase.IsValidFolder(AssetDir))
        {
            AssetDatabase.CreateFolder("Assets", "UnityEventRoundTripTestsTmp");
        }

        var material = new Material(Shader.Find("Standard"));
        AssetDatabase.CreateAsset(material, MaterialPath);
        Assert.IsTrue(
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out var guid, out long _),
            "Failed to resolve the created test material's GUID");

        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        var marquee = scene.Add(\"MarqueeHost\").Component<Marquee>(_ => { }).Ref<Marquee>();\n" +
                "        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>(b => b.OnClick(marquee, m => m.SetVisible(false)));\n",
            mutateScene: ctx =>
            {
                var button = ctx.RequireRoot("Btn").GetComponent<Button>();
                var call = CallAt(button, "m_OnClick", 0);
                call.FindPropertyRelative("m_MethodName").stringValue = "SetMaterial";
                call.FindPropertyRelative("m_Mode").intValue = (int)PersistentListenerMode.Object;
                call.FindPropertyRelative("m_Arguments.m_ObjectArgument").objectReferenceValue = material;
                call.FindPropertyRelative("m_Arguments.m_ObjectArgumentAssemblyTypeName").stringValue =
                    typeof(Material).AssemblyQualifiedName;
                call.serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(button);
            },
            assertSource: ctx =>
            {
                StringAssert.Contains(
                    ".As<Material>()", ctx.Source,
                    "The object-mode asset argument did not render with the .As<T>() typing scaffold.\n" + ctx.Source);
                StringAssert.Contains(
                    "SetMaterial(", ctx.Source, "The edited method (SetMaterial) did not reach source.\n" + ctx.Source);

                var sidecar = System.IO.File.ReadAllText(ctx.SidecarPath);
                StringAssert.Contains(
                    guid, sidecar, "The asset's GUID did not reach the sidecar Assets[] table.\n" + sidecar);
            });
    }

    // ---- Item 5: an Inspector call-state change patches in place, explicit callState suffix ---------

    [Test]
    public void Item5_InspectorCallStateEditorAndRuntime_SourceGainsCallState()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        var opener = scene.Add(\"Door\").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();\n" +
                "        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>(b => b.OnClick(opener, o => o.Open()));\n",
            mutateScene: ctx =>
            {
                var button = ctx.RequireRoot("Btn").GetComponent<Button>();
                var call = CallAt(button, "m_OnClick", 0);
                call.FindPropertyRelative("m_CallState").intValue = (int)UnityEventCallState.EditorAndRuntime;
                call.serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(button);
            },
            assertSource: ctx =>
            {
                StringAssert.Contains(
                    "callState: UnityEngine.Events.UnityEventCallState.EditorAndRuntime", ctx.Source,
                    "The edited call state did not reach source as an explicit callState argument.\n" + ctx.Source);
            });
    }

    // ---- Item 6: an Inspector remove drops only that statement ---------------------------------------

    [Test]
    public void Item6_InspectorRemoveEntry_SourceOnClickStatementRemoved()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        var opener = scene.Add(\"Door\").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();\n" +
                "        var lamp = scene.Add(\"LampHost\").Component<Lamp>(_ => { }).Ref<Lamp>();\n" +
                "        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>(b =>\n" +
                "        {\n" +
                "            b.OnClick(opener, o => o.Open());\n" +
                "            b.OnClick(lamp, l => l.SetLevel(3));\n" +
                "        });\n",
            mutateScene: ctx =>
            {
                var button = ctx.RequireRoot("Btn").GetComponent<Button>();
                var calls = CallsArray(button, "m_OnClick");
                calls.DeleteArrayElementAtIndex(0);
                calls.serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(button);
            },
            assertSource: ctx =>
            {
                StringAssert.DoesNotContain(
                    ".OnClick(opener, o => o.Open())", ctx.Source,
                    "Removing the live listener did not remove its OnClick(...) statement.\n" + ctx.Source);
                StringAssert.Contains(
                    "SetLevel(3)", ctx.Source,
                    "The remaining listener (lamp.SetLevel) must survive the removal of its sibling.\n" + ctx.Source);
            });
    }

    // ---- Item 7: a dynamic OnEvent listener round-trips to a first-sync fixed point --------------

    [Test]
    public void Item7_SliderOnValueChangedDynamicToHudSetValue_SourceIsOnEventDynamic_RoundTrips()
    {
        var result = RoundTripProofHarness.ProveCodeToSceneFixedPoint(
            usings: "",
            body:
                "        var hud = scene.Add(\"HudHost\").Component<Hud>(_ => { }).Ref<Hud>();\n" +
                "        scene.Add(\"SliderHost\").Component<UnityEngine.UI.Slider>(s => s.OnEvent(x => x.onValueChanged, hud, h => h.SetValue, dynamic: true));\n",
            assertScene: ctx =>
            {
                var slider = ctx.RequireRoot("SliderHost").GetComponent<Slider>();
                var hud = ctx.RequireRoot("HudHost").GetComponent<Hud>();
                var call = CallAt(slider, "m_OnValueChanged", 0);

                Assert.AreEqual(hud, call.FindPropertyRelative("m_Target").objectReferenceValue);
                Assert.AreEqual("SetValue", call.FindPropertyRelative("m_MethodName").stringValue);
                Assert.AreEqual((int)PersistentListenerMode.EventDefined, call.FindPropertyRelative("m_Mode").intValue);
            },
            assertConverged: ctx =>
            {
                Assert.AreEqual(
                    0, ctx.Sync.PatchEdits,
                    "The first sync over a converged dynamic-listener Slider must apply zero patch edits.");
            });

        StringAssert.Contains(
            ".OnEvent(x => x.onValueChanged, hud, h => h.SetValue, dynamic: true)", result.ConvergedSource,
            "The converged source did not keep the dynamic OnEvent form.\n" + result.ConvergedSource);
    }

    // ---- Item 8: a second Materialize with no code change applies zero plan ops (live idempotence) --

    [Test]
    public void Item8_SecondMaterializeNoCodeChange_EmitsZeroPlanOps()
    {
        var result = RoundTripProofHarness.ProveCodeToSceneFixedPoint(
            usings: "",
            body:
                "        var opener = scene.Add(\"Door\").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();\n" +
                "        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>(b => b.OnClick(opener, o => o.Open()));\n",
            assertScene: ctx =>
            {
                AssertOpenerEntry(ctx.RequireRoot("Btn"), ctx.RequireRoot("Door"));
            },
            assertConverged: ctx =>
            {
                Assert.AreEqual(
                    0, ctx.Sync.PatchEdits,
                    "The convergence sync over an unchanged authored listener must apply zero patch edits.");
            });

        Assert.IsFalse(
            result.ConvergenceSync.Changed,
            "A second Materialize/Sync pass with no code change must be a no-op (zero plan ops, live idempotence).");
    }
}
