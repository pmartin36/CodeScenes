#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// THE single decision of what managed member/type backs a component's serialized path. Pure
    /// reflection over <c>(Type componentType, string serializedPath)</c> — no
    /// <see cref="UnityEditor.SerializedProperty"/>, no probe GameObject — so the read side
    /// (<see cref="SerializedFieldBridge.ReadEnum"/> / <see cref="SerializedFieldBridge.ReadProperty"/>'s
    /// Integer arm), the write side (<see cref="SerializedFieldBridge.WriteEnum"/>) and the
    /// source-side normaliser (<see cref="SerializedEnumNormalizer"/>) all resolve identically by
    /// construction — a live property is available on two of those three sites and not the third, so
    /// any resolution built on one would let the three disagree.
    /// </summary>
    /// <remarks>
    /// The enum ladder, in order: a managed <see cref="FieldInfo"/> walk (a user-authored serializable
    /// field) -&gt; an exact <c>m_Xxx</c>-&gt;<c>xxx</c> public instance property, required to be
    /// enum-typed and non-obsolete (a native field whose backing member is a property, e.g.
    /// <c>Canvas.m_RenderMode</c>-&gt;<c>renderMode</c>) -&gt; a UNIQUE non-obsolete public instance
    /// enum-typed property whose name starts with the conventional name (e.g.
    /// <c>Rigidbody.m_CollisionDetection</c>-&gt;<c>collisionDetectionMode</c>) -&gt; unresolved. An
    /// exact-name hit that is not enum-typed (e.g. <c>Renderer.m_CastShadows</c>-&gt;<c>castShadows</c>,
    /// a <c>bool</c>) is REJECTED, not accepted mistyped — the ladder keeps walking to the prefix step.
    /// Unresolved means "not an enum field": every caller keeps the raw-int behaviour.
    /// </remarks>
    internal static class SerializedMemberMap
    {
        private static readonly Dictionary<(Type ComponentType, string Path), Type?> EnumTypeCache = new();

        // Bidirectional serialized-name <-> public-member spelling map for a nested struct/class
        // TYPE (e.g. UnityEngine.UI.ColorBlock), cached per Type per domain. Built once from the
        // type's own instance fields -- the serialized child name Unity reports for a nested value IS
        // the field's reflected name, so no path walk is needed here (contrast ResolveManagedFieldType
        // above, which walks a component's dotted/indexed path).
        private sealed class MemberMapEntry
        {
            internal readonly Dictionary<string, string> SerializedToPublic = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> PublicToSerialized = new(StringComparer.Ordinal);
        }

        private static readonly Dictionary<Type, MemberMapEntry> MemberMapCache = new();

        /// <summary>
        /// Walks a Unity serialized <paramref name="path"/> against <paramref name="componentType"/>
        /// via reflection, returning the leaf field's <see cref="Type"/>, or null when the path has no
        /// managed C# field (e.g. a built-in native serialized field).
        /// </summary>
        internal static Type? ResolveManagedFieldType(Type componentType, string path)
        {
            if (componentType == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            Type? type = componentType;
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

        /// <summary>
        /// True when <paramref name="serializedPath"/> is backed by a managed field on
        /// <paramref name="componentType"/> (a user-authored serializable field the selector identifier
        /// names) but has no compiling public spelling (<see cref="TryPublicMemberName"/> false) — a
        /// typed selector naming it would not compile. A NATIVE field (no managed <see cref="FieldInfo"/>
        /// backing it at all, e.g. <c>Rigidbody.m_Mass</c>) is never marked: its accessible spelling
        /// (<c>mass</c>) is a property with no field of its own, so a typed selector over it compiles.
        /// </summary>
        internal static bool IsInaccessibleViaSelector(Type componentType, string serializedPath) =>
            GetFieldRecursive(componentType, serializedPath) != null
            && !TryPublicMemberName(componentType, serializedPath, out _);

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

        /// <summary>
        /// Resolves the enum <see cref="Type"/> backing <paramref name="path"/> on
        /// <paramref name="componentType"/> via the ladder above, or null when nothing in the ladder
        /// resolves to an enum. Cached per <c>(Type, path)</c>.
        /// </summary>
        internal static Type? ResolveEnumType(Type componentType, string path)
        {
            var key = (componentType, path);
            if (EnumTypeCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var resolved = ResolveEnumTypeCore(componentType, path);
            EnumTypeCache[key] = resolved;
            return resolved;
        }

        private static Type? ResolveEnumTypeCore(Type componentType, string path)
        {
            var fieldType = ResolveManagedFieldType(componentType, path);
            if (fieldType != null && fieldType.IsEnum)
            {
                return fieldType;
            }

            // The property ladder answers for a TOP-LEVEL serialized path only: a nested struct
            // field's conventional property would need the struct's own instance, not the component's.
            if (path.IndexOf('.') >= 0)
            {
                return null;
            }

            var conventional = ConventionalName(path);
            if (conventional == null)
            {
                return null;
            }

            var exact = componentType.GetProperty(conventional, BindingFlags.Public | BindingFlags.Instance);
            if (exact != null && exact.PropertyType.IsEnum && !IsObsolete(exact))
            {
                return exact.PropertyType;
            }

            PropertyInfo? uniqueMatch = null;
            foreach (var prop in componentType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.PropertyType.IsEnum || IsObsolete(prop) || prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (!prop.Name.StartsWith(conventional, StringComparison.Ordinal))
                {
                    continue;
                }

                if (uniqueMatch != null)
                {
                    // Ambiguous — never guess between two candidate properties.
                    return null;
                }

                uniqueMatch = prop;
            }

            return uniqueMatch?.PropertyType;
        }

        private static bool IsObsolete(MemberInfo member) =>
            member.GetCustomAttribute<ObsoleteAttribute>(true) != null;

        // The one m_Xxx -> xxx derivation, shared by the enum ladder above and the member map below.
        // Returns null when `serializedName` does not follow the "m_" + capitalized-name convention
        // (too short, or no "m_" prefix) -- never guesses at a spelling.
        private static string? ConventionalName(string serializedName)
        {
            if (!serializedName.StartsWith("m_", StringComparison.Ordinal) || serializedName.Length < 3)
            {
                return null;
            }

            return char.ToLowerInvariant(serializedName[2]) + serializedName.Substring(3);
        }

        /// <summary>
        /// Resolves the compiling public spelling of a nested struct/class member, given the
        /// declaring struct/class <paramref name="declaringType"/> and the Unity-reported serialized
        /// child name. A public field's own name is its spelling; a private
        /// <c>[SerializeField]</c> field's spelling is the exact conventional
        /// <c>m_Xxx</c>-&gt;<c>xxx</c> property when a public instance property by that name exists,
        /// is non-obsolete, non-indexed, has a PUBLIC setter, and its type matches the field's
        /// EXACTLY. Returns false when no compiling spelling exists.
        /// </summary>
        internal static bool TryPublicMemberName(Type declaringType, string serializedName, out string member)
        {
            member = null!;
            if (declaringType == null || string.IsNullOrEmpty(serializedName))
            {
                return false;
            }

            return GetOrBuildMemberMap(declaringType).SerializedToPublic.TryGetValue(serializedName, out member!);
        }

        /// <summary>
        /// The inverse of <see cref="TryPublicMemberName"/>: resolves a public member spelling back
        /// to the serialized child name it backs, for writing through
        /// <see cref="UnityEditor.SerializedProperty.FindPropertyRelative"/>.
        /// </summary>
        internal static bool TrySerializedName(Type declaringType, string memberName, out string serializedName)
        {
            serializedName = null!;
            if (declaringType == null || string.IsNullOrEmpty(memberName))
            {
                return false;
            }

            return GetOrBuildMemberMap(declaringType).PublicToSerialized.TryGetValue(memberName, out serializedName!);
        }

        /// <summary>
        /// Composes the component-rooted serialized path of a nested member: maps
        /// <paramref name="memberKey"/> (a public spelling) back through
        /// <see cref="TrySerializedName"/> on <paramref name="declaringType"/>, falling back to
        /// <paramref name="memberKey"/> verbatim when it is not a known public spelling (a
        /// raw-serialized-name authored value), and appends it to <paramref name="parentPath"/>. The
        /// one place a child serialized path is built, so a caller can never compose one by hand.
        /// </summary>
        internal static string ChildPath(Type declaringType, string parentPath, string memberKey)
        {
            var serializedName = TrySerializedName(declaringType, memberKey, out var mapped) ? mapped : memberKey;
            return parentPath + "." + serializedName;
        }

        /// <summary>
        /// Unity's serialized element path for ANY element of the array/list at
        /// <paramref name="parentPath"/> (<c>m_Foo</c> -&gt; <c>m_Foo.Array.data[0]</c>). Index 0 is
        /// used for every element deliberately: elements are homogeneous,
        /// <see cref="ResolveManagedFieldType"/> resolves the element type from it, and a per-index
        /// path would grow the per-(Type, path) <see cref="EnumTypeCache"/> without bound on a large
        /// array.
        /// </summary>
        internal static string ElementPath(string parentPath) => parentPath + ".Array.data[0]";

        private static MemberMapEntry GetOrBuildMemberMap(Type declaringType)
        {
            if (!MemberMapCache.TryGetValue(declaringType, out var entry))
            {
                entry = BuildMemberMap(declaringType);
                MemberMapCache[declaringType] = entry;
            }

            return entry;
        }

        private static MemberMapEntry BuildMemberMap(Type declaringType)
        {
            var entry = new MemberMapEntry();
            const BindingFlags fieldFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var field in declaringType.GetFields(fieldFlags))
            {
                string? publicSpelling = null;

                if (field.IsPublic)
                {
                    publicSpelling = field.Name;
                }
                else
                {
                    var conventional = ConventionalName(field.Name);
                    if (conventional != null)
                    {
                        var prop = declaringType.GetProperty(conventional, BindingFlags.Public | BindingFlags.Instance);
                        var setter = prop?.GetSetMethod();
                        if (prop != null && !IsObsolete(prop) && prop.GetIndexParameters().Length == 0
                            && setter is { IsPublic: true } && prop.PropertyType == field.FieldType)
                        {
                            publicSpelling = conventional;
                        }
                    }
                }

                if (publicSpelling == null)
                {
                    continue;
                }

                entry.SerializedToPublic[field.Name] = publicSpelling;

                // First-wins on a (shouldn't-happen) reverse collision -- never overwrite an
                // already-resolved reverse mapping with a second field claiming the same spelling.
                if (!entry.PublicToSerialized.ContainsKey(publicSpelling))
                {
                    entry.PublicToSerialized[publicSpelling] = field.Name;
                }
            }

            return entry;
        }

        /// <summary>
        /// Resolves <paramref name="rawValue"/> on <paramref name="path"/>/<paramref name="componentType"/>
        /// to a canonical <see cref="ValueNode.Enum"/> node, or returns false when the path's backing
        /// enum type does not resolve or the value cannot be losslessly expressed by named members. An
        /// exact <see cref="Enum.GetName(Type, object)"/> hit is tried first; otherwise the value is
        /// decomposed into the members whose bits are a subset, and the OR of those members MUST
        /// reconstruct the value exactly — a decomposition that loses bits (or names nothing) returns
        /// false rather than emit a lossy node.
        /// </summary>
        internal static bool TryEnumNode(Type componentType, string path, int rawValue, out ValueNode node)
        {
            node = null!;

            var type = ResolveEnumType(componentType, path);
            if (type == null)
            {
                return false;
            }

            var exactName = Enum.GetName(type, rawValue);
            if (exactName != null)
            {
                node = ValueNode.Enum.Canonical(type.FullName ?? type.Name, new[] { exactName });
                return true;
            }

            var members = DecomposeFlags(type, rawValue, out var reconstructed);
            if (members.Count == 0 || reconstructed != rawValue)
            {
                return false;
            }

            node = ValueNode.Enum.Canonical(type.FullName ?? type.Name, members);
            return true;
        }

        private static List<string> DecomposeFlags(Type type, int rawValue, out long reconstructed)
        {
            var members = new List<string>();
            long mask = 0;
            var wanted = (long)rawValue;

            foreach (var name in Enum.GetNames(type))
            {
                var bits = Convert.ToInt64(Enum.Parse(type, name));
                if (bits != 0 && (wanted & bits) == bits)
                {
                    members.Add(name);
                    mask |= bits;
                }
            }

            reconstructed = mask;
            return members;
        }

        /// <summary>
        /// Resolves <paramref name="members"/> against <paramref name="enumType"/> to the OR'd raw
        /// <c>int</c> value (correct for one member or many — flags composition is just an OR), or
        /// returns false when the member set is empty or any member fails to parse. Never guesses.
        /// </summary>
        internal static bool TryEnumValue(Type enumType, IReadOnlyList<string> members, out int value)
        {
            value = 0;

            if (members.Count == 0)
            {
                return false;
            }

            long mask = 0;
            foreach (var member in members)
            {
                if (!Enum.TryParse(enumType, member, out var parsed))
                {
                    return false;
                }

                mask |= Convert.ToInt64(parsed);
            }

            value = unchecked((int)mask);
            return true;
        }
    }
}
