using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// Smoke test for gate-project CAPABILITIES, not product behavior. Proves that the ugui package is
// resolvable and compilable FROM the GateTests assembly (via GateTests.asmdef's "references" entry)
// and that UnityEngine.UI.Image's m_Sprite is reachable via SerializedProperty — the exact surface
// b3-t4/b4-t2 build the built-in-Sprite adapter tests on. Fully qualifies UnityEngine.UI.Image to
// avoid ambiguity with UnityEngine.UIElements.Image.
public class GateProjectCapabilitiesTests
{
    [Test]
    public void UGui_ImageComponent_ResolvesAndExposesSpriteProperty()
    {
        var go = new GameObject("GateProjectCapabilitiesTests.Image");
        try
        {
            var image = go.AddComponent<UnityEngine.UI.Image>();
            Assert.IsNotNull(image, "UnityEngine.UI.Image did not resolve as an addable Component.");

            var serialized = new SerializedObject(image);
            var spriteProperty = serialized.FindProperty("m_Sprite");
            Assert.IsNotNull(spriteProperty, "Image.m_Sprite was not reachable via SerializedProperty.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
