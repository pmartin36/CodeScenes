# Spec 47: the typed member-selector resolves the component's REAL serialized names

One defect, observed live in a one-shot authoring run (SceneBuilderOneShot, Claude session
`a6792c4f`). The typed member-selector form `.Set(r => r.field, value)` resolves the serialized path
by a FIXED `field -> m_Field` camelCase→PascalCase mangling and fails loud, aborting the whole build,
whenever Unity's actual serialized name for that field is spelled any other way. The author burned ~6
build cycles rediscovering, one component at a time, that Unity's serialized names do not follow the
one shape the resolver guesses, then wrote a `SerializedObject` iterator probe by hand to dump the real
names. The resolver already holds exactly such a probe and throws it away for a string guess; the fix
is to match the author's member against the probe's ACTUAL property names instead of fabricating one.

## The measured defect

`AuthoredPathResolver.ResolvePath` (`com.codescenes/Editor/AuthoredPathResolver.cs:319-335`) is the sole
owner that turns a parsed `member:<name>` key into a serialized `propertyPath`. It tries exactly two
candidates and then throws:

```csharp
// AuthoredPathResolver.cs:319-335
private static string ResolvePath(SerializedObject so, string member, string typeFullName)
{
    if (so.FindProperty(member) != null)
        return member;

    var mangled = "m_" + char.ToUpperInvariant(member[0]) + member.Substring(1);
    if (so.FindProperty(mangled) != null)
        return mangled;

    throw new InvalidOperationException(
        $"[SceneBuilder] Cannot resolve authored member '{member}' to a serialized path on '{typeFullName}'. " +
        "Use the raw .Set(\"m_Path\", value) form.");
}
```

The mangling at `:326` can only ever produce `m_` + an upper-cased first letter + the rest verbatim. It
structurally cannot produce a name with a space, a name whose first post-`m_` letter is lowercase, or a
single member that Unity splits across two serialized fields. Unity's real names do all three. Verbatim
Console failures from the run, each aborting the build:

- `Cannot resolve authored member 'nearClipPlane' to a serialized path on 'UnityEngine.Camera'` — the
  real serialized names are the legacy space-delimited `near clip plane` / `far clip plane` /
  `field of view`; `m_NearClipPlane` matches none.
- `Cannot resolve authored member 'fontSize' ... on 'TMPro.TextMeshProUGUI'` — the real name is
  `m_fontSize` (lowercase `f`); the mangling produces `m_FontSize`.
- `alignment` on `TextMeshProUGUI` — no single serialized field exists; Unity splits it into
  `m_HorizontalAlignment` and `m_VerticalAlignment`.
- Enum fields whose serialized name also differs from the mangling: `Rigidbody.collisionDetectionMode`
  (real `m_CollisionDetection`) and `Rigidbody.interpolation` (real `m_Interpolate`) both miss on the
  path and abort. `Light.type` (`m_Type`) and `Image.fillMethod` (`m_FillMethod`) mangle to a name that
  DOES exist, but the value then had to be forced to the raw int form; the author reported
  `type is not a supported int value` and fell back to `.Set("m_...", (int)...)` for the whole group.

The reason the resolver cannot know any of these: it holds a live probe `SerializedObject so` (built in
`GetProbe`, `:131-160`) that reports Unity's exact property names via `FindProperty`, but it only ever
ASKS that probe about two names it invented. The one existing reflection mechanism,
`SerializedMemberMap` (`com.codescenes/Editor/SerializedMemberMap.cs`), is built from
`declaringType.GetFields()` (`:272`) and so is blind to native serialized fields with no managed C#
field behind them (`Camera`'s spaced names, TMP's alignment split) — it cannot substitute for the live
probe here.

## The fix

**Owner.** `AuthoredPathResolver.ResolvePath` (`com.codescenes/Editor/AuthoredPathResolver.cs:319`). It
is the single stage every `member:<name>` key passes through, in both sync directions, for ordinary
component fields (via `ResolveComponent`, `:86-111`) AND for prefab instance property overrides (via
`NormalizeOverridePath`, `:304-314`). Fixing it here fixes every current and future caller by default;
no call site opts in.

