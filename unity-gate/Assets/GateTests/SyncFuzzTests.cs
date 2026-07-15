using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Parsing;
using GateFixtures;

// PROPERTY-BASED (FUZZ) SYNC HARNESS.
//
// Why this exists: every sync bug the user has actually hit was a COMBINATION bug —
// delete+components, move+add-component, reorder+declaration-order. Hand-written round-trip tests
// enumerate the cases their author thought of, and the spec and the tests share an author, so they
// share blind spots. This harness does not enumerate cases: it generates random sequences of REAL
// scene operations against a live EditMode scene and asserts INVARIANTS that must hold after every
// single one. It finds bugs nobody predicted.
//
// Determinism is mandatory (this is a gate, it cannot flake): every choice comes from a seeded
// System.Random, the gated test runs a FIXED curated seed list, and all generated values are
// quantized so float formatting is stable. A longer soak is available via SB_FUZZ_SOAK but is NOT
// gated.
//
// INVARIANTS asserted after every operation (these are the whole value of the file):
//   1. Sync does not throw.
//   2. The emitted source PARSES (BuilderParser.Parse) — the delete-cascade bug died here.
//   3. The emitted source COMPILES (reuses the product's BuilderCompileCheck via EmittedCodeCompiles).
//   4. CONVERGENCE: an immediately-following Sync with NO scene change applies ZERO edits. This is
//      the strongest invariant — any emission that does not round-trip fails it, whatever the cause.
//   5. No unexpected conflicts in the SyncResult.
//   6. IDENTITY PRESERVATION: Sync-then-Build destroys NOTHING that the operation itself left alive.
//      Invariants 1-5 are all structurally BLIND to the worst bug this harness has ever had to find:
//      when two same-named siblings swap identity, the emitted source still parses, still compiles,
//      and still converges — because it faithfully describes a wrong-but-self-consistent scene. The
//      damage only becomes visible when the code is built BACK into the scene and a real object is
//      destroyed and recreated in the wrong place. So this invariant closes the loop: it Builds, and
//      it checks that every live object/component that survived the operation is still the SAME
//      object (identity, not shape) afterwards.
//
// On ANY failure the harness prints a MINIMAL REPRO: the seed, the exact ordered operation sequence
// truncated at the failing step, and the offending emitted source. That repro is the deliverable.
public class SyncFuzzTests
{
    private const string ScenePath = "Assets/GateTests/__SyncFuzzTemp.unity";
    private const string FixturesDir = "Assets/GateTests/FuzzFixtures";
    private const string RedPath = FixturesDir + "/FuzzRed.mat";
    private const string BluePath = FixturesDir + "/FuzzBlue.mat";

    // The gated seed list. Fixed and curated: these exact seeds run on every gate invocation, so a
    // regression in any of them is a hard failure, not a probabilistic one.
    private static readonly int[] GatedSeeds =
        Enumerable.Range(1, 30).ToArray();

    private const int StepsPerSeed = 14;

    // NO QUARANTINE. Every gated seed must hold every invariant. The three defects this harness
    // originally found — reparent onto a handle-less parent throwing (seed 11), structural inserts
    // ignoring the target sibling index so sync never converged (seed 20), and move-to-root seating a
    // `var` below its own users (seed 17) — are fixed at their shared root: one statement-placement
    // path (SourcePatchApplier/StatementPlacement.cs) and one handle-introduction path
    // (Reconciler.ResolveOwnerHandle + SourcePatchApplier.ResolveHandleIntroductions). Their seeds
    // are now ordinary passing seeds and guard the fix.
    //
    // If a future defect makes a seed fail: fix it, or quarantine it EXPLICITLY and loudly here —
    // but never by loosening verify.sh, which gates on result="Passed" AND failed=0 precisely so an
    // ignored test cannot pass the gate in silence.

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;
    private bool _prevIgnoreFailingMessages;

