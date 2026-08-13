using System.Collections.Generic;
using UnityEngine;

// [SerializeReference] polymorphic-field fixtures (specs/m9-serializereference): an interface with
// multiple concrete implementations, one of which carries a scene-object reference and an
// asset reference of its own, and a MonoBehaviour host with both a single polymorphic field and a
// polymorphic list field. Deliberately in the GLOBAL namespace to match the bare `Component<AiBrain>`
// authoring form used by sibling fixtures (DoorOpener, WaypointPath).
public interface IStrategy
{
}

public class Aggressive : IStrategy
{
    public float range;
    public GameObject target;
    public Material someAsset;
}

public class Flee : IStrategy
{
    public float speed;
}

public class Composite : IStrategy
{
    [SerializeReference] public IStrategy primary;
    [SerializeReference] public IStrategy fallback;
}

public class AiBrain : MonoBehaviour
{
    [SerializeReference] public IStrategy strategy;
    [SerializeReference] public List<IStrategy> strategies;
}
