namespace SceneBuilder.DocGen
{
    /// <summary>
    /// Turns raw cref text into either an internal link (type slug + member anchor) or an external
    /// one. A cref that names neither the documented surface nor a Unity type renders as plain code,
    /// so an unresolvable reference degrades to readable text instead of a dead link.
    /// </summary>
    public sealed class CrefResolver
    {
        private const string UnityScriptReference = "https://docs.unity3d.com/ScriptReference/";

        private static readonly HashSet<string> Bcl = new(StringComparer.Ordinal)
        {
            "Action", "Func", "Task", "Type", "Nullable", "IEnumerable", "IReadOnlyList", "IList",
            "IDictionary", "Dictionary", "List", "Exception", "string", "int", "float", "double",
            "bool", "object", "void", "byte", "long", "short", "decimal", "char", "Span",
        };

        private readonly Dictionary<string, ApiType> _byKey = new(StringComparer.Ordinal);

        public CrefResolver(IEnumerable<ApiType> types)
        {
            foreach (var type in types)
            {
                _byKey[$"{type.Name}`{type.TypeParams.Count}"] = type;
                _byKey.TryAdd(type.Name, type);
            }
        }

        /// <summary>Rewrites every cref run in the document in place, once the full type set is known.</summary>
        public void ResolveDocument(ApiDocument document)
        {
            foreach (var type in document.Types) ResolveType(type);
            foreach (var diagnostic in document.Diagnostics) Resolve(diagnostic.Summary, null);
        }

        private void ResolveType(ApiType type)
        {
            Resolve(type.Summary, type);
            Resolve(type.Remarks, type);
            foreach (var parameter in type.TypeParams) Resolve(parameter.Summary, type);
            foreach (var value in type.EnumValues) Resolve(value.Summary, type);

            foreach (var member in type.Members)
            {
                Resolve(member.Summary, type);
                foreach (var overload in member.Overloads)
                {
                    Resolve(overload.Summary, type);
                    Resolve(overload.Remarks, type);
                    Resolve(overload.Returns, type);
                    foreach (var parameter in overload.Parameters) Resolve(parameter.Summary, type);
                    foreach (var parameter in overload.TypeParams) Resolve(parameter.Summary, type);
                }
            }

            foreach (var nested in type.NestedTypes) ResolveType(nested);
        }

        private void Resolve(List<DocNode> nodes, ApiType? containing)
        {
            foreach (var node in nodes)
            {
                if (node.Kind != "cref") continue;

                var (typeSlug, memberAnchor, label, href) = Lookup(node.Text, containing);
                node.Text = label;

                if (typeSlug is not null)
                {
                    node.Kind = "ref";
                    node.Type = typeSlug;
                    node.Member = memberAnchor;
                }
                else if (href is not null)
                {
                    node.Kind = "link";
                    node.Href = href;
                }
                else
                {
                    node.Kind = "code";
                }
            }
        }

        private (string? Type, string? Member, string Label, string? Href) Lookup(string cref, ApiType? containing)
        {
            var text = cref.Trim();
            var parenthesis = text.IndexOf('(');
            var path = parenthesis >= 0 ? text[..parenthesis] : text;

            var segments = SplitTopLevel(path);
            if (segments.Count == 0) return (null, null, text, null);

            var last = segments[^1];

            // Qualified: the segment before the last one names the type.
            if (segments.Count >= 2)
            {
                var owner = FindType(segments[^2]);
                if (owner is not null)
                {
                    var member = FindMember(owner, StripGenerics(last));
                    var label = $"{owner.Name}.{StripGenerics(last)}";
                    if (member is not null) return (owner.Id, member.Id, label, null);
                    return (owner.Id, null, label, null);
                }
            }

            // Unqualified: a type name, or a member of the type the comment sits on.
            var type = FindType(last);
            if (type is not null) return (type.Id, null, type.DisplayName, null);

            if (containing is not null)
            {
                var member = FindMember(containing, StripGenerics(last));
                if (member is not null) return (containing.Id, member.Id, StripGenerics(last), null);
            }

            var simple = StripGenerics(last);
            if (Bcl.Contains(simple) || path.StartsWith("System.", StringComparison.Ordinal))
                return (null, null, text, null);

            return (null, null, simple, $"{UnityScriptReference}{simple}.html");
        }

        private ApiType? FindType(string segment)
        {
            var name = StripGenerics(segment);
            var arity = Arity(segment);

            if (arity > 0 && _byKey.TryGetValue($"{name}`{arity}", out var generic)) return generic;
            return _byKey.TryGetValue(name, out var plain) ? plain : null;
        }

        private static ApiMember? FindMember(ApiType type, string name) =>
            type.Members.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));

        private static string StripGenerics(string segment)
        {
            var index = segment.IndexOf('<');
            return index < 0 ? segment : segment[..index];
        }

        private static int Arity(string segment)
        {
            var index = segment.IndexOf('<');
            if (index < 0) return 0;

            var depth = 0;
            var count = 1;
            for (var i = index; i < segment.Length; i++)
            {
                switch (segment[i])
                {
                    case '<': depth++; break;
                    case '>': depth--; if (depth == 0) return count; break;
                    case ',': if (depth == 1) count++; break;
                }
            }

            return count;
        }

        /// <summary>Splits on dots that sit outside generic argument lists.</summary>
        private static List<string> SplitTopLevel(string path)
        {
            var segments = new List<string>();
            var depth = 0;
            var start = 0;

            for (var i = 0; i < path.Length; i++)
            {
                switch (path[i])
                {
                    case '<': depth++; break;
                    case '>': depth--; break;
                    case '.':
                        if (depth == 0)
                        {
                            segments.Add(path[start..i]);
                            start = i + 1;
                        }

                        break;
                }
            }

            if (start < path.Length) segments.Add(path[start..]);
            return segments.Where(s => s.Length > 0).ToList();
        }
    }
}
