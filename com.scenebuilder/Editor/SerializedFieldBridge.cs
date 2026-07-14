#nullable enable
using System;
using System.Collections.Generic;
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
    /// <see cref="SerializedPropertyType"/> per M3's table. Bookkeeping properties are skipped on read.
    /// </summary>
    public static class SerializedFieldBridge
    {
        // Internal Unity bookkeeping — never surfaced as a field (would corrupt diffs).
        private static readonly HashSet<string> Bookkeeping = new()
        {
            "m_Script",
            "m_ObjectHideFlags",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_GameObject",
        };

        // ---- Read (component -> ComponentData) ---------------------------------------------

        public static ComponentData ReadComponent(Component component)
        {
            var so = new SerializedObject(component);
            var fields = new List<KeyValuePair<string, ValueNode>>();

            var it = so.GetIterator();
            var enterChildren = true;
            while (it.NextVisible(enterChildren))
            {
                enterChildren = false; // top-level visible properties only; nesting handled in ReadProperty
                if (Bookkeeping.Contains(it.propertyPath))
                {
                    continue;
                }

                var value = ReadProperty(it.Copy());

                // Field types M3 cannot represent — object/asset references (mesh, material, physics
                // material) and LayerMask are M4+ — are SKIPPED, never written. Emitting them would
                // produce uncompilable tokens (a bare `ObjectReference` / `LayerMask` identifier).
                // Spec 04: an Unsupported field is not written to source, only flagged.
                if (value is ValueNode.Unsupported)
                {
                    continue;
                }

                fields.Add(new KeyValuePair<string, ValueNode>(it.propertyPath, value));
            }

            return new ComponentData
            {
                Type = new TypeRef(component.GetType().FullName),
                Fields = new FieldMap(fields),
            };
        }

        private static ValueNode ReadProperty(SerializedProperty p)
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
                    return p.isArray ? ReadList(p) : ReadNested(p);
                default:
                    return new ValueNode.Unsupported(p.propertyType.ToString());
            }
        }

        private static ValueNode ReadEnum(SerializedProperty p)
        {
            var type = ResolveFieldType(p.serializedObject.targetObject, p.propertyPath);
            if (type == null || !type.IsEnum)
            {
                // Cannot resolve the managed enum type (e.g. built-in native field) — preserve verbatim.
                return new ValueNode.Unsupported(p.intValue.ToString());
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

        private static ValueNode ReadList(SerializedProperty p)
        {
            var items = new List<ValueNode>(p.arraySize);
            for (var i = 0; i < p.arraySize; i++)
            {
                items.Add(ReadProperty(p.GetArrayElementAtIndex(i).Copy()));
            }

            return new ValueNode.List(items);
        }

        private static ValueNode ReadNested(SerializedProperty p)
        {
            var fields = new List<KeyValuePair<string, ValueNode>>();
            var it = p.Copy();
            var end = p.GetEndProperty();
            var childDepth = p.depth + 1;
            var enterChildren = true;
            while (it.NextVisible(enterChildren) && !SerializedProperty.EqualContents(it, end))
            {
                enterChildren = false;
                if (it.depth == childDepth)
                {
                    fields.Add(new KeyValuePair<string, ValueNode>(it.name, ReadProperty(it.Copy())));
                }
            }

            return new ValueNode.Nested(new FieldMap(fields));
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
