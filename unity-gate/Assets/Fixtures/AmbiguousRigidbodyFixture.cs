using UnityEngine;

namespace MyGame.Physics
{
    // A user MonoBehaviour deliberately NAMED `Rigidbody`, colliding in simple-name with
    // `UnityEngine.Rigidbody`. Importing both `MyGame.Physics` and `UnityEngine` and authoring
    // `Component<Rigidbody>` must resolve as AMBIGUOUS — a located error listing both fully-qualified
    // candidates, never a silent pick (specs/20-unqualified-type-names.md).
    public class Rigidbody : MonoBehaviour
    {
    }
}
