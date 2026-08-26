# CodeScenes authoring API

Every type and member you can use inside `ISceneDefinition.Build` (and `IPrefabDefinition.Build`). Namespace `SceneBuilder.Authoring`, assembly `SceneBuilder.Authoring`. Generated from the C# source by SceneBuilder.DocGen.

## AlignSpace

```csharp
public enum AlignSpace
```

The reference frame an AlignTo alignment resolves edges and centers in: the target's own local axes, or world axes.

- `TargetLocal`
- `World`

## AlignTo

```csharp
public sealed class AlignTo : MonoBehaviour, IPositionDriver
```

Editor-time (and play-mode-guarded) alignment. Drives `transform.position` on the set axes so a sibling [Renderer](https://docs.unity3d.com/ScriptReference/Renderer.html)'s bounds land against a resolved surface — an explicit `target`'s extent (abut/align, per axis), or (with no target) a raycast hit / collider-less fallback scan — independent of the object's own pivot.

Add it from a builder with `NodeHandle.AlignTo` or in the inspector. It runs in edit mode only, and re-snaps when the target/frame underneath moves. Drag the object further than `captureThreshold` to detach it deliberately. Axes you leave unset are never touched, so an object can align down to a floor while staying free to move horizontally. Each axis resolves in the target's local space by default, a `frame` override's local space, or world space (`AlignSpace.World`).

### AlignTo.xMode

```csharp
public Mode xMode
```

### AlignTo.yMode

```csharp
public Mode yMode
```

### AlignTo.zMode

```csharp
public Mode zMode
```

### AlignTo.xOffset

```csharp
public float xOffset
```

### AlignTo.yOffset

```csharp
public float yOffset
```

### AlignTo.zOffset

```csharp
public float zOffset
```

### AlignTo.target

```csharp
public Transform target
```

### AlignTo.frame

Overrides which transform's local axes an alignment resolves in (default: the target's own local axes). Ignored when `space` is `AlignSpace.World`.

```csharp
public Transform frame
```

### AlignTo.space

The reference frame each axis resolves in: the target's (or `frame`'s) local axes, or world axes.

```csharp
public AlignSpace space
```

### AlignTo.captureThreshold

World-unit drag distance (measured on aligned axes only) beyond which a manual move is treated as an intentional detach rather than a re-align. Sticky: once detached the component disables itself (see `Evaluate`) until re-enabled.

```csharp
public float captureThreshold
```

### AlignTo.Evaluate

Recompute the position of each set axis so the corresponding bounds feature lands against the resolved surface for that axis (target extent alignment, or — with no target — raycast > collider-less fallback scan, Abut modes only). Free (unset) axes are left untouched.

```csharp
public void Evaluate()
```

## Mode

```csharp
public enum Mode
```

- `None`
- `AbutMin`
- `AbutMax`
- `AlignMin`
- `AlignMax`
- `AlignCenter`

## AssetReference

```csharp
public sealed class AssetReference
```

The value produced by the `AssetRefs.Asset`, `AssetRefs.Asset`, `AssetRefs.Builtin`, or `AssetRefs.Builtin` authoring factories — an asset reference authored by either a readable project path (e.g. `Asset("Assets/Materials/Red.mat")`), a sub-object of an imported project asset (e.g. `Asset("Assets/Models/Barrel.fbx", "BarrelMesh")`), or the name of a Unity built-in resource (e.g. `Builtin("Cube")`).

Compile-time scaffolding only. SceneBuilder parses the builder SOURCE TEXT (it never runs the builder), resolving the authored path/name to the asset's GUID at build time; this object carries no runtime state. A cleared/None reference is authored as `Asset(null)`.

### AssetReference.As

This asset reference, typed as T so it can be written as a UnityEvent listener's object argument (e.g. `m.SetMaterial(Assets.Materials.Red.As<UnityEngine.Material>())`). Compile-time cast scaffolding only — it performs no work at runtime.

```csharp
public T As<T>()
```

## AssetRefs

```csharp
public static class AssetRefs
```

The `Asset(displayPath[, subAssetName])` and `Builtin(name[, typeHint])` authoring factories. Bring them into scope with `using static SceneBuilder.Authoring.AssetRefs;` and reference an asset from any serialized asset field: `c.Set("m_Materials", new[] { Asset("Assets/Materials/Red.mat") })`, `c.Set("m_Mesh", Asset("Assets/Models/Barrel.fbx", "BarrelMesh"))`, or `c.Set("m_Mesh", Builtin("Cube"))`. Author a cleared field with `Asset(null)`.

Compile-time scaffolding only — the parser reads the source text, so these return an inert handle and perform no work at runtime.

### AssetRefs.Asset

Reference the project asset at displayPath (resolved to its GUID at build time). Pass `null` to author a cleared / None reference.

```csharp
public static AssetReference Asset(string displayPath)
```

- `displayPath` (`string`)

```csharp
public static AssetReference Asset(string displayPath, string subAssetName)
```

Reference the sub-object named subAssetName inside the imported project asset at displayPath (e.g. a Mesh inside an FBX, a sub-material, a sliced Sprite), resolved to the sub-object's GUID + local file identifier at build time.

- `displayPath` (`string`)
- `subAssetName` (`string`)

### AssetRefs.Builtin

