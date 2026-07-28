using System.IO;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.GateFixtures;

// b7-t1/b7-t2 (specs/28-typed-asset-catalogs.md): shared runtime fixture set for the typed-asset
// Build/Sync EditMode round-trip. Mirrors AssetCatalogTests.CreateFixtures (b6-t1) verbatim but
// lives under its OWN directory — that class's fixtures are private and torn down after its own
// [TearDown], and running both suites against the same folder would race. Both b7-t1
// (AssetCatalogBuildTests) and b7-t2 (AssetCatalogSyncTests) reuse this one copy.
public static class AssetCatalogBuildFixtures
{
    public const string Dir = "Assets/GateTests/Fixtures_AssetCatalogBuild";

    public const string RocksMatPath = Dir + "/Environment/Rocks/Stone.mat";
    public const string SkinMatPath = Dir + "/Characters/Skin/Stone.mat";
    public const string RocksSpritePath = Dir + "/Environment/Rocks/Stone.png";
    // NOTE: the container's own file/main-object name is deliberately "BarrelContainer", NOT
    // "Barrel" — Unity's native importer re-titles a main asset to match its filename on reimport
    // (confirmed empirically), which would collide with a sub-object ALSO named "Barrel" and make
    // Asset(path, "Barrel") genuinely ambiguous by name. Keeping filename == the intended main
    // name avoids that rename; the sub-mesh's OWN name ("Barrel") is what AssetCatalogGenerator
    // records as SubAsset/MemberName, independent of the container filename.
    public const string BarrelContainerPath = Dir + "/Models/Props/BarrelContainer.asset";
    public const string BarrelSubAssetName = "Barrel";

    // Raw FolderSegments the producer derives = the container's directory with only the leading
    // "Assets" segment stripped (AssetCatalogGenerator; verified in AssetCatalogTests.cs) — so
    // every typed member below carries this fixture root's own two-segment prefix.
    public const string RootPrefix = "GateTests.Fixtures_AssetCatalogBuild";

    public const string TypedRocksStone = "Assets.Materials." + RootPrefix + ".Environment.Rocks.Stone";
    public const string TypedSkinStone = "Assets.Materials." + RootPrefix + ".Characters.Skin.Stone";
    public const string TypedRocksSprite = "Assets.Sprites." + RootPrefix + ".Environment.Rocks.Stone";
    public const string TypedBarrel = "Assets.Meshes." + RootPrefix + ".Models.Props.Barrel";

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
        {
            return;
        }

        var parent = path.Substring(0, path.LastIndexOf('/'));
        var name = path.Substring(path.LastIndexOf('/') + 1);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }

    // Builds the fixture set and regenerates the manifest so it is present on disk before any
    // Build/Sync call. Two distinct same-leaf materials in different folders (cross-folder
    // collision case), a same-folder same-leaf Sprite (independent type-group namespace), and a
    // genuine sub-mesh (non-zero FileId) inside a non-Mesh-main container.
    public static void Create()
    {
        EnsureFolder(Dir + "/Environment/Rocks");
        EnsureFolder(Dir + "/Characters/Skin");
        EnsureFolder(Dir + "/Models/Props");

        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), RocksMatPath);
        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), SkinMatPath);

        var spriteFullPath = Path.Combine(Application.dataPath, RocksSpritePath.Substring("Assets/".Length));
        var tex = new Texture2D(4, 4);
        File.WriteAllBytes(spriteFullPath, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(RocksSpritePath);
        var importer = (TextureImporter)AssetImporter.GetAtPath(RocksSpritePath);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.SaveAndReimport();

        var containerMain = new Material(Shader.Find("Standard")) { name = "BarrelContainer" };
        AssetDatabase.CreateAsset(containerMain, BarrelContainerPath);
        var barrelSub = new Mesh { name = BarrelSubAssetName };
        AssetDatabase.AddObjectToAsset(barrelSub, containerMain);
        AssetDatabase.ImportAsset(BarrelContainerPath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        AssetCatalogGenerator.Regenerate();
    }

    public static void Delete()
    {
        if (AssetDatabase.IsValidFolder(Dir))
        {
            AssetDatabase.DeleteAsset(Dir);
        }
    }
}
