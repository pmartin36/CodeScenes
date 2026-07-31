#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// <see cref="SerializedPropertyType"/> per M3's table. Bookkeeping properties are skipped on
    /// read; a field at its type default is NOT skipped — <see cref="ReadComponent"/> reports it
    /// unconditionally (spec C2). The decision to OMIT a default-valued field from what gets
    /// AUTHORED is made emit-side, by <see cref="SceneBuilder.Core.Reconcile.ComponentReconciler"/>,
    /// fed by <see cref="ComponentDefaultTemplate"/> via <see cref="SceneSnapshot.ComponentDefaults"/>.
    /// </summary>
    public static class SerializedFieldBridge
    {
        // Internal Unity bookkeeping — never surfaced as a field (would corrupt diffs).
        // The last three used to be filtered for free by the old NextVisible walk: they carry the
        // native hidden flag, so an inspector-visibility walk never reached them. The walk now
        // enumerates every SERIALIZED property (see CollectFields), so they are named explicitly.
        private static readonly HashSet<string> Bookkeeping = new()
        {
            "m_Script",
            "m_ObjectHideFlags",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_GameObject",
            "m_EditorHideFlags",
            "m_EditorClassIdentifier",
            "m_Name",
        };

        // Renderer state Unity REGENERATES from a lightmap bake / static batching / editor selection.
        // It is serialized and hidden, but it is build output, not authored data: round-tripping it
        // would write bake results into the builder source and then push those stale results back
        // onto the scene on the next code->scene rebuild. Excluded by name, deliberately narrow —
        // every OTHER hidden property is captured.
        private static readonly HashSet<string> DerivedBuildState = new()
        {
            "m_LightmapIndex",
            "m_LightmapIndexDynamic",
            "m_LightmapTilingOffset",
            "m_LightmapTilingOffsetDynamic",
            "m_StaticBatchInfo",
            "m_StaticBatchRoot",
            "m_SelectedEditorRenderState",
        };

        // Components authored through a dedicated closed-keyword call (`.FitSize(height: 2f)`,
        // `.SurfaceSnap(down: true)`) rather than `.Component<T>(c => c.Set(...))`. Their authoring
        // grammar has a FIXED argument set, so a field outside it cannot be rendered back into source:
        // the writer emits `.FitSize(m_Enabled: false)` and the parser then rejects its own output.
        // m_Enabled is the only property the serialized walk reaches on them that the grammar cannot
        // express (disabling a FitSize releases its channel — that is what syncs, via the transform).
        private static readonly HashSet<string> ClosedGrammarComponents = new()
        {
            SpatialComponents.FitSizeTypeName,
            SpatialComponents.SurfaceSnapTypeName,
        };

        // ---- Read (component -> ComponentData) ---------------------------------------------

        public static ComponentData ReadComponent(Component component, Func<UnityEngine.Object, string?>? resolveSceneRef = null)
        {
            // Registration only — never filters this read. Ensures ComponentDefaultTemplate has a
            // template for every type reachable from a snapshot, so SceneSnapshotReader.FromRoots can
            // populate SceneSnapshot.ComponentDefaults for it.
            ComponentDefaultTemplate.Register(component.GetType());

            var fields = CollectFields(new SerializedObject(component), resolveSceneRef);

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

        // Collects the supported, non-bookkeeping top-level serialized fields of a component as
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
        // inspector-visible state, is what round-trips, so the filtering is by explicit name
        // (Bookkeeping / DerivedBuildState) rather than by Unity's inspector-drawing flag.
        internal static List<KeyValuePair<string, ValueNode>> CollectFields(SerializedObject so, Func<UnityEngine.Object, string?>? resolveSceneRef = null)
        {
            var fields = new List<KeyValuePair<string, ValueNode>>();

            // Derived here (not passed in) so the real-component read and the default-template build
            // filter identically by construction — a divergence would prune against a template that
            // never had the field.
            var typeName = so.targetObject == null ? null : so.targetObject.GetType().FullName;
            var closedGrammar = typeName != null && ClosedGrammarComponents.Contains(typeName);

            var it = so.GetIterator();
            var enterChildren = true;
            while (it.Next(enterChildren))
            {
                enterChildren = false; // top-level properties only; nesting handled in ReadProperty
                if (Bookkeeping.Contains(it.propertyPath) || DerivedBuildState.Contains(it.propertyPath))
                {
                    continue;
                }

                if (closedGrammar && it.propertyPath == "m_Enabled")
                {
                    continue;
                }

                var value = ReadProperty(it.Copy(), resolveSceneRef);

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

            return fields;
        }

        // A field is unrepresentable in M3 if its value is Unsupported OR it is a List/Nested whose
        // recursion bottoms out in any Unsupported leaf (an object-reference array/struct). Such a
        // field must be skipped whole — partially rendering it emits uncompilable value tokens.
        private static bool ContainsUnsupported(ValueNode value) => value switch
        {
            ValueNode.Unsupported => true,
            ValueNode.List list => list.Items.Any(ContainsUnsupported),
            ValueNode.Nested nested => nested.Fields.Any(kv => ContainsUnsupported(kv.Value)),
            _ => false,
        };

        private static ValueNode ReadProperty(SerializedProperty p, Func<UnityEngine.Object, string?>? resolveSceneRef = null)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    return ValueNode.Primitive.Bool(p.boolValue);
                case SerializedPropertyType.Integer:
                    return p.type == "long"
                        ? ValueNode.Primitive.Long(p.longValue)
                        : ValueNode.Primitive.Int(p.intValue);
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
                    return p.isArray ? ReadList(p, resolveSceneRef) : ReadNested(p, resolveSceneRef);
                case SerializedPropertyType.ObjectReference:
                    // M4: an object-reference field pointing at a project asset becomes a
                    // ValueNode.AssetRef (populated), a null asset field becomes AssetRef(null) (None).
                    // M5: a scene-object reference becomes a ValueNode.ObjectRef (resolved via
                    // resolveSceneRef when supplied), and a null GameObject/Component-typed field
                    // becomes ObjectRef(null). Replaces the old blanket "object refs are unsupported"
                    // skip for asset-pointing refs.
                    return AssetReferenceResolver.ReadObjectReference(p, resolveSceneRef);
                default:
                    return new ValueNode.Unsupported(p.propertyType.ToString());
            }
        }

        private static ValueNode ReadEnum(SerializedProperty p)
        {
            var type = ResolveFieldType(p.serializedObject.targetObject, p.propertyPath);
            if (type == null || !type.IsEnum)
            {
                // A native serialized enum (no managed FieldInfo backs the path, e.g. Canvas.m_RenderMode):
                // read the raw value as an Int, the same shape WritePrimitive writes back through
                // SerializedProperty.intValue and the same shape `c.Set("m_RenderMode", 0)` authors.
                return ValueNode.Primitive.Int(p.intValue);
            }

            var isFlags = type.IsDefined(typeof(FlagsAttribute), false);
            if (isFlags)
            {
                var mask = (long)p.intValue;
                var members = new List<string>();
                foreach (var name in Enum.GetNames(type))
                {
                    var bits = Convert.ToInt64(Enum.Parse(type, name));
                    if (bits != 0 && (mask & bits) == bits)
                    {
                        members.Add(name);
                    }
                }

                return new ValueNode.Enum(type.FullName ?? type.Name, members, true);
            }

            var names = p.enumNames;
            var idx = p.enumValueIndex;
            var member = idx >= 0 && idx < names.Length ? names[idx] : "";
            return new ValueNode.Enum(type.FullName ?? type.Name, new[] { member }, false);
        }

        private static ValueNode ReadList(SerializedProperty p, Func<UnityEngine.Object, string?>? resolveSceneRef = null)
        {
            var items = new List<ValueNode>(p.arraySize);
            for (var i = 0; i < p.arraySize; i++)
            {
                items.Add(ReadProperty(p.GetArrayElementAtIndex(i).Copy(), resolveSceneRef));
            }

            return new ValueNode.List(items);
        }

        private static ValueNode ReadNested(SerializedProperty p, Func<UnityEngine.Object, string?>? resolveSceneRef = null)
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
                if (it.depth == childDepth)
                {
                    fields.Add(new KeyValuePair<string, ValueNode>(it.name, ReadProperty(it.Copy(), resolveSceneRef)));
                }
            }

            var type = ResolveFieldType(p.serializedObject.targetObject, p.propertyPath);
            if (type == null || type.IsGenericType)
            {
                // Cannot resolve to a concrete, non-generic managed type (e.g. a built-in native
                // field, or an unsupported generic serializable) — preserve verbatim, never emit
                // a broken Nested (e.g. a backtick-arity type name).
                return new ValueNode.Unsupported(p.type);
            }

            var typeName = type.FullName!.Replace('+', '.');
            return new ValueNode.Nested(typeName, new FieldMap(fields));
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

        private static void WriteProperty(SerializedProperty p, ValueNode value)
        {
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
                    p.arraySize = list.Items.Count;
                    for (var i = 0; i < list.Items.Count; i++)
                    {
                        WriteProperty(p.GetArrayElementAtIndex(i), list.Items[i]);
                    }

                    break;
                case ValueNode.Nested nested:
                    foreach (var (key, child) in nested.Fields)
                    {
                        var childProp = p.FindPropertyRelative(key);
                        if (childProp != null)
                        {
                            WriteProperty(childProp, child);
                        }
                    }

                    break;
                case ValueNode.Unsupported:
                    // No-op (flagged upstream); never overwrite an unsupported value.
                    break;
            }
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

        private static void WriteEnum(SerializedProperty p, ValueNode.Enum e)
        {
            if (e.IsFlags)
            {
                var type = ResolveFieldType(p.serializedObject.targetObject, p.propertyPath);
                if (type == null || !type.IsEnum)
                {
                    Debug.LogWarning($"[SceneBuilder] Could not resolve [Flags] enum type for '{p.propertyPath}'.");
                    return;
                }

                long mask = 0;
                foreach (var member in e.Members)
                {
                    mask |= Convert.ToInt64(Enum.Parse(type, member));
                }

                p.intValue = (int)mask;
                return;
            }

            if (e.Members.Count == 0)
            {
                return;
            }

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

            Debug.LogWarning($"[SceneBuilder] Enum member '{target}' not found on '{p.propertyPath}'.");
        }

        // ---- Reflection: resolve a serialized propertyPath to its managed field type ---------

        /// <summary>
        /// Walks a Unity serialized <paramref name="path"/> against <paramref name="root"/>'s managed
        /// type via reflection, returning the leaf field's <see cref="Type"/>, or null when the path
        /// has no managed C# field (e.g. a built-in native serialized field). Used to recover enum
        /// types (names/bits) that <see cref="SerializedProperty"/> alone does not expose.
        /// </summary>
        public static Type? ResolveFieldType(UnityEngine.Object root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            var type = root.GetType();
            var normalized = path.Replace(".Array.data[", "[");
            foreach (var rawElement in normalized.Split('.'))
            {
                var name = rawElement;
                var isElement = false;
                var bracket = name.IndexOf('[');
                if (bracket >= 0)
                {
                    name = name.Substring(0, bracket);
                    isElement = true;
                }

                var field = GetFieldRecursive(type!, name);
                if (field == null)
                {
                    return null;
                }

                type = field.FieldType;
                if (isElement)
                {
                    if (type.IsArray)
                    {
                        type = type.GetElementType();
                    }
                    else if (type.IsGenericType)
                    {
                        type = type.GetGenericArguments()[0];
                    }
                }
            }

            return type;
        }

        private static FieldInfo? GetFieldRecursive(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (Type? t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, flags);
                if (field != null)
                {
                    return field;
                }
            }

            return null;
        }
    }
}
