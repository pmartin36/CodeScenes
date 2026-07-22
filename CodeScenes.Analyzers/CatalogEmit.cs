using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CodeScenes.Analyzers
{
    /// <summary>
    /// Reusable manifest-JSON -> typed-catalog pipeline (b3-t2). Dependency-free: no
    /// System.Text.Json, no reference to SceneBuilder.Core (ns2.0 host constraint — see
    /// .agent_handoffs/codescenes-analyzers/b3-t2/research.md). Mirrors the on-disk shape of
    /// SceneBuilder.Core/Model/ProjectCatalogManifest.cs by schema, not by shared type. Spec 25's
    /// PrefabFacadeGenerator becomes a second consumer of this module.
    /// </summary>
    internal static class CatalogEmit
    {
        public static bool TryParseManifest(
            string json,
            out IReadOnlyList<string> tags,
            out IReadOnlyList<(int Index, string Name)> layers)
        {
            tags = Array.Empty<string>();
            layers = Array.Empty<(int, string)>();

            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                var root = MiniJson.Parse(json);
                if (root is not Dictionary<string, object?> obj)
                    return false;

                var parsedTags = new List<string>();
                if (obj.TryGetValue("tags", out var tagsVal) && tagsVal is not null)
                {
                    if (tagsVal is not List<object?> tagsList)
                        return false;
                    foreach (var t in tagsList)
                    {
                        if (t is not string s)
                            return false;
                        parsedTags.Add(s);
                    }
                }

                var parsedLayers = new List<(int, string)>();
                if (obj.TryGetValue("layers", out var layersVal) && layersVal is not null)
                {
                    if (layersVal is not List<object?> layersList)
                        return false;
                    foreach (var l in layersList)
                    {
                        if (l is not Dictionary<string, object?> layerObj)
                            return false;
                        if (!layerObj.TryGetValue("index", out var idxVal) || idxVal is not double idxD)
                            return false;
                        if (!layerObj.TryGetValue("name", out var nameVal) || nameVal is not string name)
                            return false;
                        parsedLayers.Add(((int)idxD, name));
                    }
                }

                tags = parsedTags;
                layers = parsedLayers;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
            "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this",
            "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "using", "virtual", "void", "volatile", "while",
        };

        public static string SanitizeIdentifier(string raw)
        {
            var sb = new StringBuilder(string.IsNullOrEmpty(raw) ? 1 : raw.Length);
            foreach (var c in raw)
            {
                sb.Append(IsValidIdentifierChar(c) ? c : '_');
            }

            var result = sb.ToString();
            if (result.Length == 0 || char.IsDigit(result[0]))
                result = "_" + result;

            if (CSharpKeywords.Contains(result))
                result = "@" + result;

            return result;
        }

        private static bool IsValidIdentifierChar(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';

        public static string EscapeStringLiteral(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\0': sb.Append("\\0"); break;
                    default:
                        if (char.IsControl(c))
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }

        public static string EmitTags(IReadOnlyList<string> tags)
        {
            var allocator = new IdentifierAllocator();
            var sb = new StringBuilder();
            sb.Append("// <auto-generated/> — CodeScenes Tags catalog. Do not edit.\n");
            sb.Append("public static class Tags\n{\n");
            foreach (var tag in tags)
            {
                var ident = allocator.Allocate(SanitizeIdentifier(tag));
                sb.Append("    public const string ").Append(ident)
                    .Append(" = \"").Append(EscapeStringLiteral(tag)).Append("\";\n");
            }

            sb.Append("}\n");
            return sb.ToString();
        }

        public static string EmitLayers(IReadOnlyList<(int Index, string Name)> layers)
        {
            var allocator = new IdentifierAllocator();
            var idents = new string[layers.Count];
            for (var i = 0; i < layers.Count; i++)
            {
                idents[i] = allocator.Allocate(SanitizeIdentifier(layers[i].Name));
            }

            var sb = new StringBuilder();
            sb.Append("// <auto-generated/> — CodeScenes Layers catalog. Do not edit.\n");
            sb.Append("public static class Layers\n{\n");
            for (var i = 0; i < layers.Count; i++)
            {
                sb.Append("    public const int ").Append(idents[i])
                    .Append(" = ").Append(layers[i].Index.ToString(CultureInfo.InvariantCulture)).Append(";\n");
            }

            sb.Append("\n    public static class Name\n    {\n");
            for (var i = 0; i < layers.Count; i++)
            {
                sb.Append("        public const string ").Append(idents[i])
                    .Append(" = \"").Append(EscapeStringLiteral(layers[i].Name)).Append("\";\n");
            }

            sb.Append("    }\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        internal sealed class IdentifierAllocator
        {
            private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

            public string Allocate(string sanitized)
            {
                if (!_counts.TryGetValue(sanitized, out var count))
                {
                    _counts[sanitized] = 1;
                    return sanitized;
                }

                count++;
                _counts[sanitized] = count;
                return sanitized + "_" + count.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Minimal, dependency-free recursive-descent JSON reader. Produces
        /// <see cref="Dictionary{TKey,TValue}"/> (object), <see cref="List{T}"/> (array),
        /// <see cref="string"/>, <see cref="double"/>, <see cref="bool"/> or null (JSON null).
        /// Throws <see cref="FormatException"/> on any structural error.
        /// </summary>
        internal static class MiniJson
        {
            public static object? Parse(string json)
            {
                var pos = 0;
                var value = ParseValue(json, ref pos);
                SkipWhitespace(json, ref pos);
                if (pos != json.Length)
                    throw new FormatException("Trailing content after JSON value.");
                return value;
            }

            private static object? ParseValue(string s, ref int pos)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length)
                    throw new FormatException("Unexpected end of JSON.");

                var c = s[pos];
                switch (c)
                {
                    case '{': return ParseObject(s, ref pos);
                    case '[': return ParseArray(s, ref pos);
                    case '"': return ParseString(s, ref pos);
                    case 't':
                        Expect(s, ref pos, "true");
                        return true;
                    case 'f':
                        Expect(s, ref pos, "false");
                        return false;
                    case 'n':
                        Expect(s, ref pos, "null");
                        return null;
                    default:
                        return ParseNumber(s, ref pos);
                }
            }

            private static void Expect(string s, ref int pos, string literal)
            {
                if (pos + literal.Length > s.Length || string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
                    throw new FormatException("Expected '" + literal + "'.");
                pos += literal.Length;
            }

            private static Dictionary<string, object?> ParseObject(string s, ref int pos)
            {
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                pos++; // '{'
                SkipWhitespace(s, ref pos);
                if (pos < s.Length && s[pos] == '}')
                {
                    pos++;
                    return dict;
                }

                while (true)
                {
                    SkipWhitespace(s, ref pos);
                    if (pos >= s.Length || s[pos] != '"')
                        throw new FormatException("Expected string key.");
                    var key = ParseString(s, ref pos);

                    SkipWhitespace(s, ref pos);
                    if (pos >= s.Length || s[pos] != ':')
                        throw new FormatException("Expected ':'.");
                    pos++;

                    var value = ParseValue(s, ref pos);
                    dict[key] = value;

                    SkipWhitespace(s, ref pos);
                    if (pos >= s.Length)
                        throw new FormatException("Unexpected end of object.");
                    if (s[pos] == ',')
                    {
                        pos++;
                        continue;
                    }

                    if (s[pos] == '}')
                    {
                        pos++;
                        break;
                    }

                    throw new FormatException("Expected ',' or '}'.");
                }

                return dict;
            }

            private static List<object?> ParseArray(string s, ref int pos)
            {
                var list = new List<object?>();
                pos++; // '['
                SkipWhitespace(s, ref pos);
                if (pos < s.Length && s[pos] == ']')
                {
                    pos++;
                    return list;
                }

                while (true)
                {
                    var value = ParseValue(s, ref pos);
                    list.Add(value);

                    SkipWhitespace(s, ref pos);
                    if (pos >= s.Length)
                        throw new FormatException("Unexpected end of array.");
                    if (s[pos] == ',')
                    {
                        pos++;
                        continue;
                    }

                    if (s[pos] == ']')
                    {
                        pos++;
                        break;
                    }

                    throw new FormatException("Expected ',' or ']'.");
                }

                return list;
            }

            private static string ParseString(string s, ref int pos)
            {
                pos++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (pos >= s.Length)
                        throw new FormatException("Unterminated string.");
                    var c = s[pos++];
                    if (c == '"')
                        break;

                    if (c == '\\')
                    {
                        if (pos >= s.Length)
                            throw new FormatException("Unterminated escape sequence.");
                        var esc = s[pos++];
                        switch (esc)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (pos + 4 > s.Length)
                                    throw new FormatException("Invalid unicode escape.");
                                var hex = s.Substring(pos, 4);
                                if (!ushort.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                                    throw new FormatException("Invalid unicode escape.");
                                sb.Append((char)code);
                                pos += 4;
                                break;
                            default:
                                throw new FormatException("Invalid escape character: " + esc);
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }

                return sb.ToString();
            }

            private static double ParseNumber(string s, ref int pos)
            {
                var start = pos;
                if (pos < s.Length && (s[pos] == '-' || s[pos] == '+'))
                    pos++;
                while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E' ||
                                           s[pos] == '+' || s[pos] == '-'))
                    pos++;

                if (pos == start)
                    throw new FormatException("Invalid number.");

                var numStr = s.Substring(start, pos - start);
                if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
                    throw new FormatException("Invalid number: " + numStr);

                return result;
            }

            private static void SkipWhitespace(string s, ref int pos)
            {
                while (pos < s.Length && char.IsWhiteSpace(s[pos]))
                    pos++;
            }
        }
    }
}
