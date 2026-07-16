using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;

// b4-t3: located-error round-trip gate tests (#9, #10, #11) — the real-Build + live-slot-untouched
// half that RoundTripAssetRefTests.cs:468-499 does not cover (those two drive DesiredModelLoader.Load
// directly, message-only, no scene). #11's "Library/unity default resources" case is ALREADY fully
// asserted end-to-end by RoundTripAssetRefTests.cs:395-434
// (CodeToScene_AuthoredContainerPath_ThrowsLocatedErrorPointingAtBuiltin) — deliberately NOT
// duplicated here. This file instead covers IsContainerPath's untested second arm,
// "Resources/unity_builtin_extra" (BuiltinCatalog.cs:28).
public class RoundTripBuiltinRefErrorTests : BuiltinRefGateHarness
{
    // #9: an unresolvable Builtin(...) name must throw a located error naming the object, component,
    // field and the bad name, WITH near-miss suggestions — and must NOT touch the scene, so a
    // pre-existing live mesh on the field survives untouched.
    //
    // Vacuity traps: the object is named "Widget", never "Cube" — with a "Cube" object,
    // Contains("Cube") would pass off the location prefix even if suggestions were completely broken.
    // "Cub" is asserted in its quoted form ('Cub') because it is a literal substring of "Cube".
    [Test]
    public void CodeToScene_AuthoredUnresolvableBuiltinName_ThrowsLocatedErrorAndLeavesLiveSlotUntouched()
    {
        // Phase 1: build Widget + a bare MeshFilter (no ref yet) so the field is mapped.
        File.WriteAllText(BuilderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>();"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, scene);

        var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        Assert.IsNotNull(widget, "Widget was not created by SceneBuilderBuild.Run");
        var mf = widget.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf, "Authored MeshFilter was not materialized on Widget");

        // Phase 2: give the live field a real built-in mesh directly in the scene (the donor technique).
        var donorMesh = PrimitiveMesh(PrimitiveType.Sphere);
        mf.sharedMesh = donorMesh;

        // Phase 3: author the misspelled Builtin(...) name and rebuild. Must THROW, must not clear.
        File.WriteAllText(BuilderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cub\")));"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, EditorSceneManager.GetActiveScene()),
            "Build did not throw on an unresolvable Builtin(...) name.");

        StringAssert.Contains("Widget", ex.Message, "Error does not name the object.\n" + ex.Message);
        StringAssert.Contains("MeshFilter", ex.Message, "Error does not name the component.\n" + ex.Message);
        StringAssert.Contains("m_Mesh", ex.Message, "Error does not name the field.\n" + ex.Message);
        StringAssert.Contains("'Cub'", ex.Message, "Error does not name the bad name.\n" + ex.Message);
        StringAssert.Contains("Did you mean", ex.Message, "Error does not offer suggestions.\n" + ex.Message);
        StringAssert.Contains("Cube", ex.Message, "Error does not suggest the near-miss 'Cube'.\n" + ex.Message);

