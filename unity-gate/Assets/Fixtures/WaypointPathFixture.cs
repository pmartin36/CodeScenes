using System.Collections.Generic;
using UnityEngine;

// A scene-reference LIST field, both serialization shapes Unity supports: a plain array and a
// generic List<T>. Deliberately in the GLOBAL namespace to match the bare `Component<WaypointPath>`
// authoring form.
public class WaypointPath : MonoBehaviour
{
    public GameObject[] waypoints;
    public List<GameObject> targets;
}
