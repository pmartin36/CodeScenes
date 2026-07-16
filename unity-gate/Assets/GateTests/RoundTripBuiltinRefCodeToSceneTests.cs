using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;

// M-Builtin code->scene round-trip gate tests (#1, #2, #6). Authoring Builtin("Name") on a
// MeshFilter/MeshRenderer must materialize the SAME live objects a real
// GameObject.CreatePrimitive gets — asserted by reference against that independent oracle, never
// against BuiltinCatalog (which is the resolver under test).
public class RoundTripBuiltinRefCodeToSceneTests : BuiltinRefGateHarness
{
    // #1 (Cube) + #2 (Sphere, Capsule, Cylinder, Plane, Quad): authoring Builtin("<meshName>") on
    // MeshFilter.m_Mesh and Builtin("Default-Material") on MeshRenderer.m_Materials must assign,
    // BY REFERENCE, the identical mesh/material a live CreatePrimitive(type) carries. Sphere,
    // Capsule, Cylinder and Plane are exactly where Resources.GetBuiltinResource silently returns
    // the wrong legacy mesh — a name/label assertion would pass that bug; identity does not.
    [TestCase(PrimitiveType.Cube, "Cube")]
    [TestCase(PrimitiveType.Sphere, "Sphere")]
    [TestCase(PrimitiveType.Capsule, "Capsule")]
    [TestCase(PrimitiveType.Cylinder, "Cylinder")]
    [TestCase(PrimitiveType.Plane, "Plane")]
    [TestCase(PrimitiveType.Quad, "Quad")]
    public void CodeToScene_AuthoredBuiltinPrimitive_AssignsSameMeshAndMaterialAsCreatePrimitive(
        PrimitiveType type, string meshName)
    {
        var expectedMesh = PrimitiveMesh(type);
        var expectedMaterial = PrimitiveMaterial(type);

        var body =
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"" + meshName + "\")));\n" +
            "        widget.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }));";
        File.WriteAllText(BuilderPath, Source(body));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, scene);

        var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        Assert.IsNotNull(widget, "Widget was not created by SceneBuilderBuild.Run");

        var mf = widget.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf, "Authored MeshFilter was not materialized on Widget");
        Assert.IsNotNull(mf.sharedMesh,
            "Builtin(\"" + meshName + "\") did not assign a mesh — the reference was left unresolved/skipped.");
        Assert.AreEqual(expectedMesh, mf.sharedMesh,
            "Assigned mesh is not the SAME object as a live CreatePrimitive(" + type + ")'s mesh.");

        var mr = widget.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "Authored MeshRenderer was not materialized on Widget");
        Assert.IsNotNull(mr.sharedMaterial,
            "Builtin(\"Default-Material\") did not assign a material — the reference was left unresolved/skipped.");
        Assert.AreEqual(expectedMaterial, mr.sharedMaterial,
            "Assigned material is not the SAME object as a live CreatePrimitive(" + type + ")'s material.");
    }

    // #6: a mixed material list — a project Asset(...) alongside a Builtin(...) — must materialize
    // BOTH slots, in the authored order: [0] the project asset, [1] the built-in.
    [Test]
    public void CodeToScene_AuthoredMixedMaterialList_AssignsProjectAssetThenBuiltinInOrder()
    {
        var expectedBuiltin = PrimitiveMaterial(PrimitiveType.Cube);

        var body =
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + RedPath + "\"), Builtin(\"Default-Material\") }));";
        File.WriteAllText(BuilderPath, Source(body));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, scene);

        var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        Assert.IsNotNull(widget, "Widget was not created by SceneBuilderBuild.Run");
        var mr = widget.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "Authored MeshRenderer was not materialized on Widget");

        Assert.AreEqual(2, mr.sharedMaterials.Length,
            "Mixed material list did not materialize exactly 2 slots.");
        Assert.AreEqual(LoadMaterial(RedPath), mr.sharedMaterials[0],
            "Slot [0] is not the project asset (Red.mat).");
        Assert.AreEqual(expectedBuiltin, mr.sharedMaterials[1],
            "Slot [1] is not the built-in Default-Material.");
        Assert.AreNotEqual(mr.sharedMaterials[0], mr.sharedMaterials[1],
            "Both slots hold the SAME object — the order assertion above would be vacuous.");
    }
}
