using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

// b6-t2: an imported .fbx model prefab is a first-class instance source (specs/07-m6-prefab-instances.md,
// In scope lines 38-45). Mirrors PlanExecutorInstantiatePrefabTests (b5-t1) and SnapshotReaderPrefabTests
// (b5-t2), sourcing the instance from a REAL imported .fbx (via ModelImporter) instead of a .prefab, to
// prove the same source-agnostic detection path (PrefabInstanceProbe) covers model prefabs unchanged.
public class PrefabInstanceFbxTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_M6Fbx";
    private const string FbxPath = FixturesDir + "/M6_GateFbx.fbx";
    private const string ScenePath = "Assets/GateTests/__PrefabInstanceFbxTemp.unity";

    // Minimal ASCII FBX proven to import via ModelImporter in this exact editor (research.md b6-t2):
    // a single-triangle mesh under one Model node. Do not depend on the Model node's name for
    // anything semantic — only the imported asset's GUID (resolved at runtime) is used.
    private const string FbxContent = @"; FBX 7.3.0 project file
FBXHeaderExtension:  {
	FBXHeaderVersion: 1003
	FBXVersion: 7300
	Creator: ""codescenes gate fixture""
}
GlobalSettings:  {
	Version: 1000
	Properties70:  {
		P: ""UnitScaleFactor"", ""double"", ""Number"", """",1
	}
}
Objects:  {
	Geometry: 1000, ""Geometry::"", ""Mesh"" {
		Vertices: *9 {
			a: -0.5,-0.5,0, 0.5,-0.5,0, 0,0.5,0
		}
		PolygonVertexIndex: *3 {
			a: 0,1,-3
		}
		GeometryVersion: 124
		LayerElementNormal: 0 {
			Version: 101
			Name: """"
			MappingInformationType: ""ByPolygonVertex""
			ReferenceInformationType: ""Direct""
			Normals: *9 {
				a: 0,0,1, 0,0,1, 0,0,1
			}
		}
		Layer: 0 {
			Version: 100
			LayerElement:  {
				Type: ""LayerElementNormal""
				TypedIndex: 0
			}
		}
	}
	Model: 2000, ""Model::M6_GateFbx"", ""Mesh"" {
		Version: 232
		Properties70:  {
			P: ""DefaultAttributeIndex"", ""int"", ""Integer"", """",0
		}
	}
}
Connections:  {
	C: ""OO"",2000,0
	C: ""OO"",1000,2000
}
";

    private string _guid;

    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_M6Fbx");
        }

        File.WriteAllText(FbxPath, FbxContent);
        AssetDatabase.ImportAsset(FbxPath, ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.Refresh();

        _guid = AssetDatabase.AssetPathToGUID(FbxPath);
        Assert.IsNotEmpty(_guid, "Setup: fixture .fbx did not import (no GUID resolved)");
    }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(FbxPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    [Test]
    public void Fbx_InstantiatedViaExecutor_IsConnectedModelPrefab()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new InstantiatePrefab { LogicalId = "Model1", Guid = _guid, ParentLogicalId = null, SiblingIndex = 0 },
            },
        };

        var result = PlanExecutor.Execute(plan, new IdentityMap(), scene);

        Assert.IsTrue(result.GameObjectsByLogicalId.TryGetValue("Model1", out var root),
            "InstantiatePrefab did not register the FBX instance root under its LogicalId");
        Assert.AreEqual(PrefabInstanceStatus.Connected, PrefabUtility.GetPrefabInstanceStatus(root),
            "Instantiated FBX object is not a connected prefab instance");

        var source = PrefabUtility.GetCorrespondingObjectFromSource(root);
        Assert.AreEqual(PrefabAssetType.Model, PrefabUtility.GetPrefabAssetType(source),
            "Source asset is not a Model prefab type — fixture did not import as an FBX model");
    }

    [Test]
    public void Fbx_SnapshotReader_StampsFbxGuidAndPrefabKey()
    {
        var fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
        Assert.IsNotNull(fbxRoot, "Setup: could not load the imported FBX's root GameObject");

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxRoot);
        instance.name = "Model1";

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = SceneSnapshotReader.Read(scene).Roots.First(r => r.Name == "Model1");

        Assert.AreEqual(_guid, node.SourcePrefabGuid,
            "Snapshot reader did not stamp the FBX's asset GUID as SourcePrefabGuid");
        Assert.IsNotNull(node.PrefabKey, "Snapshot reader did not populate PrefabKey for the FBX instance");
        Assert.AreNotEqual(0UL, node.PrefabKey!.TargetPrefabId, "PrefabKey.TargetPrefabId was zero for the FBX instance");
    }
}
