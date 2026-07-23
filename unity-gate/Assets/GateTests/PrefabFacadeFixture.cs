using UnityEditor;
using UnityEngine;

// b6-t1 (specs/25-typed-prefab-facades.md): the SINGLE canonical fixture-authoring routine every
// EditMode test in bucket b6 shares (FIX-1 — no divergent fixtures). Builds two real `.prefab`
// assets at runtime under a caller-supplied temp folder:
//   Turret.prefab: root "Turret" with children "Barrel" (carrying a Light component) and
//                  "Antenna" (carrying a MeshRenderer component — m-nested-props b5-t1's
//                  removed-component / removed-child target).
//   Tank.prefab:   root "Tank" nesting Turret TWICE (PrefabUtility.InstantiatePrefab, renamed
//                  "LeftTurret"/"RightTurret" — genuine nested-prefab instances, the depth>=2
//                  collision b6-t6 exercises), plus a "Chassis" child with children in order
//                  ["Wheel","Wheel","Front Wheel"] (identical-name duplicate siblings +
//                  space-containing sanitize target).
public static class PrefabFacadeFixture
{
    public struct Handles
    {
        public string TurretPath;
        public string TankPath;
        public string TurretGuid;
        public string TankGuid;
    }

    public static Handles Create(string dir)
    {
        if (!AssetDatabase.IsValidFolder(dir))
        {
            var parent = dir.Substring(0, dir.LastIndexOf('/'));
            var folderName = dir.Substring(dir.LastIndexOf('/') + 1);
            AssetDatabase.CreateFolder(parent, folderName);
        }

        var turretPath = dir + "/Turret.prefab";
        var tankPath = dir + "/Tank.prefab";

        var turretRoot = new GameObject("Turret");
        var barrel = new GameObject("Barrel");
        barrel.transform.SetParent(turretRoot.transform);
        barrel.AddComponent<Light>();

        var antenna = new GameObject("Antenna");
        antenna.transform.SetParent(turretRoot.transform);
        antenna.AddComponent<MeshRenderer>();

        PrefabUtility.SaveAsPrefabAsset(turretRoot, turretPath);
        Object.DestroyImmediate(turretRoot);

        var turretAsset = AssetDatabase.LoadAssetAtPath<GameObject>(turretPath);

        var tankRoot = new GameObject("Tank");

        var leftTurret = (GameObject)PrefabUtility.InstantiatePrefab(turretAsset);
        leftTurret.name = "LeftTurret";
        leftTurret.transform.SetParent(tankRoot.transform);

        var rightTurret = (GameObject)PrefabUtility.InstantiatePrefab(turretAsset);
        rightTurret.name = "RightTurret";
        rightTurret.transform.SetParent(tankRoot.transform);

        var chassis = new GameObject("Chassis");
        chassis.transform.SetParent(tankRoot.transform);

        var wheel1 = new GameObject("Wheel");
        wheel1.transform.SetParent(chassis.transform);
        var wheel2 = new GameObject("Wheel");
        wheel2.transform.SetParent(chassis.transform);
        var frontWheel = new GameObject("Front Wheel");
        frontWheel.transform.SetParent(chassis.transform);

        PrefabUtility.SaveAsPrefabAsset(tankRoot, tankPath);
        Object.DestroyImmediate(tankRoot);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        return new Handles
        {
            TurretPath = turretPath,
            TankPath = tankPath,
            TurretGuid = AssetDatabase.AssetPathToGUID(turretPath),
            TankGuid = AssetDatabase.AssetPathToGUID(tankPath),
        };
    }

    public static void Delete(string dir)
    {
        AssetDatabase.DeleteAsset(dir + "/Tank.prefab");
        AssetDatabase.DeleteAsset(dir + "/Turret.prefab");
        if (AssetDatabase.IsValidFolder(dir))
        {
            AssetDatabase.DeleteAsset(dir);
        }
    }
}
