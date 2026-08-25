using UnityEngine;

// Managed-field target for sub-asset type disambiguation: a plain Material field resolved via
// reflection over the component's own C# fields, with no throwaway component instance required.
public class SubAssetTypeMaterialHolder : MonoBehaviour
{
    public Material mat;
}
