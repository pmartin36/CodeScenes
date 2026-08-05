using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// A round-trip PROOF drives one direction end-to-end -- write source or mutate the live scene, the
// caller's own assertion, then a convergence check -- as a single call, so a proof cannot assert a
// round trip while skipping the check that the result is a fixed point.
public static class RoundTripProofHarness
{
    // Passed to every caller callback. Scene and Source are read fresh on every access, since a
    // scene captured before a save is stale and the builder text changes under the callback.
    public sealed class ProofContext
    {
        public string BuilderPath { get; }
        public string SidecarPath { get; }
        public string ScenePath { get; }
        public Scene Scene => EditorSceneManager.GetActiveScene();
        public string Source => File.ReadAllText(BuilderPath);
        public SceneBuilderBuild.BuildResult Build { get; }
        public SceneBuilderSync.SyncResult Sync { get; }

        internal ProofContext(
            string builderPath, string sidecarPath, string scenePath,
            SceneBuilderBuild.BuildResult build, SceneBuilderSync.SyncResult sync)
        {
            BuilderPath = builderPath;
            SidecarPath = sidecarPath;
            ScenePath = scenePath;
            Build = build;
            Sync = sync;
        }

        public GameObject Root(string name)
        {
            foreach (var go in Scene.GetRootGameObjects())
            {
                if (go.name == name)
                {
                    return go;
                }
            }

            return null;
        }

        public GameObject RequireRoot(string name)
        {
            var go = Root(name);
            Assert.IsNotNull(go, $"Root GameObject '{name}' was not found in the scene.");
            return go;
        }
    }

    public sealed class ProofResult
    {
        public SceneBuilderBuild.BuildResult Build { get; }
        public SceneBuilderSync.SyncResult DriveSync { get; }
        public SceneBuilderSync.SyncResult ConvergenceSync { get; }
        public string ConvergedSource { get; }
        public string ConvergedSidecar { get; }

        internal ProofResult(
            SceneBuilderBuild.BuildResult build, SceneBuilderSync.SyncResult driveSync,
            SceneBuilderSync.SyncResult convergenceSync, string convergedSource, string convergedSidecar)
        {
            Build = build;
            DriveSync = driveSync;
            ConvergenceSync = convergenceSync;
            ConvergedSource = convergedSource;
            ConvergedSidecar = convergedSidecar;
        }
    }

    private static string Source(string usings, string body) => $@"{usings}
using UnityEngine;
using SceneBuilder.Authoring;
public class RoundTripProofScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private readonly struct Fixture
    {
        public readonly string Dir;
        public readonly string BuilderPath;
        public readonly string SidecarPath;
        public readonly string ScenePath;

        public Fixture(string dir, string builderPath, string sidecarPath, string scenePath)
        {
            Dir = dir;
            BuilderPath = builderPath;
            SidecarPath = sidecarPath;
            ScenePath = scenePath;
        }
    }

