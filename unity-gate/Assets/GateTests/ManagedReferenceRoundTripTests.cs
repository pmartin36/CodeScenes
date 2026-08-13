using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// Spec 10's Unity confirmation checklist (specs/10-m9-serializereference.md:169-184): the live
// bidirectional round trip for [SerializeReference] polymorphic fields, driven end-to-end through
// RoundTripProofHarness (the real SceneBuilderBuild/SceneBuilderSync path) rather than any
// hand-built plan or snapshot. Each item is its own case so a failure names exactly which leg of
// the checklist broke.
public class ManagedReferenceRoundTripTests
{
    // ---- Item 1: author a managed instance, Materialize, the live field holds it ------------------

    [Test]
    public void Item1_AuthorAggressive_Materialize_LiveStrategyIsAggressiveRange5()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        scene.Add(\"Host\").Component<AiBrain>(c => c.SetRef(x => x.strategy, new Aggressive { range = 5f }));\n",
            assertScene: ctx =>
            {
                var brain = ctx.RequireRoot("Host").GetComponent<AiBrain>();
                var strategy = new SerializedObject(brain).FindProperty("strategy").managedReferenceValue as Aggressive;
                Assert.IsNotNull(strategy, "The live strategy field is not an Aggressive instance.");
                Assert.AreEqual(5f, strategy.range);
            });
    }

    // ---- Item 2: an Inspector field edit patches only that value, concrete type untouched ----------

    [Test]
    public void Item2_InspectorEditRange5To9_SourceRangeBecomes9f()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        scene.Add(\"Host\").Component<AiBrain>(c => c.SetRef(x => x.strategy, new Aggressive { range = 5f }));\n",
            mutateScene: ctx =>
            {
                var brain = ctx.RequireRoot("Host").GetComponent<AiBrain>();
                var strategyProp = new SerializedObject(brain).FindProperty("strategy");
                strategyProp.FindPropertyRelative("range").floatValue = 9f;
                strategyProp.serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(brain);
            },
            assertSource: ctx =>
            {
                StringAssert.Contains(
                    "range = 9f", ctx.Source,
                    "The edited field did not reach source as range = 9f.\n" + ctx.Source);
                StringAssert.Contains(
                    "new Aggressive", ctx.Source,
                    "The concrete type must be unchanged by a plain field edit.\n" + ctx.Source);
                StringAssert.DoesNotContain(
                    "range = 5f", ctx.Source,
                    "The stale pre-edit field value is still present in source.\n" + ctx.Source);
            });
    }

    // ---- Item 3: an Inspector concrete-type switch rewrites the whole value, no stale fields --------

    [Test]
    public void Item3_InspectorSwitchTypeToFlee_SourceBecomesNewFlee_NoStaleAggressiveFields()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        scene.Add(\"Host\").Component<AiBrain>(c => c.SetRef(x => x.strategy, new Aggressive { range = 5f }));\n",
            mutateScene: ctx =>
            {
                var brain = ctx.RequireRoot("Host").GetComponent<AiBrain>();
                var strategyProp = new SerializedObject(brain).FindProperty("strategy");
                strategyProp.managedReferenceValue = new Flee { speed = 3f };
                strategyProp.serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(brain);
            },
            assertSource: ctx =>
            {
                StringAssert.Contains(
                    "new Flee", ctx.Source, "The type switch did not reach source as new Flee.\n" + ctx.Source);
                StringAssert.Contains(
                    "speed = 3f", ctx.Source, "The new type's field did not reach source.\n" + ctx.Source);
                StringAssert.DoesNotContain(
                    "Aggressive", ctx.Source,
                    "No stale Aggressive fields/type may survive a concrete-type switch.\n" + ctx.Source);
            });
    }

    // ---- Item 4: an Inspector set-to-None patches the field to a null SetRef assignment -------------

    [Test]
    public void Item4_InspectorSetToNone_SourceBecomesSetRefNull()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        scene.Add(\"Host\").Component<AiBrain>(c => c.SetRef(x => x.strategy, new Aggressive { range = 5f }));\n",
            mutateScene: ctx =>
            {
                var brain = ctx.RequireRoot("Host").GetComponent<AiBrain>();
                var strategyProp = new SerializedObject(brain).FindProperty("strategy");
                strategyProp.managedReferenceValue = null;
                strategyProp.serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(brain);
            },
            assertSource: ctx =>
            {
                StringAssert.Contains(
                    "SetRef(x => x.strategy, null)", ctx.Source,
                    "Setting the field to None did not reach source as SetRef(..., null).\n" + ctx.Source);
                StringAssert.DoesNotContain(
                    "new Aggressive", ctx.Source,
                    "The stale pre-clear instance is still present in source.\n" + ctx.Source);
            });
    }

    // ---- Item 5: an Inspector nested-field edit patches the nested construction span only -----------

    [Test]
    public void Item5_InspectorEditNestedCompositePrimaryRange_SourceReflectsNestedValue()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        scene.Add(\"Host\").Component<AiBrain>(c => c.SetRef(x => x.strategy, "
                    + "new Composite { primary = new Aggressive { range = 5f }, fallback = new Flee { speed = 3f } }));\n",
            mutateScene: ctx =>
            {
                var brain = ctx.RequireRoot("Host").GetComponent<AiBrain>();
                var strategyProp = new SerializedObject(brain).FindProperty("strategy");
                strategyProp.FindPropertyRelative("primary").FindPropertyRelative("range").floatValue = 9f;
                strategyProp.serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(brain);
            },
            assertSource: ctx =>
            {
                StringAssert.Contains(
                    "range = 9f", ctx.Source,
                    "The nested field edit did not reach source as range = 9f.\n" + ctx.Source);
                StringAssert.Contains(
                    "speed = 3f", ctx.Source,
                    "The untouched sibling nested value (fallback) must survive the edit to primary.\n" + ctx.Source);
                StringAssert.DoesNotContain(
                    "range = 5f", ctx.Source,
                    "The stale pre-edit nested field value is still present in source.\n" + ctx.Source);
            });
    }

    // ---- Item 6: a second Materialize with no code change applies zero plan ops (live idempotence) --

    [Test]
    public void Item6_SecondMaterializeNoCodeChange_ZeroPlanOpsIdempotent()
    {
        var result = RoundTripProofHarness.ProveCodeToSceneFixedPoint(
            usings: "",
            body:
                "        scene.Add(\"Host\").Component<AiBrain>(c => c.SetRef(x => x.strategy, new Aggressive { range = 5f }));\n",
            assertScene: ctx =>
            {
                var brain = ctx.RequireRoot("Host").GetComponent<AiBrain>();
                var strategy = new SerializedObject(brain).FindProperty("strategy").managedReferenceValue as Aggressive;
                Assert.IsNotNull(strategy, "The live strategy field is not an Aggressive instance.");
                Assert.AreEqual(5f, strategy.range);
            },
            assertConverged: ctx =>
            {
                Assert.AreEqual(
                    0, ctx.Sync.PatchEdits,
                    "The convergence sync over an unchanged authored managed ref must apply zero patch edits.");
            });

        Assert.IsFalse(
            result.ConvergenceSync.Changed,
            "A second Materialize/Sync pass with no code change must be a no-op (zero plan ops, live idempotence).");
    }

    // ---- Item 7 (negative): a live managed ref whose recorded type resolves to no loadable C# type
    // surfaces a located conflict; the source is not patched and the field is not nulled -----------

    private const string Item7ScenePath = "Assets/GateTests/__ManagedReferenceRoundTrip_Item7.unity";

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(Item7ScenePath))
        {
            AssetDatabase.DeleteAsset(Item7ScenePath);
        }
    }

    [Test]
    public void Item7_MissingConcreteType_LocatedConflict_SourceUnchanged_FieldNotNulled()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb_mrrt_item7_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var builderPath = Path.Combine(dir, "ManagedReferenceRoundTripItem7Scene.cs");
        var sidecarPath = Path.Combine(dir, "ManagedReferenceRoundTripItem7Scene.sbmap.json");

        try
        {
            File.WriteAllText(builderPath, @"
using SceneBuilder.Authoring;
public class ManagedReferenceRoundTripItem7Scene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Item7Host"").Component<AiBrain>(c => c.SetRef(x => x.strategy, new Aggressive { range = 5f }));
    }
}");

            // Corrupt only the RECORDED ASSEMBLY of the managed-ref RefIds entry (not the class
            // name), which makes the type genuinely unresolvable while usually leaving the owning
            // MonoBehaviour itself intact on reload (SerializationUtility confirms the missing-type
            // state below). Unity's own deserializer is not deterministic about preserving the
            // owning component across this corruption -- it drops the whole MonoBehaviour on a
            // fraction of otherwise-identical attempts, independent of anything this test controls.
            // Retry the whole build-corrupt-reopen cycle, each with its own fresh scene and a
            // distinct bogus assembly name, until Unity reports the live precondition this item
            // exercises (the field missing its type, the component present).
            const int maxAttempts = 50;
            AiBrain brain = null;
            for (var attempt = 1; attempt <= maxAttempts && brain == null; attempt++)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var scene = EditorSceneManager.GetActiveScene();
                var build = SceneBuilderBuild.Run(builderPath, Item7ScenePath, sidecarPath, scene);
                Assert.IsEmpty(build.Diagnostics, "The seeding build was refused.");

                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), Item7ScenePath);

                var sceneText = File.ReadAllText(Item7ScenePath);
                StringAssert.Contains(
                    "asm: GateFixtures}", sceneText,
                    "Expected the materialized scene YAML to record the managed-ref's assembly.\n" + sceneText);
                // A distinct bogus assembly name every attempt: Unity's unresolvable-type handling
                // reuses whatever outcome it decided on the FIRST time a given (assembly, class)
                // pair proved unresolvable in THIS process, so retrying the identical ghost name
                // only ever replays the same result. A fresh name each attempt gives every retry an
                // independent trial.
                var ghostAsm = "GateFixturesGhost" + attempt;
                var corrupted = sceneText.Replace("asm: GateFixtures}", "asm: " + ghostAsm + "}");
                Assert.AreNotEqual(sceneText, corrupted, "The corruption string-replace matched nothing.");
                File.WriteAllText(Item7ScenePath, corrupted);
                AssetDatabase.Refresh();

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.OpenScene(Item7ScenePath, OpenSceneMode.Single);

                var host = GameObject.Find("Item7Host");
                var candidate = host != null ? host.GetComponent<AiBrain>() : null;
                if (candidate != null && UnityEditor.SerializationUtility.HasManagedReferencesWithMissingTypes(candidate))
                {
                    brain = candidate;
                }
            }

            Assert.IsNotNull(
                brain,
                $"Could not manufacture a live missing-type managed reference (component surviving) "
                    + $"after {maxAttempts} attempts -- EditMode was unable to reproduce the state this item exercises.");

            var beforeSync = File.ReadAllBytes(builderPath);
            var result = EmittedCodeCompiles.SyncAndAssertCompiles(builderPath, sidecarPath, EditorSceneManager.GetActiveScene());
            var afterSync = File.ReadAllBytes(builderPath);

            Assert.AreEqual(
                0, result.PatchEdits,
                "A missing-concrete-type managed ref must not be patched into source.");
            CollectionAssert.AreEqual(
                beforeSync, afterSync,
                "The builder source changed even though the missing-type field must not be patched.");

            var source = File.ReadAllText(builderPath);
            StringAssert.Contains(
                "new Aggressive", source,
                "The authored source must survive a live missing-type field unchanged.\n" + source);
            StringAssert.DoesNotContain(
                "SetRef(x => x.strategy, null)", source,
                "A missing-type field must not be silently nulled in source.\n" + source);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
