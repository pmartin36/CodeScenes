using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Editor;

// b3-t2: BuiltinCatalog against the LIVE AssetDatabase. Every assertion proves the two sanctioned
// primitives (LoadAllAssetsAtPath + TryGetGUIDAndLocalFileIdentifier) rather than the
// Resources.GetBuiltinResource trap, which silently returns the WRONG mesh for 4 of 6 primitives
// (spec 17-builtin-resources.md §Research). Object-identity assertions (AreSame), never
// name/label comparisons, are what catch that class of bug.
public class BuiltinCatalogTests
{
    [Test]
    public void Resolve_BareCubeName_ReturnsTheMeshAPrimitiveCubeUses()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            var expectedMesh = go.GetComponent<MeshFilter>().sharedMesh;

            var resolved = BuiltinCatalog.Resolve("Cube", null, out var ambiguous);

            Assert.AreSame(expectedMesh, resolved, "Resolve(\"Cube\") must return the SAME mesh a real primitive uses.");
            Assert.IsFalse(ambiguous);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Resolve_QualifiedUISprite_DistinguishesSpriteFromTexture2D()
    {
        var sprite = BuiltinCatalog.Resolve("UISprite", "Sprite", out var spriteAmbiguous);
        var texture = BuiltinCatalog.Resolve("UISprite", "Texture2D", out var textureAmbiguous);

        Assert.IsNotNull(sprite);
        Assert.IsNotNull(texture);
        Assert.AreNotSame(sprite, texture);
        Assert.IsInstanceOf<Sprite>(sprite);
        Assert.IsInstanceOf<Texture2D>(texture);
        Assert.IsFalse(spriteAmbiguous);
        Assert.IsFalse(textureAmbiguous);
    }

    [Test]
    public void Resolve_BareAmbiguousName_IsAmbiguousAndReturnsNull()
    {
        var resolved = BuiltinCatalog.Resolve("UISprite", null, out var ambiguous);

        Assert.IsTrue(ambiguous, "Bare 'UISprite' matches both a Sprite and a Texture2D — must report ambiguous.");
        Assert.IsNull(resolved, "An ambiguous match must never guess.");
    }

    [Test]
    public void Resolve_UnknownName_ReturnsNullAndIsNotAmbiguous()
    {
        var resolved = BuiltinCatalog.Resolve("NoSuchBuiltinName", null, out var ambiguous);

        Assert.IsNull(resolved);
        Assert.IsFalse(ambiguous);
    }

    [Test]
    public void TryDeriveName_LivePrimitiveMeshAndMaterial_InvertsGuidAndFileIdToName()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            var material = go.GetComponent<MeshRenderer>().sharedMaterial;

            Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out var meshGuid, out var meshFileId));
            Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out var materialGuid, out var materialFileId));

            var meshFound = BuiltinCatalog.TryDeriveName(
                meshGuid, meshFileId, out var meshName, out var meshType, out var meshAmbiguous);
            Assert.IsTrue(meshFound);
            Assert.AreEqual("Cube", meshName);
            Assert.AreEqual("Mesh", meshType);
            Assert.IsFalse(meshAmbiguous);

            var materialFound = BuiltinCatalog.TryDeriveName(
                materialGuid, materialFileId, out var materialName, out var materialType, out var materialAmbiguous);
            Assert.IsTrue(materialFound);
            Assert.AreEqual("Default-Material", materialName);
            Assert.AreEqual("Material", materialType);
            Assert.IsFalse(materialAmbiguous);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void TryDeriveName_AmbiguousBareName_ReportsNameIsAmbiguous()
    {
        var sprite = BuiltinCatalog.Resolve("UISprite", "Sprite", out _);
        Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out var guid, out var fileId));

        var found = BuiltinCatalog.TryDeriveName(guid, fileId, out var name, out var typeName, out var nameIsAmbiguous);

        Assert.IsTrue(found);
        Assert.AreEqual("UISprite", name);
        Assert.AreEqual("Sprite", typeName);
        Assert.IsTrue(nameIsAmbiguous, "The Cube case (above) must contrast with this — bare 'UISprite' is ambiguous.");
    }

    [Test]
    public void TryDeriveName_FabricatedFileId_ReturnsFalse()
    {
        var found = BuiltinCatalog.TryDeriveName(
            BuiltinCatalog.BuiltinResourcesGuid, 999999999L, out var name, out var typeName, out var nameIsAmbiguous);

        Assert.IsFalse(found);
        Assert.AreEqual("", name);
        Assert.AreEqual("", typeName);
        Assert.IsFalse(nameIsAmbiguous);
    }

    [Test]
    public void Suggest_NearMissName_IncludesTheRealName()
    {
        var suggestions = BuiltinCatalog.Suggest("Cub", null).ToList();

        Assert.Contains("Cube", suggestions);
        Assert.LessOrEqual(suggestions.Count, 5);
        Assert.AreEqual(suggestions.Distinct().Count(), suggestions.Count, "Suggest must be deduped.");
    }

    [Test]
    public void CandidateTypeNames_AmbiguousName_NamesBothCandidateTypes()
    {
        var candidates = BuiltinCatalog.CandidateTypeNames("UISprite").ToList();

        Assert.Contains("Sprite", candidates);
        Assert.Contains("Texture2D", candidates);
    }

    [Test]
    public void Catalog_RepeatedQueries_ScanTheContainersOnlyOnce()
    {
        BuiltinCatalog.Resolve("Cube", null, out _);
        Assert.AreEqual(1, BuiltinCatalog.BuildCount, "First use must build the catalog exactly once.");

        BuiltinCatalog.Resolve("Sphere", null, out _);
        BuiltinCatalog.TryDeriveName(BuiltinCatalog.BuiltinResourcesGuid, 10202L, out _, out _, out _);
        BuiltinCatalog.Suggest("Cub", null);
        BuiltinCatalog.CandidateTypeNames("UISprite");

        Assert.AreEqual(1, BuiltinCatalog.BuildCount, "Further queries must answer from the cache, never rescan.");
    }
}
