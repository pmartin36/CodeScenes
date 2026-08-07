using System;
using UnityEngine;
using UnityEngine.Events;

// An int-argument listener target: `SetLevel(int)` for the dynamic int-mode authoring form,
// `Level` exposed as a read-only property so the invocation is observable.
public class Lamp : MonoBehaviour
{
    private int _level;
    public int Level => _level;
    public void SetLevel(int level) { _level = level; }
}

// A float-argument listener target for the dynamic float-mode authoring form.
public class Mixer : MonoBehaviour
{
    private float _volume;
    public float Volume => _volume;
    public void SetVol(float volume) { _volume = volume; }
}

// Carries both a void method (`Refresh`, for the static void `.OnEvent` form) and a float-argument
// method (`SetValue`, for the dynamic float form) on distinct names, so `SetValue` is never
// overloaded and stays safely usable as a bare method-group argument.
public class Hud : MonoBehaviour
{
    private int _refreshCount;
    private float _value;
    public int RefreshCount => _refreshCount;
    public float Value => _value;
    public void Refresh() { _refreshCount++; }
    public void SetValue(float value) { _value = value; }
}

// A listener target with one method per dynamic argument kind that isn't already covered
// elsewhere: string, bool, a scene object (GameObject) and an asset object (Material).
public class Marquee : MonoBehaviour
{
    private string _text;
    private bool _visible;
    private GameObject _subject;
    private Material _material;
    public string Text => _text;
    public bool Visible => _visible;
    public GameObject Subject => _subject;
    public Material Material => _material;
    public void SetText(string text) { _text = text; }
    public void SetVisible(bool visible) { _visible = visible; }
    public void SetSubject(GameObject subject) { _subject = subject; }
    public void SetMaterial(Material material) { _material = material; }
}

// One method name at two arities (`Apply()` and `Apply(int)`), so the argument at a call site is
// what selects the overload, not a name lookup.
public class Repeater : MonoBehaviour
{
    private int _count;
    private int _last;
    public int Count => _count;
    public int Last => _last;
    public void Apply() { _count++; }
    public void Apply(int value) { _count++; _last = value; }
}

// A user subclass of a generic UnityEvent, the shape a discovery predicate must recognize even
// though its serialized `SerializedProperty.type` reports the subclass name, not "UnityEvent`1".
[Serializable]
public class MyEvent : UnityEvent<int>
{
}

// A listener host carrying every non-Button serialized UnityEvent shape: the plain form, the
// single-generic form, the two-generic form, and a user subclass of a generic form.
public class Pinger : MonoBehaviour
{
    public UnityEvent onPinged;
    public UnityEvent<float> onLevelChanged;
    public UnityEvent<int, float> onBothChanged;
    public MyEvent onCounted;

    private int _pings;
    private float _lastLevel;
    private float _lastAmount;
    public int Pings => _pings;
    public float LastLevel => _lastLevel;
    public float LastAmount => _lastAmount;

    public void Ping() { _pings++; }
    public void SetBoth(int level, float amount) { _lastLevel = level; _lastAmount = amount; }
}