        Assert.AreEqual(donorMesh, widget.GetComponent<MeshFilter>().sharedMesh,
            "Build must leave the live mesh untouched when refusing an unresolvable Builtin(...) name, not clear it.");
    }

    // #10: a bare Builtin(...) name matching more than one type ("UISprite" matches both a Sprite and
    // a Texture2D in this gate project — proven by the green
    // RoundTripBuiltinRefSceneToCodeTests.cs:160-187) must throw a located error naming BOTH candidate
    // types and telling the author to qualify — and must NOT touch the scene, so nothing is guessed.
    //
    // Vacuity trap: "Sprite" is a literal substring of "UISprite" (the offending name itself), so a
    // plain Contains("Sprite") would pass even if the candidate list were broken. Use a word-boundary
    // regex so the standalone "Sprite" candidate is required, not the "UISprite" substring.
    [Test]
    public void CodeToScene_AuthoredBareAmbiguousBuiltin_ThrowsLocatedErrorNamingBothTypesAndGuessesNothing()
    {
        // Phase 1: build Widget + a bare MeshFilter (no ref yet) so the field is mapped.
        File.WriteAllText(BuilderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>();"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, scene);

        var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        Assert.IsNotNull(widget, "Widget was not created by SceneBuilderBuild.Run");
        var mf = widget.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf, "Authored MeshFilter was not materialized on Widget");

        // Phase 2: give the live field a real built-in mesh directly in the scene (the donor technique).
        var donorMesh = PrimitiveMesh(PrimitiveType.Sphere);
        mf.sharedMesh = donorMesh;

        // Phase 3: author the bare ambiguous Builtin(...) name and rebuild. Must THROW, must not guess.
        File.WriteAllText(BuilderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"UISprite\")));"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, EditorSceneManager.GetActiveScene()),
            "Build did not throw on a bare ambiguous Builtin(...) name.");

        StringAssert.Contains("Widget", ex.Message, "Error does not name the object.\n" + ex.Message);
        StringAssert.Contains("MeshFilter", ex.Message, "Error does not name the component.\n" + ex.Message);
        StringAssert.Contains("m_Mesh", ex.Message, "Error does not name the field.\n" + ex.Message);
        Assert.IsTrue(Regex.IsMatch(ex.Message, @"\bSprite\b"),
            "Error does not name the standalone Sprite candidate.\n" + ex.Message);
        StringAssert.Contains("Texture2D", ex.Message, "Error does not name the Texture2D candidate.\n" + ex.Message);
        StringAssert.Contains("Qualify", ex.Message, "Error does not tell the author to qualify.\n" + ex.Message);

        Assert.AreEqual(donorMesh, widget.GetComponent<MeshFilter>().sharedMesh,
            "Build must leave the live mesh untouched (nothing guessed) when refusing an ambiguous Builtin(...) name.");
    }

    // #11, untested arm: an authored Asset("Resources/unity_builtin_extra") path — the SECOND
    // IsContainerPath literal (BuiltinCatalog.cs:28) — must be refused with a located error pointing
    // at Builtin(...), same as the first container path (already covered end-to-end by
    // RoundTripAssetRefTests.cs:395-434 for "Library/unity default resources").
    [Test]
    public void CodeToScene_AuthoredBuiltinExtraContainerPath_ThrowsLocatedErrorPointingAtBuiltin()
    {
        // Phase 1: build Widget + a bare MeshFilter (no ref yet) so the field is mapped.
        File.WriteAllText(BuilderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>();"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, scene);

        var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        Assert.IsNotNull(widget, "Widget was not created by SceneBuilderBuild.Run");
        var mf = widget.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf, "Authored MeshFilter was not materialized on Widget");

        // Phase 2: give the live field a real built-in mesh directly in the scene (the donor technique).
        var donorMesh = PrimitiveMesh(PrimitiveType.Sphere);
        mf.sharedMesh = donorMesh;

        // Phase 3: author the OLD container-path form (second literal) and rebuild. Must THROW.
        // Source(...) injects "using static AssetRefs;" automatically because this body contains "Asset(".
        File.WriteAllText(BuilderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Asset(\"Resources/unity_builtin_extra\")));"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, EditorSceneManager.GetActiveScene()),
            "Build did not throw on an authored 'Resources/unity_builtin_extra' container path.");

        StringAssert.Contains("Widget", ex.Message, "Error does not name the object.\n" + ex.Message);
        StringAssert.Contains("MeshFilter", ex.Message, "Error does not name the component.\n" + ex.Message);
        StringAssert.Contains("m_Mesh", ex.Message, "Error does not name the field.\n" + ex.Message);
        StringAssert.Contains("Builtin", ex.Message, "Error does not point at Builtin(...) as the fix.\n" + ex.Message);

        Assert.AreEqual(donorMesh, widget.GetComponent<MeshFilter>().sharedMesh,
            "Build must leave the live mesh untouched when refusing a container-path reference, not clear it.");
    }
}