    private static Fixture NewFixture()
    {
        var guid = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "sb_rtproof_" + guid);
        Directory.CreateDirectory(dir);
        return new Fixture(
            dir,
            Path.Combine(dir, "RoundTripProofScene.cs"),
            Path.Combine(dir, "RoundTripProofScene.sbmap.json"),
            "Assets/GateTests/__RoundTripProof_" + guid + ".unity");
    }

    private static void Cleanup(Fixture fixture)
    {
        if (File.Exists(fixture.ScenePath))
        {
            AssetDatabase.DeleteAsset(fixture.ScenePath);
        }

        if (Directory.Exists(fixture.Dir))
        {
            Directory.Delete(fixture.Dir, true);
        }
    }

    private static SceneBuilderBuild.BuildResult SeedScene(Fixture fixture, string usings, string body)
    {
        File.WriteAllText(fixture.BuilderPath, Source(usings, body));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var build = SceneBuilderBuild.Run(fixture.BuilderPath, fixture.ScenePath, fixture.SidecarPath, scene);
        Assert.IsEmpty(
            build.Diagnostics,
            "A REFUSED build mutates nothing; a proof cannot start from a scene that was never built.\n"
                + string.Join("\n", build.Diagnostics.Select(d => d.Message)));
        Assert.Greater(
            build.PlanOpCount, 0,
            "The seeding build into an empty scene applied zero plan ops -- it built nothing to prove against.");
        return build;
    }

    private static SceneBuilderSync.SyncResult ConvergeToFixedPoint(Fixture fixture, string context)
    {
        EmittedCodeCompiles.SyncAndAssertCompiles(fixture.BuilderPath, fixture.SidecarPath, EditorSceneManager.GetActiveScene());
        var beforeSecond = File.ReadAllBytes(fixture.BuilderPath);
        var second = EmittedCodeCompiles.SyncAndAssertCompiles(fixture.BuilderPath, fixture.SidecarPath, EditorSceneManager.GetActiveScene());
        var afterSecond = File.ReadAllBytes(fixture.BuilderPath);

        Assert.AreEqual(
            0, second.PatchEdits,
            context + ": the second sync produced " + second.PatchEdits + " patch edit(s); it must be a fixed point.");
        Assert.IsFalse(second.Changed, context + ": the second sync reported Changed even though it must be a no-op.");
        CollectionAssert.AreEqual(
            beforeSecond, afterSecond,
            context + ": the builder source was not byte-identical across the second sync.");
        return second;
    }

    public static ProofResult ProveCodeToScene(string usings, string body, Action<ProofContext> assertScene)
    {
        var fixture = NewFixture();
        try
        {
            var build = SeedScene(fixture, usings, body);

            assertScene(new ProofContext(fixture.BuilderPath, fixture.SidecarPath, fixture.ScenePath, build, null));

            var convergenceSync = ConvergeToFixedPoint(fixture, "code->scene convergence");

            return new ProofResult(
                build,
                driveSync: null,
                convergenceSync: convergenceSync,
                convergedSource: File.ReadAllText(fixture.BuilderPath),
                convergedSidecar: File.ReadAllText(fixture.SidecarPath));
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    // Proves a code->scene edit made to an ALREADY-CONVERGED source: seed, converge, hand the
    // converged text to `reauthor`, rebuild from the edited text, let the caller assert the live
    // scene, then converge again. This is the shape a bare "assign, build, assert" proof cannot
    // express -- the rebuild must apply a nonzero plan op count, so a `reauthor` callback that
    // matched nothing (and therefore proved nothing) fails loud instead of passing as a no-op.
    public static ProofResult ProveReauthoredCodeToScene(
        string usings, string body, Func<string, string> reauthor, Action<ProofContext> assertScene)
    {
        var fixture = NewFixture();
        try
        {
            SeedScene(fixture, usings, body);
            ConvergeToFixedPoint(fixture, "re-authored code->scene baseline (before the edit)");

            var converged = File.ReadAllText(fixture.BuilderPath);
            var edited = reauthor(converged);
            Assert.AreNotEqual(
                converged, edited,
                "The reauthor callback did not change the converged source -- it matched nothing, so the "
                    + "case proves nothing.");
            File.WriteAllText(fixture.BuilderPath, edited);

            var rebuild = SceneBuilderBuild.Run(
                fixture.BuilderPath, fixture.ScenePath, fixture.SidecarPath, EditorSceneManager.GetActiveScene());
            Assert.IsEmpty(
                rebuild.Diagnostics,
                "The re-authored build was refused.\n" + string.Join("\n", rebuild.Diagnostics.Select(d => d.Message)));
            Assert.Greater(
                rebuild.PlanOpCount, 0,
                "The re-authored build applied zero plan ops -- the edit reached nothing.");

            assertScene(new ProofContext(fixture.BuilderPath, fixture.SidecarPath, fixture.ScenePath, rebuild, null));

            var convergenceSync = ConvergeToFixedPoint(fixture, "re-authored code->scene convergence");

            return new ProofResult(
                rebuild,
                driveSync: null,
                convergenceSync: convergenceSync,
                convergedSource: File.ReadAllText(fixture.BuilderPath),
                convergedSidecar: File.ReadAllText(fixture.SidecarPath));
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    // Proves a code->scene source is a BUILD fixed point whose author's file survives the first
    // sync: seed, let the caller assert the built scene, rebuild twice more (each must apply ZERO
    // plan ops and must not be refused), then sync once -- which must apply zero patch edits and
    // leave the builder file byte-identical, so a line the author wrote cannot have been silently
    // pruned to reach convergence -- and let the caller assert the converged state while the scene
    // and source are still live. `ProveCodeToScene` cannot express this: it never rebuilds, and its
    // convergence check leaves the FIRST sync unasserted.
    public static ProofResult ProveCodeToSceneFixedPoint(
        string usings, string body,
        Action<ProofContext> assertScene, Action<ProofContext> assertConverged)
    {
        var fixture = NewFixture();
        try
        {
            var build = SeedScene(fixture, usings, body);

            assertScene(new ProofContext(fixture.BuilderPath, fixture.SidecarPath, fixture.ScenePath, build, null));

            for (var pass = 2; pass <= 3; pass++)
            {
                var rebuild = SceneBuilderBuild.Run(
                    fixture.BuilderPath, fixture.ScenePath, fixture.SidecarPath, EditorSceneManager.GetActiveScene());
                Assert.IsEmpty(
                    rebuild.Diagnostics,
                    $"Build {pass} of an unchanged source and scene was refused.\n"
                        + string.Join("\n", rebuild.Diagnostics.Select(d => d.Message)));
                Assert.AreEqual(
                    0, rebuild.PlanOpCount,
                    $"Build {pass} of an unchanged source and scene applied {rebuild.PlanOpCount} plan "
                        + "op(s); a converged build applies none.");
            }

            var beforeFirstSync = File.ReadAllBytes(fixture.BuilderPath);
            var firstSync = EmittedCodeCompiles.SyncAndAssertCompiles(
                fixture.BuilderPath, fixture.SidecarPath, EditorSceneManager.GetActiveScene());
            Assert.AreEqual(
                0, firstSync.PatchEdits,
                "The first sync over a converged build produced " + firstSync.PatchEdits
                    + " patch edit(s); it must be a fixed point.");
            Assert.IsFalse(
                firstSync.Changed, "The first sync over a converged build reported Changed even though it must be a no-op.");
            CollectionAssert.AreEqual(
                beforeFirstSync, File.ReadAllBytes(fixture.BuilderPath),
                "The first sync rewrote the author's builder source; a sync that pruned an authored line "
                    + "would still look converged on the second sync, so only this assertion can see it.");

            var convergenceSync = ConvergeToFixedPoint(fixture, "code->scene fixed point");

            assertConverged(new ProofContext(fixture.BuilderPath, fixture.SidecarPath, fixture.ScenePath, build, convergenceSync));

            return new ProofResult(
                build,
                driveSync: null,
                convergenceSync: convergenceSync,
                convergedSource: File.ReadAllText(fixture.BuilderPath),
                convergedSidecar: File.ReadAllText(fixture.SidecarPath));
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    public static ProofResult ProveSceneToCode(
        string usings, string body, Action<ProofContext> mutateScene, Action<ProofContext> assertSource)
    {
        var fixture = NewFixture();
        try
        {
            var build = SeedScene(fixture, usings, body);

            ConvergeToFixedPoint(fixture, "scene->code baseline (before the mutation)");

            mutateScene(new ProofContext(fixture.BuilderPath, fixture.SidecarPath, fixture.ScenePath, build, null));

            var beforeDrive = File.ReadAllBytes(fixture.BuilderPath);
            var driveSync = EmittedCodeCompiles.SyncAndAssertCompiles(
                fixture.BuilderPath, fixture.SidecarPath, EditorSceneManager.GetActiveScene());
            var afterDrive = File.ReadAllBytes(fixture.BuilderPath);
            CollectionAssert.AreNotEqual(
                beforeDrive, afterDrive,
                "The scene mutation reached no builder-source edit; the drive sync must change the source.");

            assertSource(new ProofContext(fixture.BuilderPath, fixture.SidecarPath, fixture.ScenePath, build, driveSync));

            var convergenceSync = ConvergeToFixedPoint(fixture, "scene->code convergence");

            return new ProofResult(
                build,
                driveSync: driveSync,
                convergenceSync: convergenceSync,
                convergedSource: File.ReadAllText(fixture.BuilderPath),
                convergedSidecar: File.ReadAllText(fixture.SidecarPath));
        }
        finally
        {
            Cleanup(fixture);
        }
    }
}