Reference the Unity built-in resource named name (resolved from the editor's built-in resource containers at build time).

```csharp
public static AssetReference Builtin(string name)
```

- `name` (`string`)

```csharp
public static AssetReference Builtin(string name, string typeHint)
```

Reference the Unity built-in resource named name, qualified by typeHint (the concrete type name, e.g. `"Sprite"`) to disambiguate names shared by more than one built-in object.

- `name` (`string`)
- `typeHint` (`string`)

## AxisAlign

```csharp
public readonly struct AxisAlign
```

A single-axis alignment mode for AlignTo: which edge or center of self lands on which reference plane of the target, plus an optional world-unit offset applied after alignment. UnityEngine-free so it can appear in generated authoring source without pulling in the runtime component.

### AxisAlign.None

No alignment on this axis.

```csharp
public static readonly AxisAlign None
```

### AxisAlign.AbutMin

Self's maximum-side edge abuts the target's minimum-side edge (self sits outside, on the target's min side).

```csharp
public static readonly AxisAlign AbutMin
```

### AxisAlign.AbutMax

Self's minimum-side edge abuts the target's maximum-side edge (self sits outside, on the target's max side).

```csharp
public static readonly AxisAlign AbutMax
```

### AxisAlign.AlignMin

Self's minimum-side edge is flush with the target's minimum-side edge (near faces coincide). Requires a target.

```csharp
public static readonly AxisAlign AlignMin
```

### AxisAlign.AlignMax

Self's maximum-side edge is flush with the target's maximum-side edge (far faces coincide). Requires a target.

```csharp
public static readonly AxisAlign AlignMax
```

### AxisAlign.AlignCenter

Self's center coincides with the target's center. Requires a target.

```csharp
public static readonly AxisAlign AlignCenter
```

### AxisAlign.Offset

Returns a copy of this alignment carrying an additional world-unit offset applied after the alignment is resolved.

```csharp
public AxisAlign Offset(float worldUnits)
```

- `worldUnits` (`float`)

## Between

```csharp
public sealed partial class Between : MonoBehaviour, IPositionDriver
```

Editor-time (and play-mode-guarded) single-axis corridor placement. Drives `transform.position` along one axis so a sibling [Renderer](https://docs.unity3d.com/ScriptReference/Renderer.html)'s bounds land flush against `from` at fraction 0 and flush against `to` at fraction 1, independent of the object's own pivot.

### Between.from

```csharp
public Transform from
```

### Between.to

```csharp
public Transform to
```

### Between.orientation

```csharp
public Transform orientation
```

### Between.fraction

```csharp
public float fraction
```

### Between.axis

```csharp
public Axis axis
```

### Between.Evaluate

Recompute `transform.position` along the configured axis so self's bounds land flush against `from` at fraction 0 and flush against `to` at fraction 1 (unclamped past either end). The axis is a world direction by default, or the local axis of `orientation` when set. Only the position component along that axis is touched.

```csharp
public void Evaluate()
```

## Axis

```csharp
public enum Axis
```

- `X`
- `Y`
- `Z`

## ComponentHandle<T>

```csharp
public sealed partial class ComponentHandle<T>
```

A handle to a component being authored on a GameObject (via `NodeHandle.Component`). Set serialized fields either by serialized property path (`c.Set("m_Mass", 5f)`) or by a typed member selector (`c.Set(r => r.mass, 5f)`).

Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so these methods return handles for chaining but perform no work at runtime.

### ComponentHandle.Set

Set a serialized field by its serialized property path (e.g. "m_Mass").

```csharp
public ComponentHandle<T> Set(string serializedPath, object value)
```

- `serializedPath` (`string`)
- `value` (`object`)

```csharp
public ComponentHandle<T> Set<TValue>(Func<T, TValue> selector, TValue value)
```

Set a field by typed member selector (e.g. `r => r.mass`).

- `selector` (`Func<T, TValue>`)
- `value` (`TValue`)

```csharp
public ComponentHandle<T> Set<TValue>(Func<T, TValue> selector, AssetReference asset)
```

Set an asset-reference field by typed member selector, e.g. `c.Set(r => r.sharedMaterial, Asset("Assets/Materials/Red.mat"))`. The selector's return type is the asset type; the value is the `AssetReference` factory result.

- `selector` (`Func<T, TValue>`)
- `asset` (`AssetReference`)

```csharp
public ComponentHandle<T> Set<TValue>(Func<T, TValue> selector, SceneObjectHandle target)
```

Set a cross-object-reference field by typed member selector, e.g. `c.Set(r => r.target, door)`. The target is any scene object handle — a GameObject or a prefab instance root. Pass `NodeHandle.None` to clear the slot.

- `selector` (`Func<T, TValue>`)
- `target` (`SceneObjectHandle`)

### ComponentHandle.OnClick

Wire a persistent listener onto this component's `OnClick` UnityEvent (`m_OnClick`): when it fires, call method on target. The method is a typed lambda on the target component, so a method it does not have fails to compile. The lambda's own call carries the static argument, if any (e.g. `x => x.SetLevel(3)`); a zero-argument call wires a void listener.

```csharp
public ComponentHandle<T> OnClick<TTarget>(ComponentRef<TTarget> target, Action<TTarget> method)
```

- `target` (`ComponentRef<TTarget>`)
- `method` (`Action<TTarget>`)

```csharp
public ComponentHandle<T> OnClick<TTarget>(ComponentRef<TTarget> target, Action<TTarget> method, UnityEngine.Events.UnityEventCallState callState)
```

Wire a persistent listener onto this component's `OnClick` UnityEvent, with an explicit callState governing whether it fires in edit mode, play mode, or both. Omitting this argument defaults to [RuntimeOnly](https://docs.unity3d.com/ScriptReference/RuntimeOnly.html).

- `target` (`ComponentRef<TTarget>`)
- `method` (`Action<TTarget>`)
- `callState` (`UnityEngine.Events.UnityEventCallState`)

```csharp
public ComponentHandle<T> OnClick<TTarget>(AssetReference target, Action<TTarget> method)
```

Wire a persistent listener whose target is a project asset (resolved through the typed asset catalog, e.g. `Assets.Prefabs.Speakers.Speaker`) rather than a scene reference. TTarget is not inferable from an asset reference, so the call site supplies it explicitly (e.g. `b.OnClick<AudioSource>(Assets.Prefabs.Speaker, a => a.Play())`).

- `target` (`AssetReference`)
- `method` (`Action<TTarget>`)

```csharp
public ComponentHandle<T> OnClick<TTarget>(AssetReference target, Action<TTarget> method, UnityEngine.Events.UnityEventCallState callState)
```

The asset-target form of `OnClick` with an explicit callState.

- `target` (`AssetReference`)
- `method` (`Action<TTarget>`)
- `callState` (`UnityEngine.Events.UnityEventCallState`)

```csharp
public ComponentHandle<T> OnClick(SceneObjectHandle target, Action<UnityEngine.GameObject> method)
```

Wire a persistent listener whose target is a GameObject (a scene node, not one of its components) — the shape a `Button.onClick => GameObject.SetActive(false)` wiring takes.

- `target` (`SceneObjectHandle`)
- `method` (`Action<UnityEngine.GameObject>`)

```csharp
public ComponentHandle<T> OnClick(SceneObjectHandle target, Action<UnityEngine.GameObject> method, UnityEngine.Events.UnityEventCallState callState)
```

The GameObject-target form of `OnClick` with an explicit callState.

- `target` (`SceneObjectHandle`)
- `method` (`Action<UnityEngine.GameObject>`)
- `callState` (`UnityEngine.Events.UnityEventCallState`)

### ComponentHandle.OnEvent

Wire a persistent listener onto an arbitrary `UnityEvent`-typed field selected by unityEvent (e.g. `b.OnEvent(x => x.onValueChanged, target, t => t.Method())`), rather than the fixed `OnClick` shortcut. TEvent is unconstrained, so any `UnityEvent`-typed field on T is authorable this way.

```csharp
public ComponentHandle<T> OnEvent<TEvent, TTarget>(Func<T, TEvent> unityEvent, ComponentRef<TTarget> target, Action<TTarget> method)
```

- `unityEvent` (`Func<T, TEvent>`)
- `target` (`ComponentRef<TTarget>`)
- `method` (`Action<TTarget>`)

```csharp
public ComponentHandle<T> OnEvent<TEvent, TTarget>(Func<T, TEvent> unityEvent, ComponentRef<TTarget> target, Action<TTarget> method, UnityEngine.Events.UnityEventCallState callState)
```

The component-target form of `OnEvent` with an explicit callState.

- `unityEvent` (`Func<T, TEvent>`)
- `target` (`ComponentRef<TTarget>`)
- `method` (`Action<TTarget>`)
- `callState` (`UnityEngine.Events.UnityEventCallState`)

```csharp
public ComponentHandle<T> OnEvent<TEvent, TTarget>(Func<T, TEvent> unityEvent, AssetReference target, Action<TTarget> method)
```

The asset-target form of `OnEvent`: wires the selected event field to a method on an asset reference rather than a scene component.

- `unityEvent` (`Func<T, TEvent>`)
- `target` (`AssetReference`)
- `method` (`Action<TTarget>`)

```csharp
public ComponentHandle<T> OnEvent<TEvent>(Func<T, TEvent> unityEvent, SceneObjectHandle target, Action<UnityEngine.GameObject> method)
```

The GameObject-target form of `OnEvent`: wires the selected event field to a method on the target GameObject itself (e.g. `SetActive`).

- `unityEvent` (`Func<T, TEvent>`)
- `target` (`SceneObjectHandle`)
- `method` (`Action<UnityEngine.GameObject>`)

```csharp
public ComponentHandle<T> OnEvent<TArg, TTarget>(Func<T, UnityEngine.Events.UnityEvent<TArg>> unityEvent, ComponentRef<TTarget> target, Func<TTarget, Action<TArg>> method, bool dynamic)
```

Wire a "dynamic" persistent listener: the argument the event fires with is passed straight through to method at runtime, rather than a static value baked in at authoring time. method is a method GROUP on the target (e.g. `h => h.SetValue`, not `h => h.SetValue(...)`) whose parameter type is TArg — the selected event's own generic argument.

- `unityEvent` (`Func<T, UnityEngine.Events.UnityEvent<TArg>>`)
- `target` (`ComponentRef<TTarget>`)
- `method` (`Func<TTarget, Action<TArg>>`)
- `dynamic` (`bool`)

```csharp
public ComponentHandle<T> OnEvent<TArg0, TArg1, TTarget>(Func<T, UnityEngine.Events.UnityEvent<TArg0, TArg1>> unityEvent, ComponentRef<TTarget> target, Func<TTarget, Action<TArg0, TArg1>> method, bool dynamic)
```

The two-argument-event form of `OnEvent`.

- `unityEvent` (`Func<T, UnityEngine.Events.UnityEvent<TArg0, TArg1>>`)
- `target` (`ComponentRef<TTarget>`)
- `method` (`Func<TTarget, Action<TArg0, TArg1>>`)
- `dynamic` (`bool`)

### ComponentHandle.SetRef

Set a `[SerializeReference]` polymorphic field by typed member selector, e.g. `c.SetRef(r => r.strategy, new Aggressive { range = 5f })`. The selector fixes TField to the field's declared interface/abstract/base type; any assignable concrete instance, or `null` to clear the reference, may be passed.

```csharp
public ComponentHandle<T> SetRef<TField>(Func<T, TField> selector, TField value)
```

- `selector` (`Func<T, TField>`)
- `value` (`TField`)

## ComponentRef<T>

```csharp
public sealed class ComponentRef<T>
```

A reference to an already-declared component, captured with `NodeHandle.Ref` and passed as a UnityEvent listener target. Reference-only: it exposes no configuration members, so a component can only be configured inside its own `Component<T>(c => ...)` closure.

Compile-time scaffolding only — SceneBuilder parses the source text.

### ComponentRef.As

This component reference, typed as T so it can be written as a UnityEvent listener's object argument (e.g. `hud.Bind(opener.As())`). Compile-time cast scaffolding only — it does not select a different component and performs no work at runtime.

```csharp
public T As()
```

## FitSize

```csharp
public sealed class FitSize : MonoBehaviour
```

Editor-time (and play-mode-guarded) world-size solver. Drives `transform.localScale` from a sibling [MeshFilter](https://docs.unity3d.com/ScriptReference/MeshFilter.html)'s local bounds so an authored width/height/depth (aspect-locked) or explicit per-axis `size` becomes an exact WORLD size, independent of the mesh's native dimensions, rotation, or a scaled parent.

Add it from a builder with `NodeHandle.FitSize` or in the inspector. It runs in edit mode only. Resize the object by hand and it reads the new world size back into `value` / `size`, so the size you drag to is the size your builder file ends up saying.

### FitSize.mode

```csharp
public Mode mode
```

### FitSize.value

The single authored aspect-locked dimension when `mode` is Width/Height/Depth. Unused (and unwritten) for Explicit/None.

```csharp
public float value
```

### FitSize.size

Explicit per-axis world size when `mode` is Explicit.

```csharp
public Vector3 size
```

### FitSize.Evaluate

Recompute `localScale` from the current mesh bounds / intent, or (if the user manually changed `localScale` since our last write) back-solve the intent field(s) from the new world size instead.

```csharp
public void Evaluate()
```

## Mode

```csharp
public enum Mode
```

Which dimension drives the size. `None` is the default and drives nothing, so a freshly added FitSize leaves the object alone until you pick a mode.

- `None`
- `Width`
- `Height`
- `Depth`
- `Explicit`

## IPrefabDefinition

```csharp
public interface IPrefabDefinition
```

A Unity prefab defined in code. SceneBuilder parses the `Build` method (it does not execute it) and materializes the described prefab, then keeps code and prefab in sync. A prefab defines exactly one root object.

### IPrefabDefinition.Build

```csharp
void Build(PrefabRoot root)
```

- `root` (`PrefabRoot`)

## IPrefabVariantDefinition

```csharp
public interface IPrefabVariantDefinition
```

A prefab variant defined in code: a base prefab plus a set of root-level overrides, authored without re-declaring the base prefab's hierarchy. SceneBuilder parses the `Build` method (it does not execute it) and materializes the variant as a single prefab-instance root over `Base`, then keeps code and prefab in sync.

### IPrefabVariantDefinition.Base

The base prefab this variant overrides, e.g. `Prefabs.Tank`.

```csharp
PrefabRef Base { get; }
```

### IPrefabVariantDefinition.Build

Authors the variant's root-level overrides onto root.

```csharp
void Build(VariantRoot root)
```

- `root` (`VariantRoot`)

## ISceneDefinition

```csharp
public interface ISceneDefinition
```

A Unity scene defined in code. SceneBuilder parses the `Build` method (it does not execute it) and materializes the described scene, then keeps code and scene in sync.

### ISceneDefinition.Build

```csharp
void Build(SceneRoot scene)
```

- `scene` (`SceneRoot`)

## InstanceHandle

```csharp
public class InstanceHandle : SceneObjectHandle
```

A handle to a prefab instance in a scene definition, returned by `SceneRoot.Instance` / `NodeHandle.Instance`. Chain calls to configure the instance's root transform and nest plain children.

Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so these methods return handles for chaining but perform no work at runtime.

A prefab instance is authored as one whole unit: you do not author child GameObjects inside its hierarchy. Change what the instance carries with `Override`, `AddComponent` (or its `Component` alias) and `RemoveComponent`, and reach a nested target with `On`.

### InstanceHandle.Transform

Set the local transform. Rotation is authored in Euler degrees.

```csharp
public InstanceHandle Transform((float x, float y, float z)? pos = null, (float x, float y, float z)? rot = null, (float x, float y, float z)? scale = null)
```

- `pos` (`(float x, float y, float z)?`)
- `rot` (`(float x, float y, float z)?`)
- `scale` (`(float x, float y, float z)?`)

### InstanceHandle.Id

Assign an explicit, stable logical id (otherwise one is derived).

```csharp
public InstanceHandle Id(string id)
```

- `id` (`string`)

### InstanceHandle.Add

Add a plain child GameObject alongside the instance's hierarchy.

```csharp
public NodeHandle Add(string name)
```

- `name` (`string`)

```csharp
public NodeHandle Add(string name, Action<NodeHandle> configure)
```

Add a plain child GameObject and configure it in a closure.

- `name` (`string`)
- `configure` (`Action<NodeHandle>`)

### InstanceHandle.Override

Author property overrides on the instance root's own components, e.g. `.Override(e => e.Set((Health x) => x.health, 50))`. Targets are the instance root only — nested targets (including inside a nested prefab) are not authored here.

```csharp
public InstanceHandle Override(Action<OverrideHandle> configure)
```

- `configure` (`Action<OverrideHandle>`)

### InstanceHandle.AddComponent

Add a component of type T to the instance root with no field overrides.

```csharp
public InstanceHandle AddComponent<T>()
```

```csharp
public InstanceHandle AddComponent<T>(Action<ComponentHandle<T>> configure)
```

Add a component of type T to the instance root and set its serialized fields in a closure.

- `configure` (`Action<ComponentHandle<T>>`)

### InstanceHandle.Component

Alias for `AddComponent`.

```csharp
public InstanceHandle Component<T>()
```

```csharp
public InstanceHandle Component<T>(Action<ComponentHandle<T>> configure)
```

- `configure` (`Action<ComponentHandle<T>>`)

### InstanceHandle.RemoveComponent

Remove a component of type T from the instance root (must exist on the source prefab).

```csharp
public InstanceHandle RemoveComponent<T>()
```

### InstanceHandle.On

Author overrides scoped to a nested target inside the instance's hierarchy, addressed by child path (e.g. `"Turret/Barrel"`). Fallback for the typed selector overload on `InstanceHandle<TRef>` when no generated ref type is available.

```csharp
public InstanceHandle On(string childPath, Action<ScopedHandle> closure)
```

- `childPath` (`string`)
- `closure` (`Action<ScopedHandle>`)

### InstanceHandle.AddChild

Add a new child GameObject nested under parentPath in the instance's hierarchy (`""` = the instance root).

```csharp
public NodeHandle AddChild(string parentPath, string name)
```

- `parentPath` (`string`)
- `name` (`string`)

```csharp
public NodeHandle AddChild(string parentPath, string name, Action<NodeHandle> configure)
```

Add a new child GameObject nested under parentPath and configure it in a closure (the full `NodeHandle` authoring surface).

- `parentPath` (`string`)
- `name` (`string`)
- `configure` (`Action<NodeHandle>`)

### InstanceHandle.RemoveChild

Remove an existing child GameObject at childPath from the instance's hierarchy.

```csharp
public InstanceHandle RemoveChild(string childPath)
```

- `childPath` (`string`)

## InstanceHandle<TRef>

```csharp
public sealed class InstanceHandle<TRef> : InstanceHandle where TRef : PrefabRef
```

A typed handle to a prefab instance, returned by `SceneRoot.Instance`. Carries the generated ref type TRef through the chain so `On`'s selector can be type-checked against the instance's actual nested hierarchy.

Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so these methods return handles for chaining but perform no work at runtime.

### InstanceHandle.Transform

Set the local transform. Rotation is authored in Euler degrees.

```csharp
public new InstanceHandle<TRef> Transform((float x, float y, float z)? pos = null, (float x, float y, float z)? rot = null, (float x, float y, float z)? scale = null)
```

- `pos` (`(float x, float y, float z)?`)
- `rot` (`(float x, float y, float z)?`)
- `scale` (`(float x, float y, float z)?`)

### InstanceHandle.Id

Assign an explicit, stable logical id (otherwise one is derived).

```csharp
public new InstanceHandle<TRef> Id(string id)
```

- `id` (`string`)

### InstanceHandle.Override

Author property overrides on the instance root's own components, e.g. `.Override(e => e.Set((Health x) => x.health, 50))`. Targets are the instance root only — nested targets (including inside a nested prefab) are not authored here.

```csharp
public new InstanceHandle<TRef> Override(Action<OverrideHandle> configure)
```

- `configure` (`Action<OverrideHandle>`)

### InstanceHandle.AddComponent

Add a component of type T to the instance root with no field overrides.

```csharp
public new InstanceHandle<TRef> AddComponent<T>()
```

```csharp
public new InstanceHandle<TRef> AddComponent<T>(Action<ComponentHandle<T>> configure)
```

Add a component of type T to the instance root and set its serialized fields in a closure.

- `configure` (`Action<ComponentHandle<T>>`)

### InstanceHandle.Component

Alias for `AddComponent`.

```csharp
public new InstanceHandle<TRef> Component<T>()
```

```csharp
public new InstanceHandle<TRef> Component<T>(Action<ComponentHandle<T>> configure)
```

- `configure` (`Action<ComponentHandle<T>>`)

### InstanceHandle.RemoveComponent

Remove a component of type T from the instance root (must exist on the source prefab).

```csharp
public new InstanceHandle<TRef> RemoveComponent<T>()
```

### InstanceHandle.On

Author overrides scoped to a nested target inside the instance's hierarchy, addressed by a compiler-checked member selector (e.g. `t => t.Turret.Barrel`). TNode is inferred from the selector's leaf property type.

```csharp
public InstanceHandle<TRef> On<TNode>(Func<TRef, TNode> selector, Action<ScopedHandle> closure)
```

- `selector` (`Func<TRef, TNode>`)
- `closure` (`Action<ScopedHandle>`)

```csharp
public new InstanceHandle<TRef> On(string childPath, Action<ScopedHandle> closure)
```

String fallback that keeps TRef for chaining continuity.

- `childPath` (`string`)
- `closure` (`Action<ScopedHandle>`)

### InstanceHandle.RemoveChild

Remove an existing source child GameObject addressed by a compiler-checked member selector (e.g. `t => t.LeftTurret.Antenna`). A typo or a stale name is a COMPILE error, and a hierarchy rename auto-syncs the accessor — exactly like `On`. Use the string overload for a child with no generated ref type.

```csharp
public InstanceHandle<TRef> RemoveChild<TNode>(Func<TRef, TNode> selector)
```

- `selector` (`Func<TRef, TNode>`)

```csharp
public new InstanceHandle<TRef> RemoveChild(string childPath)
```

Remove an existing child GameObject at childPath from the instance's hierarchy.

- `childPath` (`string`)

### InstanceHandle.AddChild

Add a new child GameObject nested under a compiler-checked parent selector (e.g. `t => t.LeftTurret`). The PARENT is façade-checked (typo/stale = compile error, rename auto-syncs); the new child name stays a string — it is a genuinely new object with no façade type yet.

```csharp
public NodeHandle AddChild<TNode>(Func<TRef, TNode> parentSelector, string name)
```

- `parentSelector` (`Func<TRef, TNode>`)
- `name` (`string`)

```csharp
public NodeHandle AddChild<TNode>(Func<TRef, TNode> parentSelector, string name, Action<NodeHandle> configure)
```

Add a new child GameObject under a compiler-checked parent selector and configure it in a closure (the full `NodeHandle` authoring surface).

- `parentSelector` (`Func<TRef, TNode>`)
- `name` (`string`)
- `configure` (`Action<NodeHandle>`)

## NodeHandle

```csharp
public sealed class NodeHandle : SceneObjectHandle
```

A handle to a GameObject in a scene definition. Chain calls to configure it and nest children.

Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so these methods return handles for chaining but perform no work at runtime.

### NodeHandle.None

Assign this to a reference field to clear it, leaving the slot empty in the scene.

```csharp
public static readonly NodeHandle None
```

### NodeHandle.Self

Assign this to a reference field to point it at the GameObject the component is on — a Button's target graphic on its own Image, for example. It needs no variable, so it is legal inside the statement that declares the node.

```csharp
public static readonly NodeHandle Self
```

### NodeHandle.Add

Add a child GameObject.

```csharp
public NodeHandle Add(string name)
```

- `name` (`string`)

```csharp
public NodeHandle Add(string name, Action<NodeHandle> configure)
```

Add a child GameObject and configure it in a closure.

- `name` (`string`)
- `configure` (`Action<NodeHandle>`)

### NodeHandle.Transform

Set the local transform. Rotation is authored in Euler degrees.

```csharp
public NodeHandle Transform((float x, float y, float z)? pos = null, (float x, float y, float z)? rot = null, (float x, float y, float z)? scale = null)
```

- `pos` (`(float x, float y, float z)?`)
- `rot` (`(float x, float y, float z)?`)
- `scale` (`(float x, float y, float z)?`)

### NodeHandle.RectTransform

Set the UI layout (RectTransform). Presence of this call marks the node as a RectTransform node; omitted arguments stay at Unity's RectTransform defaults.

```csharp
public NodeHandle RectTransform((float x, float y)? anchoredPos = null, (float x, float y)? sizeDelta = null, (float x, float y)? anchorMin = null, (float x, float y)? anchorMax = null, (float x, float y)? pivot = null)
```

- `anchoredPos` (`(float x, float y)?`)
- `sizeDelta` (`(float x, float y)?`)
- `anchorMin` (`(float x, float y)?`)
- `anchorMax` (`(float x, float y)?`)
- `pivot` (`(float x, float y)?`)

### NodeHandle.FitSize

Aspect-locked world size: exactly one of width/height/ depth drives the size, aspect preserved on the other two axes.

```csharp
public NodeHandle FitSize(float? width = null, float? height = null, float? depth = null)
```

- `width` (`float?`)
- `height` (`float?`)
- `depth` (`float?`)

```csharp
public NodeHandle FitSize((float x, float y, float z) size)
```

Explicit per-axis world size (non-uniform allowed).

- `size` (`(float x, float y, float z)`)

### NodeHandle.AlignTo

Align to target's extent on any of x/y/ z: `AxisAlign.AbutMin` lands this object's max face against the target's min face (and `AxisAlign.AbutMax` the mirror), each optionally offset an extra world-unit distance via `.Offset(f)`. Each axis resolves in the target's own local space by default, a frame override's local space, or world space (space). target is any scene object handle — a GameObject or a prefab instance root.

```csharp
public NodeHandle AlignTo(SceneObjectHandle target, AxisAlign x = default, AxisAlign y = default, AxisAlign z = default, SceneObjectHandle frame = null, AlignSpace space = AlignSpace.TargetLocal)
```

- `target` (`SceneObjectHandle`)
- `x` (`AxisAlign`)
- `y` (`AxisAlign`)
- `z` (`AxisAlign`)
- `frame` (`SceneObjectHandle`)
- `space` (`AlignSpace`)

### NodeHandle.Between

Single-axis corridor placement: lands this object's bounds flush against from at fraction 0 and flush against to at fraction 1, moving only along axis. fraction is unclamped, so a value outside [0, 1] places past the respective anchor. axis is a world direction by default, or the matching local axis of alongOrientationOf when given, so placement can follow a tilted reference. Which anchor owns the fraction-0 end is fixed by argument order (from), not by which anchor sits at the higher world coordinate. Hierarchy-independent — anchors and self may live under unrelated parents. Chaining more than two `Between` calls into a longer anchor cycle is undefined: each evaluates independently and the last one to run wins.

```csharp
public NodeHandle Between(SceneObjectHandle from, SceneObjectHandle to, float fraction, Between.Axis axis, SceneObjectHandle alongOrientationOf = null)
```

- `from` (`SceneObjectHandle`)
- `to` (`SceneObjectHandle`)
- `fraction` (`float`)
- `axis` (`Between.Axis`)
- `alongOrientationOf` (`SceneObjectHandle`)

### NodeHandle.Tag

Set the GameObject tag.

```csharp
public NodeHandle Tag(string tag)
```

- `tag` (`string`)

### NodeHandle.Layer

Set the GameObject layer.

```csharp
public NodeHandle Layer(int layer)
```

- `layer` (`int`)

### NodeHandle.Active

Set the active state.

```csharp
public NodeHandle Active(bool active)
```

- `active` (`bool`)

### NodeHandle.Static

Mark the GameObject static.

```csharp
public NodeHandle Static(bool value = true)
```

- `value` (`bool`)

### NodeHandle.Id

Assign an explicit, stable logical id (otherwise one is derived).

```csharp
public NodeHandle Id(string id)
```

- `id` (`string`)

### NodeHandle.Instance

Instantiate a prefab instance nested under this GameObject, from assetPath (a `.prefab` or an imported model such as a `.fbx`). The path is authoring-time convenience only — the GUID is authoritative and stored in the sidecar; two calls with the same path produce two distinct instances.

```csharp
public InstanceHandle Instance(string assetPath)
```

- `assetPath` (`string`)

```csharp
public InstanceHandle<TRef> Instance<TRef>(TRef prefab) where TRef : PrefabRef
```

Instantiate a prefab instance nested under this GameObject from a generated typed ref (e.g. `Prefabs.Gun`). Returns a typed handle so nested targets can be addressed via a compiler-checked selector (`.On(t => t.Turret.Barrel, ...)`) and overrides authored at existing-vocabulary depth (the instance root and its direct children).

- `prefab` (`TRef`)

### NodeHandle.Component

Attach a component of type T with no field overrides.

```csharp
public NodeHandle Component<T>()
```

```csharp
public NodeHandle Component<T>(Action<ComponentHandle<T>> configure)
```

Attach a component of type T and set its serialized fields in a closure — `c.Set("m_Mass", 5f)` (serialized path) or `c.Set(r => r.mass, 5f)` (typed member selector).

- `configure` (`Action<ComponentHandle<T>>`)

### NodeHandle.Ref

Capture a reference to a component already added to this GameObject, to pass as a UnityEvent listener target. ordinal selects among several components of the same type on this GameObject (0 = the first).

```csharp
public ComponentRef<T> Ref<T>(int ordinal = 0)
```

- `ordinal` (`int`)

## OverrideHandle

```csharp
public sealed class OverrideHandle
```

A handle for authoring property overrides on a prefab instance's root, via `InstanceHandle.Override`. Unlike `ComponentHandle<T>`, a single closure can target multiple different root component types — `TComponent` is inferred per-call from the selector's explicitly-typed lambda parameter, e.g. `e.Set((Health x) => x.health, 50)`.

Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so these methods return handles for chaining but perform no work at runtime.

### OverrideHandle.Set

Set a field by typed member selector (e.g. `(Health x) => x.health`).

```csharp
public OverrideHandle Set<TComponent, TValue>(Func<TComponent, TValue> selector, TValue value)
```

- `selector` (`Func<TComponent, TValue>`)
- `value` (`TValue`)

```csharp
public OverrideHandle Set<TComponent, TValue>(Func<TComponent, TValue> selector, AssetReference asset)
```

Set an asset-reference field by typed member selector, e.g. `e.Set((MeshRenderer m) => m.sharedMaterial, Asset("Assets/Materials/Red.mat"))`. The selector's return type is the asset type; the value is the `AssetReference` factory result.

- `selector` (`Func<TComponent, TValue>`)
- `asset` (`AssetReference`)

```csharp
public OverrideHandle Set<TComponent, TValue>(Func<TComponent, TValue> selector, SceneObjectHandle target)
```

Set a cross-object-reference field by typed member selector, e.g. `e.Set((Joint j) => j.connectedBody, target)`. The target is any scene object handle — a GameObject or a prefab instance root. Pass `NodeHandle.None` to clear the slot.

- `selector` (`Func<TComponent, TValue>`)
- `target` (`SceneObjectHandle`)

```csharp
public OverrideHandle Set<TComponent>(string serializedPath, object value)
```

Set a field by serialized property path (e.g. `e.Set<BoxCollider>("m_Center.x", 1.0)`). The target root component type is explicit since there is no selector closure to infer it from.

- `serializedPath` (`string`)
- `value` (`object`)

## PrefabRef

```csharp
public class PrefabRef
```

Base type every generated typed prefab reference derives from — both root prefab refs (e.g. `TankRef`, exposed as `Prefabs.Tank`) and node refs for nested children (e.g. `TurretRef`, exposed as `TankRef.Turret`).

Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so derived types carry no runtime behavior (generated get-only properties simply return `default!`).

## PrefabRoot

```csharp
public sealed class PrefabRoot
```

Root of a prefab definition. Add the prefab's single root GameObject here.

This is compile-time scaffolding so builder files type-check and autocomplete. SceneBuilder reads the source text (via Roslyn) to build the prefab — these methods are never executed. Call `Add` (or its closure overload) exactly once; a second call produces a located build error, since a prefab has exactly one root.

### PrefabRoot.Add

Add the prefab's root GameObject. Call this once.

```csharp
public NodeHandle Add(string name)
```

- `name` (`string`)

```csharp
public NodeHandle Add(string name, Action<NodeHandle> configure)
```

Add the prefab's root GameObject and configure it (and its children) in a closure.

- `name` (`string`)
- `configure` (`Action<NodeHandle>`)

## SceneObjectHandle

```csharp
public abstract class SceneObjectHandle
```

A handle to an object in a scene definition: a plain GameObject (`NodeHandle`) or a prefab instance root (`InstanceHandle`). Every authoring call that takes a cross-object reference takes this, so either kind of target can be assigned.

Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so this carries no state and performs no work at runtime.

### SceneObjectHandle.As

This scene object, typed as T so it can be written as a UnityEvent listener's object argument (e.g. `m.SetSubject(door.As<UnityEngine.GameObject>())`). T is a compile-time cast for that parameter slot; it does NOT select a component (use `NodeHandle.Ref` for that) and performs no work at runtime.

```csharp
public T As<T>()
```

## SceneRoot

```csharp
public sealed class SceneRoot
```

Root of a scene definition. Add top-level GameObjects here.

This is compile-time scaffolding so builder files type-check and autocomplete. SceneBuilder reads the source text (via Roslyn) to build the scene — these methods are never executed.

### SceneRoot.Add

Add a root GameObject.

```csharp
public NodeHandle Add(string name)
```

- `name` (`string`)

```csharp
public NodeHandle Add(string name, Action<NodeHandle> configure)
```

Add a root GameObject and configure it (and its children) in a closure.

- `name` (`string`)
- `configure` (`Action<NodeHandle>`)

### SceneRoot.Instance

Instantiate a root prefab instance from assetPath (a `.prefab` or an imported model such as a `.fbx`). The path is authoring-time convenience only — the GUID is authoritative and stored in the sidecar; two calls with the same path produce two distinct instances.

```csharp
public InstanceHandle Instance(string assetPath)
```

- `assetPath` (`string`)

```csharp
public InstanceHandle<TRef> Instance<TRef>(TRef prefab) where TRef : PrefabRef
```

Instantiate a root prefab instance from a generated typed ref (e.g. `Prefabs.Tank`). Returns a typed handle so nested targets can be addressed via a compiler-checked selector, e.g. `.On(t => t.Turret.Barrel, ...)`.

- `prefab` (`TRef`)

## ScopedHandle

```csharp
public sealed class ScopedHandle
```

The closure argument passed to `InstanceHandle.On` / `InstanceHandle.On`, used to author overrides scoped to a nested target inside a prefab instance's hierarchy (e.g. the turret's barrel).

Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so these methods return handles for chaining but perform no work at runtime.

### ScopedHandle.Override

Author property overrides on the scoped target's own components, mirroring `InstanceHandle.Override`.

```csharp
public ScopedHandle Override(Action<OverrideHandle> configure)
```

- `configure` (`Action<OverrideHandle>`)

### ScopedHandle.AddComponent

Add a component of type T to the scoped target with no field overrides.

```csharp
public ScopedHandle AddComponent<T>()
```

```csharp
public ScopedHandle AddComponent<T>(Action<ComponentHandle<T>> configure)
```

Add a component of type T to the scoped target and set its serialized fields in a closure.

- `configure` (`Action<ComponentHandle<T>>`)

### ScopedHandle.RemoveComponent

Remove a component of type T from the scoped target (must exist on the source prefab).

```csharp
public ScopedHandle RemoveComponent<T>()
```

## VariantRoot

```csharp
public sealed class VariantRoot
```

The `root` parameter of `IPrefabVariantDefinition.Build` — an override-only handle onto the variant's base prefab instance. There is no `Add`: the variant's single root IS the base prefab, never a freshly authored object.

Compile-time scaffolding only — SceneBuilder parses the source text to build the variant, so these methods return handles for chaining but perform no work at runtime.

### VariantRoot.Override

Author property overrides on the base prefab's own root components, mirroring `InstanceHandle.Override`.

```csharp
public VariantRoot Override(Action<OverrideHandle> configure)
```

- `configure` (`Action<OverrideHandle>`)

### VariantRoot.AddComponent

Add a component of type T to the base prefab's root with no field overrides.

```csharp
public VariantRoot AddComponent<T>()
```

```csharp
public VariantRoot AddComponent<T>(Action<ComponentHandle<T>> configure)
```

Add a component of type T to the base prefab's root and set its serialized fields in a closure.

- `configure` (`Action<ComponentHandle<T>>`)

### VariantRoot.RemoveComponent

Remove a component of type T from the base prefab's root (must exist on the base).

```csharp
public VariantRoot RemoveComponent<T>()
```

### VariantRoot.On

Author overrides scoped to a nested target inside the base prefab's hierarchy, addressed by child path (e.g. `"Turret/Barrel"`), mirroring `InstanceHandle.On`.

```csharp
public VariantRoot On(string childPath, Action<ScopedHandle> closure)
```

- `childPath` (`string`)
- `closure` (`Action<ScopedHandle>`)

## Analyzer diagnostics

| ID | Severity | Title |
|----|----------|-------|
| SB1001 | Error | Build body must be a flat sequence of builder calls |
| SB1002 | Error | Unrecognized builder call |
| SB1003 | Error | Component closure must contain only .Set(...), .OnClick(...) or .OnEvent(...) calls |
| SB1101 | Info | Use the typed .Set(...) form |
| SB1102 | Info | Use the typed prefab reference |
| SB1103 | Info | Use the typed .On(...) selector |
| SB1104 | Warning | Use the typed Tags catalog |
| SB1105 | Warning | Use the typed Layers catalog |
| SB1106 | Info | Use the typed Set<T> overload |
| SB1201 | Error | UnityEvent listener signature mismatch |
| SB1202 | Warning | UnityEvent listener target not eligible |
| SB1203 | Warning | Two position drivers claim the same axis |
