#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Core.Model;
using CoreColor = SceneBuilder.Core.Model.Color;
using CoreVec2 = SceneBuilder.Core.Model.Vec2;
using CoreVec3 = SceneBuilder.Core.Model.Vec3;
using CoreVec4 = SceneBuilder.Core.Model.Vec4;
using CoreQuat = SceneBuilder.Core.Model.Quat;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// The M3 <see cref="SerializedProperty"/> dispatch layer (read + write). Converts a live
    /// component's serialized fields to/from Core <see cref="ValueNode"/>s, dispatching on
    /// <see cref="SerializedPropertyType"/> per M3's table. Excluded properties (see
    /// <see cref="SerializedFieldExclusions"/>) are skipped on read; a field at its type default is
    /// NOT skipped — <see cref="ReadComponent"/> reports it unconditionally (spec C2). The decision to
    /// OMIT a default-valued field from what gets AUTHORED is made emit-side, by
    /// <see cref="SceneBuilder.Core.Reconcile.ComponentReconciler"/>, fed by
    /// <see cref="ComponentDefaultTemplate"/> via <see cref="SceneSnapshot.ComponentDefaults"/>.
    /// An <c>Enum</c>-typed or Integer-typed-but-enum-backed field's managed member/type is resolved
    /// through <see cref="SerializedMemberMap"/>, the one owner of that decision on both read and write.
    /// A nested struct/class field's members are likewise keyed by their compiling PUBLIC spelling
    /// (<c>ColorBlock.m_NormalColor</c> -&gt; <c>normalColor</c>) via
    /// <see cref="SerializedMemberMap.TryPublicMemberName"/> on read and
    /// <see cref="SerializedMemberMap.TrySerializedName"/> on write, so <c>ReadNested</c> and
    /// <c>WriteTarget.Member</c> never disagree on a member's name.
    /// </summary>
    public static class SerializedFieldBridge
    {
        // ---- Read (component -> ComponentData) ---------------------------------------------

        public static ComponentData ReadComponent(
            Component component, Func<UnityEngine.Object, string?>? resolveSceneRef,
            Func<UnityEngine.Object?, ValueNode?>? resolveListenerRef = null)
        {
            // Registration only — never filters this read. Ensures ComponentDefaultTemplate has a
            // template for every type reachable from a snapshot, so SceneSnapshotReader.FromRoots can
            // populate SceneSnapshot.ComponentDefaults for it.
            ComponentDefaultTemplate.Register(component.GetType());

            var fields = CollectFields(new SerializedObject(component), resolveSceneRef, resolveListenerRef);

            return new ComponentData
            {
                Type = BuildTypeRef(component),
                Fields = new FieldMap(fields),
            };
        }

        // A user MonoBehaviour's identity is anchored to its MonoScript asset GUID, so it resolves
        // even if the assembly/namespace later changes. Built-in native components have no MonoScript
        // asset — they keep the plain full-name TypeRef unchanged.
        private static TypeRef BuildTypeRef(Component component)
        {
            var fullName = component.GetType().FullName;

            if (component is MonoBehaviour monoBehaviour)
            {
                var monoScript = MonoScript.FromMonoBehaviour(monoBehaviour);
                if (monoScript != null)
                {
                    var path = AssetDatabase.GetAssetPath(monoScript);
                    if (!string.IsNullOrEmpty(path))
                    {
                        var guid = AssetDatabase.AssetPathToGUID(path);
                        if (!string.IsNullOrEmpty(guid))
                        {
                            return new TypeRef(fullName, null, guid);
                        }
                    }
                }
            }

            return new TypeRef(fullName);
        }

        // Collects the supported, non-excluded top-level serialized fields of a component as
        // (propertyPath -> ValueNode) pairs. Shared by the real-component read path
        // (ReadComponent) and ComponentDefaultTemplate.Register's template build, so both read
        // identically and a live component's fields and its type's default template can never
        // diverge in SHAPE (only in value). Internal — ComponentDefaultTemplate is its only other
        // caller.
        //
        // Walks with Next, NOT NextVisible. NextVisible yields exactly what a DEFAULT inspector would
        // draw, so every property a custom editor draws by hand — Canvas.m_SortingOrder /
        // m_SortingLayerID / m_TargetDisplay, Rigidbody.m_Constraints, Renderer sorting — and every
        // property drawn outside the property list (m_Enabled, in the component header) was invisible
        // to the reader. Scene->code diffs for those fields were therefore always empty: the editor
        // value changed and the sync answered "Scene already matches code". Serialized state, not
        // inspector-visible state, is what round-trips, so the filtering is by explicit name, via
        // SerializedFieldExclusions, rather than by Unity's inspector-drawing flag.
        internal static List<KeyValuePair<string, ValueNode>> CollectFields(
            SerializedObject so, Func<UnityEngine.Object, string?>? resolveSceneRef,
            Func<UnityEngine.Object?, ValueNode?>? resolveListenerRef = null)
        {
            var fields = new List<KeyValuePair<string, ValueNode>>();

            // Derived here (not passed in) so the real-component read and the default-template build
            // filter identically by construction — a divergence would prune against a template that
            // never had the field.
            var ownerType = so.targetObject == null ? null : so.targetObject.GetType();

            // Every serialized UnityEvent field's public C# spelling, gathered alongside the field
            // read so ComponentDefaultTemplate.RegisterMemberSpellings sees the exact same set of
            // fields the live read does. A field whose serialized member has no compiling public
            // spelling (TryPublicMemberName false) is left out — SceneSnapshot.MemberSpellings then
            // carries no entry for it, and the reconciler's existing UnsyncableListener path fires.
            List<(string SerializedPath, string PublicName)>? spellings = null;

            // spec 54: every top-level serialized field's PROVEN-safe public selector spelling
            // (SerializedMemberMap.TrySafeSelectorName), gathered alongside the field read so
            // ComponentDefaultTemplate.RegisterSafeMembers sees the exact same set the live read
            // does — read and signal can never diverge. A field with no proven-safe spelling is left
            // out; the reconciler's self-heal then downgrades any typed selector naming it.
            List<string>? safeMembers = null;

            // Computed ONCE per component, ahead of the field loop: the propertyPath of every
            // managed-ref occurrence that is either a sibling-shared instance or a cycle's
            // back-edge is a report-only Unsupported marker, not a forked/non-terminating read
            // (spec 10 §7).
            var unsupportedManagedRefPaths = ManagedReferenceReader.ComputeUnsupportedManagedRefPaths(so);

            var it = so.GetIterator();
            var enterChildren = true;
            while (it.Next(enterChildren))
            {
                enterChildren = false; // top-level properties only; nesting handled in ReadProperty
                if (SerializedFieldExclusions.IsExcluded(ownerType, it.propertyPath))
                {
                    continue;
                }

                var isUnityEventField = UnityEventReader.IsUnityEventField(it);
                var value = isUnityEventField
                    ? UnityEventReader.ReadField(it.Copy(), resolveListenerRef)
                    : ReadProperty(it.Copy(), resolveSceneRef, unsupportedManagedRefPaths);

                if (isUnityEventField && ownerType != null
                    && SerializedMemberMap.TryPublicMemberName(ownerType, it.propertyPath, out var publicName))
                {
                    (spellings ??= new List<(string, string)>()).Add((it.propertyPath, publicName));
                }

                if (ownerType != null && SerializedMemberMap.TrySafeSelectorName(ownerType, it.propertyPath, out var safeName))
                {
                    (safeMembers ??= new List<string>()).Add(safeName);
                }

                // Field types M3 cannot represent — object/asset references (mesh, material, physics
                // material) and LayerMask are M4+ — are SKIPPED, never written. Emitting them would
                // produce uncompilable tokens (a bare `ObjectReference` / `LayerMask` identifier).
                // This covers a bare Unsupported value AND any List/Nested that carries an Unsupported
                // leaf (e.g. a MeshRenderer's m_Materials object-reference array — the cube-dump bug):
                // rendering `new[] { ObjectReference }` is just as uncompilable as a bare token.
                // Spec 04: an Unsupported field is not written to source, only flagged.
                if (ContainsUnsupported(value))
                {
                    continue;
                }

                fields.Add(new KeyValuePair<string, ValueNode>(it.propertyPath, value));
            }

            if (ownerType?.FullName is { } ownerTypeFullName && spellings is { Count: > 0 })
            {
                spellings.Sort((a, b) => string.CompareOrdinal(a.SerializedPath, b.SerializedPath));
                ComponentDefaultTemplate.RegisterMemberSpellings(ownerTypeFullName, spellings);
            }

            // Registered unconditionally (even zero safe members) for every read with a known owner
            // type — an entry, not its count, is what marks the type INSPECTED (spec 54's
            // self-heal conservatism gate: absence of knowledge must never read as proof of not-safe).
            if (ownerType?.FullName is { } ownerTypeFullNameForSafe)
            {
                ComponentDefaultTemplate.RegisterSafeMembers(
                    ownerTypeFullNameForSafe, (IReadOnlyList<string>?)safeMembers ?? Array.Empty<string>());
            }

            return fields;
        }

        // A field is unrepresentable in M3 if its value is Unsupported OR it is a List whose
        // recursion bottoms out in any Unsupported leaf (an object-reference array). Such a field
        // must be skipped whole — partially rendering it emits uncompilable value tokens. A Nested
        // value's own unrepresentable MEMBERS are excluded at the member level instead (spec 32 C4,
        // NestedValueEmission.Project) — the rest of the struct still round-trips.
        //
        // Its recursion stays hand-rolled and list-only. ValueWalk.Any descends Nested members as
        // well, so routing this through it would start reporting a whole struct as unrepresentable
        // because one member is, dropping the entire field from source where NestedValueEmission.Project
        // excludes that one member and round-trips the rest.
        //
        // A top-level managed-ref marker (missing-type or shared/cycle) is exempt: both are handled
        // downstream by ComponentReconciler's managed-ref intercept (conflict, or report-only skip),
        // never emitted as a raw source token, so dropping them here would silently vanish the
        // fail-loud missing-type conflict and the sharing-marker deliverable. A marker nested INSIDE
        // a ManagedReference's own Fields already survives — this switch recurses only into List.
        private static bool ContainsUnsupported(ValueNode value) => value switch
        {
            // Fully qualified: UnityEditor's OWN ManagedReferenceMissingType (an inspector helper
            // type) collides on bare name with this Core marker.
            ValueNode.Unsupported u => !(u.RawToken.StartsWith(SceneBuilder.Core.Reconcile.ManagedReferenceMissingType.Marker, StringComparison.Ordinal)
                || u.RawToken.StartsWith(ManagedReferenceReader.SharedMarker, StringComparison.Ordinal)),
            ValueNode.List list => list.Items.Any(ContainsUnsupported),
            _ => false,
        };

        private static ValueNode ReadProperty(
            SerializedProperty p, Func<UnityEngine.Object, string?>? resolveSceneRef,
            ISet<string> unsupportedManagedRefPaths)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    return ValueNode.Primitive.Bool(p.boolValue);
                case SerializedPropertyType.Integer:
                    return ReadInteger(p);
                case SerializedPropertyType.Float:
                    return p.type == "double"
                        ? ValueNode.Primitive.Double(p.doubleValue)
                        : ValueNode.Primitive.Float(p.floatValue);
                case SerializedPropertyType.String:
                    return ValueNode.Primitive.String(p.stringValue ?? "");
                case SerializedPropertyType.Enum:
                    return ReadEnum(p);
                case SerializedPropertyType.Vector2:
                {
                    var v = p.vector2Value;
                    return new ValueNode.Vec2(new CoreVec2(v.x, v.y));
                }
                case SerializedPropertyType.Vector3:
                {
                    var v = p.vector3Value;
                    return new ValueNode.Vec3(new CoreVec3(v.x, v.y, v.z));
                }
                case SerializedPropertyType.Vector4:
                {
                    var v = p.vector4Value;
                    return new ValueNode.Vec4(new CoreVec4(v.x, v.y, v.z, v.w));
                }
                case SerializedPropertyType.Quaternion:
                {
                    var q = p.quaternionValue;
                    return new ValueNode.Quat(new CoreQuat(q.x, q.y, q.z, q.w));
                }
                case SerializedPropertyType.Color:
                {
                    var c = p.colorValue;
                    return new ValueNode.Color(new CoreColor(c.r, c.g, c.b, c.a));
                }
                case SerializedPropertyType.Generic:
                    return p.isArray ? ReadList(p, resolveSceneRef, unsupportedManagedRefPaths) : ReadNested(p, resolveSceneRef, unsupportedManagedRefPaths);
                case SerializedPropertyType.ObjectReference:
                    // M4: an object-reference field pointing at a project asset becomes a
                    // ValueNode.AssetRef (populated), a null asset field becomes AssetRef(null) (None).
                    // M5: a scene-object reference becomes a ValueNode.ObjectRef (resolved via
                    // resolveSceneRef when supplied), and a null GameObject/Component-typed field
                    // becomes ObjectRef(null). Replaces the old blanket "object refs are unsupported"
                    // skip for asset-pointing refs.
                    return AssetReferenceResolver.ReadObjectReference(p, resolveSceneRef);
                case SerializedPropertyType.ManagedReference:
                    // A [SerializeReference] polymorphic field (spec 10): null / missing-type /
                    // shared-instance-or-cycle / resolved-concrete-instance, all owned by the
                    // dedicated reader so this dispatch never falls to the bare Unsupported default.
                    return ManagedReferenceReader.ReadField(p, resolveSceneRef, unsupportedManagedRefPaths);
                default:
                    return new ValueNode.Unsupported(p.propertyType.ToString());
            }
        }

        // An Integer-typed serialized property (SerializedPropertyType.Integer, not Enum) can still
        // be a native enum's backing storage — e.g. Rigidbody.m_Constraints, a [Flags] enum with no
        // SerializedPropertyType.Enum at all. Ask the SAME resolver ReadEnum does BEFORE the plain
        // int/long dispatch, so a native flags field reads as a typed member set, not a raw int.
        private static ValueNode ReadInteger(SerializedProperty p)
        {
            if (p.type == "long")
            {
                return ValueNode.Primitive.Long(p.longValue);
            }

            var targetType = p.serializedObject.targetObject?.GetType();
            if (targetType != null && SerializedMemberMap.TryEnumNode(targetType, p.propertyPath, p.intValue, out var enumNode))
            {
                return enumNode;
            }

            return ValueNode.Primitive.Int(p.intValue);
        }

        // Resolves the backing enum Type via SerializedMemberMap (managed field, then the
        // m_Xxx->xxx property ladder) and emits a canonical Enum node by member NAME — never
        // p.enumNames (sometimes a display string, e.g. "Screen Space - Camera", sometimes raw
        // member names) and never p.enumValueIndex (measured -1 for a non-contiguous/combined
        // flags value). An unresolved backing type keeps the raw value as an Int, the same shape
        // WritePrimitive writes back through SerializedProperty.intValue and the same shape
        // `c.Set("m_RenderMode", 0)` authors.
        private static ValueNode ReadEnum(SerializedProperty p)
        {
            var targetType = p.serializedObject.targetObject?.GetType();
            if (targetType != null && SerializedMemberMap.TryEnumNode(targetType, p.propertyPath, p.intValue, out var enumNode))
            {
                return enumNode;
            }

            return ValueNode.Primitive.Int(p.intValue);
        }

        private static ValueNode ReadList(
            SerializedProperty p, Func<UnityEngine.Object, string?>? resolveSceneRef,
            ISet<string> unsupportedManagedRefPaths)
        {
            var items = new List<ValueNode>(p.arraySize);
            for (var i = 0; i < p.arraySize; i++)
            {
                items.Add(ReadProperty(p.GetArrayElementAtIndex(i).Copy(), resolveSceneRef, unsupportedManagedRefPaths));
            }

            return new ValueNode.List(items);
        }

        private static ValueNode ReadNested(
            SerializedProperty p, Func<UnityEngine.Object, string?>? resolveSceneRef,
            ISet<string> unsupportedManagedRefPaths)
        {
            // The struct/class Type resolves FIRST -- every child key below is mapped through it, so
            // the map must be in hand before the child walk starts.
            var rootType = p.serializedObject.targetObject?.GetType();
            var type = rootType != null ? SerializedMemberMap.ResolveManagedFieldType(rootType, p.propertyPath) : null;
            if (type == null || type.IsGenericType)
            {
                // Cannot resolve to a concrete, non-generic managed type (e.g. a built-in native
                // field, or an unsupported generic serializable) — preserve verbatim, never emit
                // a broken Nested (e.g. a backtick-arity type name).
                return new ValueNode.Unsupported(p.type);
            }

            var fields = ReadManagedInstanceFields(p, type, resolveSceneRef, unsupportedManagedRefPaths);

            var typeName = type.FullName!.Replace('+', '.');
            return new ValueNode.Nested(typeName, new FieldMap(fields));
        }

        // The child-walk shared by ReadNested (declared struct/class type) and
        // ManagedReferenceReader.ReadField (a [SerializeReference] instance's CONCRETE type) — the
        // only difference between the two callers is which Type maps the children, so nested managed
        // refs and lists compose through the SAME walk with no duplicated traversal logic.
        internal static List<KeyValuePair<string, ValueNode>> ReadManagedInstanceFields(
            SerializedProperty p, Type type, Func<UnityEngine.Object, string?>? resolveSceneRef,
            ISet<string> unsupportedManagedRefPaths)
        {
            var fields = new List<KeyValuePair<string, ValueNode>>();
            var it = p.Copy();
            var end = p.GetEndProperty();
            var childDepth = p.depth + 1;
            var enterChildren = true;

            // Next, not NextVisible — same rule as CollectFields one level down: a struct's hidden
            // children (e.g. StaticBatchInfo's firstSubMesh/subMeshCount) are serialized state and
            // must not disappear from the read just because no inspector draws them.
            while (it.Next(enterChildren) && !SerializedProperty.EqualContents(it, end))
            {
                enterChildren = false;
                if (it.depth != childDepth)
                {
                    continue;
                }

                // A member with a compiling public spelling (UnityEngine.UI.ColorBlock.m_NormalColor
                // -> normalColor) is keyed by it, so the emitted initializer compiles (spec 32 C4). A
                // member with NO public spelling is kept under its serialized name with its value
                // replaced by a located marker -- Core excludes and reports it per-member
                // (NestedValueEmission.Project) rather than the whole struct being dropped.
                var hasPublicName = SerializedMemberMap.TryPublicMemberName(type, it.name, out var publicName);
                var key = hasPublicName ? publicName : it.name;
                var value = hasPublicName
                    ? ReadProperty(it.Copy(), resolveSceneRef, unsupportedManagedRefPaths)
                    : new ValueNode.Unsupported($"no public member for '{it.name}'");

                fields.Add(new KeyValuePair<string, ValueNode>(key, value));
            }

            return fields;
        }

        // ---- Write (ValueNode -> SerializedProperty) ---------------------------------------

        /// <summary>
        /// Writes <paramref name="value"/> into the property at <paramref name="path"/> on the given
        /// <see cref="SerializedObject"/>. Caller commits via a single <c>ApplyModifiedProperties</c>.
        /// </summary>
        public static void WriteField(SerializedObject so, string path, ValueNode value)
        {
            var prop = so.FindProperty(path);
            if (prop == null)
            {
                Debug.LogWarning($"[SceneBuilder] SerializedProperty '{path}' not found on '{so.targetObject}'.");
                return;
            }

            WriteProperty(prop, value);
        }

        // The DESCENT is ValueWalk.Descend, the one recursion over a value's container structure. It
        // enters a node before its children and hands each child the context its parent produced, so
        // what stays here is per-node work only: write the leaf, size the array before its elements
        // are entered, resolve the struct type a nested value's own members map through.
        private static void WriteProperty(SerializedProperty p, ValueNode value) =>
            ValueWalk.Descend(
                value,
                new WriteTarget(p, null),
                enter: EnterNode,
                item: (_, target, index) => target.Element(index),
                member: (_, target, key) => target.Member(key),
                // No live property is derived for a listener slot here: a UnityEvent is written by
                // its own dedicated plan op, never through the generic field bridge, so the subtree
                // is left exactly as found.
                listenerSlot: (_, _, _, _) => default,
                // A managed reference is written by its own dedicated plan op, never through the
                // generic field bridge, so the subtree is left exactly as found.
                managedReferenceMember: (_, _, _) => default);

        // Where a node is written, plus the managed struct Type resolved on the ENCLOSING nested
        // value: the map each of THAT value's own member keys goes through, and nothing deeper. A
        // nested member's own members map through the type resolved at their own node. A null
        // Property means there is no live property here, so the node and everything under it is left
        // exactly as found, which is how a member key matching no child property skips its whole
        // subtree without writing or warning.
        private readonly struct WriteTarget
        {
            internal WriteTarget(SerializedProperty? property, Type? structType)
            {
                Property = property;
                StructType = structType;
            }

            internal SerializedProperty? Property { get; }

            internal Type? StructType { get; }

            internal WriteTarget Element(int index) =>
                Property == null ? default : new WriteTarget(Property.GetArrayElementAtIndex(index), null);

            // Maps the PUBLIC member key back to its serialized child name before FindPropertyRelative.
            // A key that does not map through it (the type itself doesn't resolve, or this specific key
            // isn't a known public spelling) falls back to the key verbatim -- this is what keeps a
            // legacy raw-serialized-name builder writing correctly.
            internal WriteTarget Member(string key)
            {
                if (Property == null)
                {
                    return default;
                }

                var serializedKey = StructType != null && SerializedMemberMap.TrySerializedName(StructType, key, out var mapped)
                    ? mapped
                    : key;
                return new WriteTarget(Property.FindPropertyRelative(serializedKey), null);
            }
        }

        private static WriteTarget EnterNode(ValueNode value, WriteTarget target)
        {
            var p = target.Property;
            if (p == null)
            {
                return default;
            }

            switch (value)
            {
                case ValueNode.Primitive prim:
                    WritePrimitive(p, prim);
                    break;
                case ValueNode.Enum e:
                    WriteEnum(p, e);
                    break;
                case ValueNode.Vec2 v:
                    p.vector2Value = new Vector2(v.Value.X, v.Value.Y);
                    break;
                case ValueNode.Vec3 v:
                    p.vector3Value = new Vector3(v.Value.X, v.Value.Y, v.Value.Z);
                    break;
                case ValueNode.Vec4 v:
                    p.vector4Value = new Vector4(v.Value.X, v.Value.Y, v.Value.Z, v.Value.W);
                    break;
                case ValueNode.Quat q:
                    p.quaternionValue = new Quaternion(q.Value.X, q.Value.Y, q.Value.Z, q.Value.W);
                    break;
                case ValueNode.Color c:
                    p.colorValue = new UnityEngine.Color(c.Value.R, c.Value.G, c.Value.B, c.Value.A);
                    break;
                case ValueNode.List list:
                    // Sizing is this node's OWN work, not its descent: element contexts are derived
                    // after this returns, so the array is sized before any element is reached, and an
                    // empty list still truncates the live array to zero.
                    p.arraySize = list.Items.Count;
                    break;
                case ValueNode.Nested:
                {
                    // Resolve the struct/class Type the SAME way ReadNested does, from the target
                    // object's type plus THIS node's own propertyPath, and hand it to this node's
                    // members. Resolving it here, once per nested value rather than once per member, is
                    // the single reflection walk per node the write path has always done.
                    var rootType = p.serializedObject.targetObject?.GetType();
                    return new WriteTarget(
                        p,
                        rootType != null ? SerializedMemberMap.ResolveManagedFieldType(rootType, p.propertyPath) : null);
                }
                case ValueNode.Unsupported:
                    // No-op (flagged upstream); never overwrite an unsupported value.
                    break;
                default:
                    Debug.LogWarning(
                        $"[SceneBuilder] {LocationOf(p)}: value kind '{value.GetType().Name}' has no write " +
                        "dispatch and was not applied. A scene or asset reference is written by SetReference / " +
                        "SetAssetRef, never through a field value.");
                    break;
            }

            return target;
        }

        private static string LocationOf(SerializedProperty? p)
        {
            var target = p?.serializedObject?.targetObject;
            if (target == null)
            {
                return "<unknown>";
            }

            var owner = target as Component;
            var objectName = owner != null && owner.gameObject != null ? owner.gameObject.name : target.name;
            return $"{objectName} > {target.GetType().Name}.{p!.propertyPath}";
        }

        private static void WritePrimitive(SerializedProperty p, ValueNode.Primitive prim)
        {
            switch (prim.Kind)
            {
                case PrimitiveKind.Bool:
                    p.boolValue = Convert.ToBoolean(prim.Value);
                    break;
                case PrimitiveKind.Int:
                    p.intValue = Convert.ToInt32(prim.Value);
                    break;
                case PrimitiveKind.Long:
                    p.longValue = Convert.ToInt64(prim.Value);
                    break;
                case PrimitiveKind.Float:
                    p.floatValue = Convert.ToSingle(prim.Value);
                    break;
                case PrimitiveKind.Double:
                    p.doubleValue = Convert.ToDouble(prim.Value);
                    break;
                case PrimitiveKind.String:
                    p.stringValue = Convert.ToString(prim.Value) ?? "";
                    break;
            }
        }

        // Resolves the backing enum Type via SerializedMemberMap and writes via Enum.Parse OR'd into
        // p.intValue for every member (correct for a single member or a flags combination alike —
        // never p.enumValueIndex, measured -1 for a non-contiguous/combined value). Only when the
        // map returns null (unresolved backing type) does this fall back to a p.enumNames
        // display-name match on a non-flags node; refuses (warns, writes nothing) rather than guess.
        private static void WriteEnum(SerializedProperty p, ValueNode.Enum e)
        {
            var targetType = p.serializedObject.targetObject?.GetType();
            var type = targetType != null ? SerializedMemberMap.ResolveEnumType(targetType, p.propertyPath) : null;

            if (type != null)
            {
                if (SerializedMemberMap.TryEnumValue(type, e.Members, out var value))
                {
                    p.intValue = value;
                    return;
                }

                Debug.LogWarning(
                    $"[SceneBuilder] Enum member(s) '{string.Join("|", e.Members)}' could not be parsed as " +
                    $"'{type.FullName}' for '{p.propertyPath}'.");
                return;
            }

            if (!e.IsFlags && e.Members.Count > 0)
            {
                var names = p.enumNames;
                var target = e.Members[0];
                for (var i = 0; i < names.Length; i++)
                {
                    if (names[i] == target)
                    {
                        p.enumValueIndex = i;
                        return;
                    }
                }
            }

            var unresolvedTarget = e.Members.Count > 0 ? e.Members[0] : "";
            Debug.LogWarning($"[SceneBuilder] Enum member '{unresolvedTarget}' not found on '{p.propertyPath}'.");
        }
    }
}