    // The starting builder: a deliberate mix of handled and handle-less objects, roots and a child,
    // because the handle/handle-less seam is where the emission bugs live.
    private const string InitialBody =
        "        var alpha = scene.Add(\"Alpha\");\n" +
        "        scene.Add(\"Beta\");\n" +
        "        var gamma = scene.Add(\"Gamma\");\n" +
        "        gamma.Add(\"Delta\");";

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class FuzzScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_fuzz_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "FuzzScene.cs");
        _sidecarPath = Path.Combine(_dir, "FuzzScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "FuzzFixtures");
        }

        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), RedPath);
        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), BluePath);
        AssetDatabase.SaveAssets();

        // The product logs conflicts/compile errors to the Console; the Unity runner would fail the
        // test on a logged error BEFORE our assertion runs, which would replace the minimal repro
        // with an unhelpful log dump. We assert compilability and conflicts EXPLICITLY below, so the
        // assertion — not the log — is this file's reporting surface.
        _prevIgnoreFailingMessages = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
    }

    [TearDown]
    public void TearDown()
    {
        LogAssert.ignoreFailingMessages = _prevIgnoreFailingMessages;

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }

        AssetDatabase.DeleteAsset(RedPath);
        AssetDatabase.DeleteAsset(BluePath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }
    }

    // THE GATE. One case per curated seed; each runs a random op sequence and checks every invariant
    // after every step.
    [Test]
    public void Fuzz_RandomOperationSequence_HoldsSyncInvariants([ValueSource(nameof(GatedSeeds))] int seed)
    {
        RunSeed(seed, StepsPerSeed);
    }

    // Non-gated soak. `SB_FUZZ_SOAK=200 SB_FUZZ_STEPS=40 ./verify.sh` widens the search far past the
    // gated list without making the gate slower or flakier.
    //
    // With no SB_FUZZ_SOAK this runs ZERO seeds and passes — it does not Assert.Ignore. An Ignore
    // here would be indistinguishable, to NUnit and to verify.sh, from a quarantined bug: it drags
    // the whole run's result to "Skipped:Ignored", which is precisely the signal the strict gate uses
    // to catch a silently-skipped test. This is an opt-in extended run, not a suppressed failure, so
    // it must not spend that signal.
    [Test]
    public void Fuzz_Soak_NotGated()
    {
        var soak = Environment.GetEnvironmentVariable("SB_FUZZ_SOAK");
        var seedCount = 0;
        if (string.IsNullOrEmpty(soak) || !int.TryParse(soak, out seedCount) || seedCount <= 0)
        {
            TestContext.WriteLine(
                "Soak disabled (0 seeds run). Set SB_FUZZ_SOAK=<seedCount> (optionally SB_FUZZ_STEPS=<steps>) to enable.");
            return;
        }

        var steps = StepsPerSeed;
        var stepsEnv = Environment.GetEnvironmentVariable("SB_FUZZ_STEPS");
        if (!string.IsNullOrEmpty(stepsEnv) && int.TryParse(stepsEnv, out var parsedSteps) && parsedSteps > 0)
        {
            steps = parsedSteps;
        }

        for (var seed = 1; seed <= seedCount; seed++)
        {
            SetUpSoakIteration();
            RunSeed(seed, steps);
        }
    }

    // The soak reuses one [SetUp]'d fixture across iterations; each iteration needs a fresh
    // builder/sidecar pair so seeds do not contaminate one another.
    private void SetUpSoakIteration()
    {
        _builderPath = Path.Combine(_dir, "FuzzScene_" + Guid.NewGuid().ToString("N") + ".cs");
        _sidecarPath = Path.Combine(_dir, "FuzzScene_" + Guid.NewGuid().ToString("N") + ".sbmap.json");
    }

    // ---------------------------------------------------------------------------------------------
    // The fuzz loop
    // ---------------------------------------------------------------------------------------------

    private void RunSeed(int seed, int steps)
    {
        var rng = new System.Random(seed);
        var log = new List<string>();
        var nameCounter = 0;

        File.WriteAllText(_builderPath, Source(InitialBody));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        log.Add("BUILD initial: " + InitialBody.Replace("\n", " | ").Trim());

        // The initial build itself must leave a converged, compiling file — check before step 1 so a
        // failure here is never misattributed to a generated operation.
        AssertInvariants(seed, log, "<initial build>", LiveIdentities());

        for (var step = 0; step < steps; step++)
        {
            var description = ApplyRandomOperation(rng, ref nameCounter);
            if (description == null)
            {
                continue; // no legal target for the drawn op this step; not an error
            }

            log.Add($"step {step}: {description}");

            // Snapshot identity AFTER the operation: whatever the operation itself destroyed (a
            // delete, a component removal) is legitimately gone, and everything still standing is
            // what Sync-then-Build must not touch. Taking it here rather than before the operation
            // means invariant 6 needs no per-op knowledge of what was "supposed" to die.
            AssertInvariants(seed, log, description, LiveIdentities());
        }
    }

    // Every live GameObject and Component, keyed by its Unity entity id — the identity that survives
    // a rename/reorder/reparent and does NOT survive a destroy+recreate. That distinction is the
    // whole point: a wrong-but-self-consistent scene looks identical by NAME and by SHAPE, and
    // differs only here.
    //
    // Keyed by UnityEngine.EntityId ITSELF (it is IEquatable + IComparable, so it is a valid key).
    // Not by int: GetInstanceID() no longer exists in 6000.5.3f1, and EntityId's implicit int
    // conversion is [Obsolete]-as-error here because an EntityId will not fit an int in future
    // versions.
    private static Dictionary<EntityId, string> LiveIdentities()
    {
        var result = new Dictionary<EntityId, string>();
        foreach (var go in AllObjects())
        {
            if (go == null)
            {
                continue;
            }

            var path = PathOf(go);
            result[go.GetEntityId()] = $"GameObject \"{path}\"";

            foreach (var component in go.GetComponents<Component>())
            {
                if (component != null)
                {
                    result[component.GetEntityId()] = $"{component.GetType().Name} on \"{path}\"";
                }
            }
        }

        return result;
    }

    // Asserts every invariant against the CURRENT scene, reporting a minimal repro on failure.
    // `survivedOp` is the identity snapshot taken right after the generated operation.
    private void AssertInvariants(int seed, List<string> log, string lastOp, Dictionary<EntityId, string> survivedOp)
    {
        var scene = EditorSceneManager.GetActiveScene();

        // INVARIANT 1: Sync does not throw.
        SceneBuilderSync.SyncResult first;
        try
        {
            first = SceneBuilderSync.Run(_builderPath, _sidecarPath, scene);
        }
        catch (Exception e)
        {
            Assert.Fail(Repro(seed, log, lastOp, "INVARIANT 1 VIOLATED — Sync THREW.\n" + e));
            return;
        }

        var emitted = File.ReadAllText(_builderPath);

        // INVARIANT 2: the emitted source PARSES.
        try
        {
            BuilderParser.Parse(emitted);
        }
        catch (Exception e)
        {
            Assert.Fail(Repro(seed, log, lastOp,
                "INVARIANT 2 VIOLATED — emitted source does NOT PARSE (BuilderParser.Parse threw).\n" + e,
                emitted));
            return;
        }

        // INVARIANT 3: the emitted source COMPILES. Reuses the product's own check.
        var compileErrors = BuilderCompileCheck.Check(emitted);
        if (compileErrors.Count > 0)
        {
            Assert.Fail(Repro(seed, log, lastOp,
                "INVARIANT 3 VIOLATED — emitted source does NOT COMPILE.\n" +
                BuilderCompileCheck.Format(compileErrors, "fuzz emission", emitted)));
            return;
        }

        // INVARIANT 5 (first sync): no unexpected conflicts.
        if (first.Conflicts.Length > 0)
        {
            Assert.Fail(Repro(seed, log, lastOp,
                "INVARIANT 5 VIOLATED — Sync reported conflict(s):\n" + FormatConflicts(first.Conflicts),
                emitted));
            return;
        }

        // INVARIANT 4: CONVERGENCE. A re-sync with NO scene change must be a TOTAL no-op.
        //
        // This used to assert ONLY on EditsApplied, which is set exclusively inside `if (newSource !=
        // source)` — so a reconcile that wrongly decided the source needed patching, but happened to
        // re-emit byte-identical text, scored zero and sailed through. That is exactly how the
        // unlowered-asset-ref bug hid here for so long while this fuzzer ran green every seed. The
        // assertions below name the defect at its source instead of at its (lucky) symptom.
        var sidecarBefore = File.ReadAllBytes(_sidecarPath);
        var sourceStampBefore = File.GetLastWriteTimeUtc(_builderPath);
        var sidecarStampBefore = File.GetLastWriteTimeUtc(_sidecarPath);

        SceneBuilderSync.SyncResult second;
        try
        {
            second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        }
        catch (Exception e)
        {
            Assert.Fail(Repro(seed, log, lastOp,
                "INVARIANT 4 VIOLATED — the CONVERGENCE re-sync (no scene change) THREW.\n" + e,
                emitted));
            return;
        }

        // 4a. The reconcile must not have produced an edit AT ALL. Independent of whether the emitted
        //     text happened to match, a no-op re-sync that produces edits has not converged.
        if (second.PatchEdits != 0)
        {
            Assert.Fail(Repro(seed, log, lastOp,
                $"INVARIANT 4 VIOLATED — NOT CONVERGED: a re-sync with NO scene change produced " +
                $"{second.PatchEdits} patch edit(s) (EditsApplied={second.EditsApplied}). The reconcile " +
                "believes the source is out of date when it is not — even if the re-emitted text happens " +
                "to match byte-for-byte, one formatting divergence turns this into a perpetual rewrite.\n" +
                "---- source AFTER the convergence re-sync ----\n" + File.ReadAllText(_builderPath),
                emitted));
            return;
        }

        // 4b. No edits may be APPLIED.
        if (second.EditsApplied != 0)
        {
            Assert.Fail(Repro(seed, log, lastOp,
                $"INVARIANT 4 VIOLATED — NOT CONVERGED: a re-sync with NO scene change applied " +
                $"{second.EditsApplied} edit(s). The first sync's emission does not round-trip.\n" +
                "---- source AFTER the convergence re-sync ----\n" + File.ReadAllText(_builderPath),
                emitted));
            return;
        }

        // 4c. The sync must REPORT no change. Code->scene is driven by the plugin's own file watcher,
        //     so this bit is load-bearing: a sync that always claims Changed is a watcher that always
        //     fires.
        if (second.Changed)
        {
            Assert.Fail(Repro(seed, log, lastOp,
                "INVARIANT 4 VIOLATED — NOT CONVERGED: a re-sync with NO scene change reported " +
                "Changed=true despite applying zero edits.",
                emitted));
            return;
        }

        // 4d. No sidecar entry churn.
        if (second.AddedEntries != 0 || second.RemovedEntries != 0)
        {
            Assert.Fail(Repro(seed, log, lastOp,
                $"INVARIANT 4 VIOLATED — NOT CONVERGED: a re-sync with NO scene change churned the " +
                $"sidecar (+{second.AddedEntries} / -{second.RemovedEntries} entr(ies)).",
                emitted));
            return;
        }

        // 4e. ZERO WRITES actually occurred. Byte-equality alone is satisfied by a CHURNING write —
        //     the sidecar was rewritten with identical content on every single sync and no byte check
        //     could ever have noticed. The mtime is what catches it, so both are asserted.
        if (File.GetLastWriteTimeUtc(_builderPath) != sourceStampBefore)
        {
            Assert.Fail(Repro(seed, log, lastOp,
                "INVARIANT 4 VIOLATED — a re-sync with NO scene change WROTE the builder source " +
                "(mtime moved) despite applying zero edits.",
                emitted));
            return;
        }

        if (File.GetLastWriteTimeUtc(_sidecarPath) != sidecarStampBefore)
        {
            Assert.Fail(Repro(seed, log, lastOp,
                "INVARIANT 4 VIOLATED — a re-sync with NO scene change WROTE the sidecar (mtime moved). " +
                "Identical bytes are not enough: the write itself fires the file watcher.",
                emitted));
            return;
        }

        if (!File.ReadAllBytes(_sidecarPath).SequenceEqual(sidecarBefore))
        {
            Assert.Fail(Repro(seed, log, lastOp,
                "INVARIANT 4 VIOLATED — a re-sync with NO scene change altered the sidecar CONTENT.",
                emitted));
            return;
        }

        if (second.Conflicts.Length > 0)
        {
            Assert.Fail(Repro(seed, log, lastOp,
                "INVARIANT 5 VIOLATED — the convergence re-sync reported conflict(s):\n" +
                FormatConflicts(second.Conflicts),
                emitted));
            return;
        }

        // INVARIANT 6: IDENTITY PRESERVATION.
        //
        // Everything above this line is satisfied by a scene that is WRONG but self-consistent —
        // which is exactly what a duplicate-name identity swap produces. Closing the loop is the only
        // way to see it: build the emitted code BACK into the scene and check that every object and
        // component the operation left alive is still the SAME instance. Build is
        // reconcile-into-existing (§5), and the code was just synced FROM this scene, so a correct
        // round-trip destroys nothing at all — any lost entity id is a real object that the tool
        // destroyed and (usually) recreated somewhere else, which is the user's data.
        try
        {
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        }
        catch (Exception e)
        {
            Assert.Fail(Repro(seed, log, lastOp,
                "INVARIANT 6 VIOLATED — Build (code->scene) of the just-synced source THREW.\n" + e,
                emitted));
            return;
        }

        var afterBuild = LiveIdentities();
        var destroyed = survivedOp.Where(kv => !afterBuild.ContainsKey(kv.Key)).ToList();
        if (destroyed.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                $"INVARIANT 6 VIOLATED — Sync-then-Build DESTROYED {destroyed.Count} live object(s)/component(s) " +
                "that the operation itself left alive. The emitted source parses, compiles and converges — it " +
                "just describes the WRONG objects, so the round-trip silently destroyed the user's data:");
            foreach (var (entityId, description) in destroyed)
            {
                sb.AppendLine($"    LOST [{entityId}] {description}");
            }

            Assert.Fail(Repro(seed, log, lastOp, sb.ToString(), emitted));
        }
    }

    private static string FormatConflicts(IEnumerable<SceneBuilder.Core.Reconcile.Conflict> conflicts)
    {
        var sb = new StringBuilder();
        foreach (var c in conflicts)
        {
            sb.AppendLine($"  [{c.Kind}] {c.LogicalId}: {c.Reason}");
        }

        return sb.ToString();
    }

    // THE DELIVERABLE: a minimal, self-contained repro. Seed + the exact ordered op sequence
    // truncated at the failing step + the offending emitted source.
    private string Repro(int seed, List<string> log, string lastOp, string failure, string emitted = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("================ FUZZ FAILURE — MINIMAL REPRO ================");
        sb.AppendLine($"SEED: {seed}");
        sb.AppendLine($"FAILING OPERATION: {lastOp}");
        sb.AppendLine();
        sb.AppendLine("OPERATION SEQUENCE (in order, truncated at the failing step):");
        foreach (var entry in log)
        {
            sb.AppendLine("  " + entry);
        }

        sb.AppendLine();
        sb.AppendLine("FAILURE:");
        sb.AppendLine(failure);

        if (emitted != null)
        {
            sb.AppendLine();
            sb.AppendLine("---- OFFENDING EMITTED SOURCE ----");
            sb.AppendLine(emitted);
        }

        sb.AppendLine("=============================================================");
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------
    // Operation generator — samples randomly from LIVE scene state
    // ---------------------------------------------------------------------------------------------

    private static readonly string[] BuiltinTags = { "Untagged", "Player", "MainCamera", "Respawn", "Finish" };
    private static readonly int[] BuiltinLayers = { 0, 1, 2, 4, 5 };

    // Returns a human-readable description of what it did, or null when the drawn op had no legal
    // target in the current scene (a no-op step, not an error).
    private string ApplyRandomOperation(System.Random rng, ref int nameCounter)
    {
        var all = AllObjects();
        var op = rng.Next(12);

        switch (op)
        {
            case 0: return CreateRoot(rng, ref nameCounter);
            case 1: return CreateChild(rng, all, ref nameCounter);
            case 2: return DeleteObject(rng, all);
            case 3: return RenameObject(rng, all, ref nameCounter);
            case 4: return ChangeTransform(rng, all);
            case 5: return Reparent(rng, all);
            case 6: return SetFlag(rng, all);
            case 7: return AddComponent(rng, all);
            case 8: return RemoveComponent(rng, all);
            case 9: return SetComponentField(rng, all);
            case 10: return MaterialOp(rng, all);
            case 11: return ReorderSibling(rng, all);
            default: return null;
        }
    }

    // Names of the objects that would be SIBLINGS of something parented to `parent` (null = scene
    // root). The source of duplicate-name collisions.
    private static List<string> SiblingNames(Transform parent)
    {
        if (parent == null)
        {
            return EditorSceneManager.GetActiveScene().GetRootGameObjects().Select(go => go.name).ToList();
        }

        var names = new List<string>();
        for (var i = 0; i < parent.childCount; i++)
        {
            names.Add(parent.GetChild(i).name);
        }

        return names;
    }

    // THE name generator. Roughly 1 in 4, reuse an EXISTING sibling's name instead of minting a
    // fresh one.
    //
    // Why this exists: every name used to come from a monotonic `nameCounter` ("Fuzz"+n), so two
    // siblings could NEVER share a name — the duplicate-sibling-name hazard was impossible BY
    // CONSTRUCTION, and this harness ran 30 seeds x 14 steps of green over a defect that silently
    // destroys real components. A fuzzer that cannot generate a state cannot find its bugs.
    private static string NameFor(System.Random rng, Transform parent, ref int nameCounter)
    {
        if (rng.Next(4) == 0)
        {
            var siblings = SiblingNames(parent);
            if (siblings.Count > 0)
            {
                return Pick(rng, siblings);
            }
        }

        return "Fuzz" + nameCounter++;
    }

    // Sibling count of the level `go` lives on (its parent's children, or the scene's roots).
    private static int SiblingCount(GameObject go) =>
        go.transform.parent == null
            ? EditorSceneManager.GetActiveScene().rootCount
            : go.transform.parent.childCount;

    // PURE REORDER — the operation that turns the duplicate-name defect from latent into
    // destructive, and which this harness could not perform AT ALL: there was no reorder op
    // (`rng.Next(11)`), SetSiblingIndex was never called, and Reparent explicitly skips same-parent
    // moves. A reorder creates and destroys nothing, so ANY object it loses is a bug.
    private string ReorderSibling(System.Random rng, List<GameObject> all)
    {
        var candidates = all.Where(go => go != null && SiblingCount(go) > 1).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var go = Pick(rng, candidates);
        var path = PathOf(go);
        var oldIndex = go.transform.GetSiblingIndex();
        var newIndex = rng.Next(SiblingCount(go));
        if (newIndex == oldIndex)
        {
            return null;
        }

        go.transform.SetSiblingIndex(newIndex);
        return $"reorder \"{path}\" sibling index {oldIndex} -> {newIndex}";
    }

    private static List<GameObject> AllObjects()
    {
        var result = new List<GameObject>();
        foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
        {
            Collect(root, result);
        }

        return result;
    }

    private static void Collect(GameObject go, List<GameObject> into)
    {
        into.Add(go);
        for (var i = 0; i < go.transform.childCount; i++)
        {
            Collect(go.transform.GetChild(i).gameObject, into);
        }
    }

    private static T Pick<T>(System.Random rng, IReadOnlyList<T> items) =>
        items.Count == 0 ? default : items[rng.Next(items.Count)];

    // Deterministic, formatting-stable values: quantized to quarters so the emitted float literal
    // text is identical for a given seed on every run.
    private static float Quantized(System.Random rng, float min, float max)
    {
        var steps = (int)((max - min) * 4f);
        return min + rng.Next(steps + 1) / 4f;
    }

    private string CreateRoot(System.Random rng, ref int nameCounter)
    {
        var name = NameFor(rng, null, ref nameCounter);
        var go = new GameObject(name);
        go.transform.SetParent(null);
        return $"create root GameObject \"{name}\"";
    }

    private string CreateChild(System.Random rng, List<GameObject> all, ref int nameCounter)
    {
        var parent = Pick(rng, all);
        if (parent == null)
        {
            return CreateRoot(rng, ref nameCounter);
        }

        var name = NameFor(rng, parent.transform, ref nameCounter);
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform);
        return $"create GameObject \"{name}\" as child of \"{PathOf(parent)}\"";
    }

    private string DeleteObject(System.Random rng, List<GameObject> all)
    {
        // Never empty the scene — an empty scene stops generating interesting operations.
        if (all.Count <= 1)
        {
            return null;
        }

        var victim = Pick(rng, all);
        var path = PathOf(victim);
        UnityEngine.Object.DestroyImmediate(victim);
        return $"delete GameObject \"{path}\"";
    }

    private string RenameObject(System.Random rng, List<GameObject> all, ref int nameCounter)
    {
        var go = Pick(rng, all);
        if (go == null)
        {
            return null;
        }

        var old = PathOf(go);

        // ~1 in 4, rename ONTO a sibling's name — a rename into a collision creates the ambiguous
        // pair with no create involved at all, so a fix that only guards the append path misses it.
        var name = "Ren" + nameCounter++;
        if (rng.Next(4) == 0)
        {
            var siblings = SiblingNames(go.transform.parent).Where(n => n != go.name).ToList();
            if (siblings.Count > 0)
            {
                name = Pick(rng, siblings);
            }
        }

        go.name = name;
        return $"rename \"{old}\" -> \"{name}\"";
    }

    private string ChangeTransform(System.Random rng, List<GameObject> all)
    {
        var go = Pick(rng, all);
        if (go == null)
        {
            return null;
        }

        var which = rng.Next(3);
        var path = PathOf(go);
        switch (which)
        {
            case 0:
                var p = new Vector3(Quantized(rng, -5f, 5f), Quantized(rng, -5f, 5f), Quantized(rng, -5f, 5f));
                go.transform.localPosition = p;
                return $"set \"{path}\".localPosition = {Fmt(p)}";
            case 1:
                var e = new Vector3(Quantized(rng, 0f, 90f), Quantized(rng, 0f, 90f), Quantized(rng, 0f, 90f));
                go.transform.localEulerAngles = e;
                return $"set \"{path}\".localEulerAngles = {Fmt(e)}";
            default:
                var s = new Vector3(Quantized(rng, 0.25f, 4f), Quantized(rng, 0.25f, 4f), Quantized(rng, 0.25f, 4f));
                go.transform.localScale = s;
                return $"set \"{path}\".localScale = {Fmt(s)}";
        }
    }

    private string Reparent(System.Random rng, List<GameObject> all)
    {
        if (all.Count < 2)
        {
            return null;
        }

        var child = Pick(rng, all);
        var path = PathOf(child);

        // To root (1 in 3), else under a random object that is neither itself nor one of its own
        // descendants (which would be an illegal cycle, not a bug we are hunting).
        if (rng.Next(3) == 0)
        {
            if (child.transform.parent == null)
            {
                return null;
            }

            child.transform.SetParent(null);
            return $"reparent \"{path}\" -> scene root";
        }

        var candidates = all
            .Where(go => go != null && go != child && !IsDescendantOf(go.transform, child.transform))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var parent = Pick(rng, candidates);
        if (child.transform.parent == parent.transform)
        {
            return null;
        }

        child.transform.SetParent(parent.transform);
        return $"reparent \"{path}\" -> under \"{PathOf(parent)}\"";
    }

    private static bool IsDescendantOf(Transform candidate, Transform ancestor)
    {
        for (var t = candidate; t != null; t = t.parent)
        {
            if (t == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    private string SetFlag(System.Random rng, List<GameObject> all)
    {
        var go = Pick(rng, all);
        if (go == null)
        {
            return null;
        }

        var path = PathOf(go);
        switch (rng.Next(4))
        {
            case 0:
                var tag = Pick(rng, BuiltinTags);
                go.tag = tag;
                return $"set \"{path}\".tag = \"{tag}\"";
            case 1:
                var layer = Pick(rng, BuiltinLayers);
                go.layer = layer;
                return $"set \"{path}\".layer = {layer}";
            case 2:
                var active = rng.Next(2) == 0;
                go.SetActive(active);
                return $"set \"{path}\".SetActive({active.ToString().ToLowerInvariant()})";
            default:
                var isStatic = rng.Next(2) == 0;
                go.isStatic = isStatic;
                return $"set \"{path}\".isStatic = {isStatic.ToString().ToLowerInvariant()}";
        }
    }

    private string AddComponent(System.Random rng, List<GameObject> all)
    {
        var go = Pick(rng, all);
        if (go == null)
        {
            return null;
        }

        var path = PathOf(go);
        switch (rng.Next(4))
        {
            case 0:
                // DisallowMultipleComponent — adding a second would throw in the EDITOR, which is a
                // harness bug, not a sync bug.
                if (go.GetComponent<Rigidbody>() != null)
                {
                    return null;
                }

                go.AddComponent<Rigidbody>();
                return $"add component Rigidbody to \"{path}\"";
            case 1:
                // BoxCollider permits multiples — deliberately allowed, so same-type component
                // identity/index matching gets exercised.
                go.AddComponent<BoxCollider>();
                return $"add component BoxCollider to \"{path}\"";
            case 2:
                go.AddComponent<GateSampleBehaviour>();
                return $"add component GateFixtures.GateSampleBehaviour to \"{path}\"";
            default:
                if (go.GetComponent<MeshRenderer>() != null)
                {
                    return null;
                }

                go.AddComponent<MeshRenderer>();
                return $"add component MeshRenderer to \"{path}\"";
        }
    }

    // Only components this harness itself adds are removal candidates — never Transform, which
    // cannot be destroyed.
    private static List<Component> RemovableComponents(GameObject go) =>
        go.GetComponents<Component>()
            .Where(c => c is Rigidbody || c is BoxCollider || c is GateSampleBehaviour || c is MeshRenderer)
            .ToList();

    private string RemoveComponent(System.Random rng, List<GameObject> all)
    {
        var withComponents = all.Where(go => go != null && RemovableComponents(go).Count > 0).ToList();
        if (withComponents.Count == 0)
        {
            return null;
        }

        var go = Pick(rng, withComponents);
        var components = RemovableComponents(go);
        var victim = Pick(rng, components);
        var type = victim.GetType().Name;
        var path = PathOf(go);
        UnityEngine.Object.DestroyImmediate(victim);
        return $"remove component {type} from \"{path}\"";
    }

    private string SetComponentField(System.Random rng, List<GameObject> all)
    {
        var rigidbodies = all.Where(go => go != null && go.GetComponent<Rigidbody>() != null).ToList();
        var behaviours = all.Where(go => go != null && go.GetComponent<GateSampleBehaviour>() != null).ToList();

        var useRigidbody = rigidbodies.Count > 0 && (behaviours.Count == 0 || rng.Next(2) == 0);
        if (useRigidbody)
        {
            var go = Pick(rng, rigidbodies);
            var mass = Quantized(rng, 0.25f, 10f);
            go.GetComponent<Rigidbody>().mass = mass;
            return $"set \"{PathOf(go)}\".Rigidbody.mass = {mass.ToString(CultureInfo.InvariantCulture)}";
        }

        if (behaviours.Count > 0)
        {
            var go = Pick(rng, behaviours);
            var health = rng.Next(1, 100);
            go.GetComponent<GateSampleBehaviour>().Health = health;
            return $"set \"{PathOf(go)}\".GateSampleBehaviour.Health = {health}";
        }

        return null;
    }

    private string MaterialOp(System.Random rng, List<GameObject> all)
    {
        var renderers = all.Where(go => go != null && go.GetComponent<MeshRenderer>() != null).ToList();
        if (renderers.Count == 0)
        {
            return null;
        }

        var go = Pick(rng, renderers);
        var mr = go.GetComponent<MeshRenderer>();
        var path = PathOf(go);

        switch (rng.Next(3))
        {
            case 0:
                mr.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(RedPath);
                return $"assign material FuzzRed.mat on \"{path}\"";
            case 1:
                mr.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(BluePath);
                return $"assign material FuzzBlue.mat on \"{path}\"";
            default:
                mr.sharedMaterial = null;
                return $"clear material on \"{path}\"";
        }
    }

    private static string Fmt(Vector3 v) =>
        string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2})", v.x, v.y, v.z);

    // A stable, readable identifier for the repro log: the object's full scene path.
    private static string PathOf(GameObject go)
    {
        if (go == null)
        {
            return "<null>";
        }

        var parts = new List<string>();
        for (var t = go.transform; t != null; t = t.parent)
        {
            parts.Add(t.name);
        }

        parts.Reverse();
        return string.Join("/", parts);
    }
}