**The single shared mechanism: match the member against the probe's ACTUAL properties.** Replace the
fixed two-candidate string guess with an enumeration of the live probe `SerializedObject` (which the
method already holds) — `so.GetIterator()` walked with `NextVisible`, the same read Unity's own
inspector uses and the same iteration the author performed by hand. Match the author's member against
the real property names in this order, taking the first hit:

1. An EXACT `propertyPath` match (`nearClipPlane` on a user MonoBehaviour whose serialized name is the
   field name itself — the current first branch, preserved).
2. The managed-field spelling from reflection where one exists: honor `[SerializeField]` private-field
   names and `[FormerlySerializedAs]` aliases, reusing `SerializedMemberMap`'s public↔serialized map so
   a private `[SerializeField] m_foo` authored as `foo` resolves to `m_foo`.
3. A case-insensitive match of the member against the probe's real property names, comparing against
   both the raw serialized name and its de-`m_`/de-space normalization, so `fontSize`→`m_fontSize`,
   `collisionDetectionMode`→`m_CollisionDetection`, `interpolation`→`m_Interpolate`, and
   `nearClipPlane`→`near clip plane` all resolve from the probe's own truth rather than a guess.

Where the matched property is enum-typed (`SerializedPropertyType.Enum`), the resolved serialized path
carries the value through the existing enum→int lowering already owned by
`SerializedMemberMap.ResolveEnumType` + `SerializedFieldBridge.WriteEnum`
(`com.codescenes/Editor/SerializedFieldBridge.cs:551`), so an enum-typed typed-selector writes the
serialized int without the author restating it as a raw `(int)` cast. A member that Unity splits across
two serialized fields (TMP `alignment`) is out of scope for a single-path resolve and stays a located
failure that names the two real fields (see Out of scope).

**The invariant and the check that fails on bypass.** ResolvePath returns ONLY a string `p` for which
`so.FindProperty(p) != null` on the probe, or it throws the located error. The mechanism enforcing this
is that every candidate is produced BY enumerating the probe (or verified against it) rather than
concatenated, so a return value that does not exist on the component is unreachable by construction. A
regression that reintroduces a fabricated path — any name the probe does not report — cannot slip
through, because the return is gated on the probe reporting it. On a genuine miss (a member that maps to
no real property) the located throw is retained, unchanged in shape.

## Accept when

An EditMode regression test in `unity-gate/Assets/GateTests/` authors each of the following through the
TYPED-selector form `.Set(r => r.<member>, value)` against a live editor component, runs the real build,
and asserts it resolves and applies with zero Console errors (proven by the actual run, not a mock):

- `Camera`: `nearClipPlane`, `fieldOfView` (resolve to the spaced serialized names).
- `TextMeshProUGUI`: `fontSize` (resolves to `m_fontSize`).
- `Light`: `type` (enum resolves and the enum value writes without a raw `(int)`).
- `Image`: `fillMethod` (enum resolves and writes).
- `Rigidbody`: `collisionDetectionMode`, `interpolation` (resolve to `m_CollisionDetection` /
  `m_Interpolate`, enum values write).

The same test asserts a member that maps to NO real serialized property still throws the located
`Cannot resolve authored member ...` error (the guess is gone, the located failure is not), and that the
error for a split member (TMP `alignment`) names the real `m_HorizontalAlignment` / `m_VerticalAlignment`
fields so the author is not left to discover them by hand. A prior-behavior guard fails if ResolvePath
ever returns a path absent from the probe `SerializedObject`.

## Out of scope

- A single member that Unity backs with TWO serialized fields (`TextMeshProUGUI.alignment` →
  `m_HorizontalAlignment` + `m_VerticalAlignment`). One typed selector cannot address two paths; this
  spec only requires the failure be LOCATED and name both real fields, not that it auto-splits. A
  splitting authoring verb, if wanted, is a separate item.
- The scene→code emitter's choice of which selector form to write. This spec is the code→scene resolve
  direction only.
- Any change to `SerializedMemberMap`'s reflection ladder or to `SerializedFieldBridge.WriteEnum`; both
  are reused as-is once ResolvePath hands them the correct serialized path.
