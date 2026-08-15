using UnityEngine;

// The mixed-field cross-reference fixture: one component whose fields cover a FORWARD
// cross-object reference, a BACKWARD cross-object reference, and a plain scalar in the same
// closure, so a test can assert all three fold into ONE `Component<MixedRefWiring>(...)` call
// rather than splitting across several. Global namespace, matching DoorOpener.
public class MixedRefWiring : MonoBehaviour
{
    public GameObject forward;
    public GameObject backward;
    public int weight;
}
