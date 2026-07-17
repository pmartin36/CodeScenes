using UnityEngine;

// The M5 cross-object-reference example fixture, verbatim from the spec's FooScene example
// (specs/06-m5-cross-object-references.md): a managed GameObject-typed reference field. Deliberately
// in the GLOBAL namespace to match the spec's bare `Component<DoorOpener>` authoring form.
public class DoorOpener : MonoBehaviour
{
    public GameObject target;
}
