using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.DocGen
{
    /// <summary>
    /// Reads the public authoring surface out of source syntax. Members keep their source order,
    /// which for a fluent builder is the order the author intended them to be read in.
    /// </summary>
    public static class TypeExtractor
    {
        public static List<ApiType> FromDirectory(string directory, string repoRoot)
        {
            var files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal);

            // A partial type is declared across several files; each yields one ApiType that we then
            // merge into a single entry keyed by Id (namespace-flat name + generic arity). Insertion
            // order is preserved so the emitted list stays file-then-declaration ordered.
            var byId = new Dictionary<string, List<ApiType>>();
            var order = new List<string>();

            foreach (var file in files)
            {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
                var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

                foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (declaration.Parent is BaseTypeDeclarationSyntax) continue;
                    if (!IsPublic(declaration, insideInterface: false)) continue;

                    var part = ReadType(declaration, relative);
                    if (!byId.TryGetValue(part.Id, out var parts))
                    {
                        parts = new List<ApiType>();
                        byId[part.Id] = parts;
                        order.Add(part.Id);
                    }

                    parts.Add(part);
                }
            }

            return order.Select(id =>
            {
                var parts = byId[id];
                return parts.Count == 1 ? parts[0] : MergePartials(parts);
            }).ToList();
        }

        /// <summary>
        /// Folds the several declarations of one partial type into a single <see cref="ApiType"/>:
        /// members re-group across the parts by (Name, Kind) so overloads still collapse, nested
        /// types and enum values union, and the richest metadata wins — the base list and summary
        /// from whichever part carries one.
        /// </summary>
        private static ApiType MergePartials(List<ApiType> parts)
        {
            // The base-list-bearing declaration is the "body" file; take its shell metadata.
            var primary = parts.FirstOrDefault(p => p.BaseType is not null) ?? parts[0];

            var merged = new ApiType
            {
                Id = primary.Id,
                Name = primary.Name,
                DisplayName = primary.DisplayName,
                Kind = primary.Kind,
                Signature = primary.Signature,
                BaseType = primary.BaseType,
                Category = parts.Select(p => p.Category).FirstOrDefault(c => c is not null),
                SourcePath = primary.SourcePath,
                TypeParams = primary.TypeParams,
            };

            merged.Summary.AddRange((parts.FirstOrDefault(p => p.Summary.Count > 0) ?? primary).Summary);
            merged.Remarks.AddRange((parts.FirstOrDefault(p => p.Remarks.Count > 0) ?? primary).Remarks);

            foreach (var group in parts.SelectMany(p => p.Members).GroupBy(m => (m.Name, m.Kind)))
            {
                var first = group.First();
                var entry = new ApiMember
                {
                    Name = first.Name,
                    Kind = first.Kind,
                    Id = first.Id,
                    IsStatic = group.All(m => m.IsStatic),
                };
                foreach (var member in group) entry.Overloads.AddRange(member.Overloads);
                var withSummary = group.FirstOrDefault(m => m.Summary.Count > 0);
                if (withSummary is not null) entry.Summary.AddRange(withSummary.Summary);
                merged.Members.Add(entry);
            }

            merged.NestedTypes.AddRange(parts.SelectMany(p => p.NestedTypes));
            merged.EnumValues.AddRange(parts.SelectMany(p => p.EnumValues));

            return merged;
        }

        private static ApiType ReadType(BaseTypeDeclarationSyntax declaration, string sourcePath)
        {
            var docs = DocComments.Read(declaration);
            var name = declaration.Identifier.ValueText;
            var typeParams = TypeParamsOf(declaration, docs);

            var type = new ApiType
            {
                Name = name,
                DisplayName = typeParams.Count == 0
                    ? name
                    : $"{name}<{string.Join(", ", typeParams.Select(p => p.Name))}>",
                Id = Slug.ForType(name, typeParams.Count),
                Kind = KindOf(declaration),
                Signature = TypeSignature(declaration),
                BaseType = declaration.BaseList?.Types.FirstOrDefault()?.Type.ToString(),
                Category = CategoryOf(declaration),
                SourcePath = sourcePath,
                TypeParams = typeParams,
            };

            type.Summary.AddRange(docs.Summary);
            type.Remarks.AddRange(docs.Remarks);

            if (declaration is EnumDeclarationSyntax enumDeclaration)
            {
                foreach (var value in enumDeclaration.Members)
                {
                    var valueDocs = DocComments.Read(value);
                    var entry = new ApiEnumValue
                    {
                        Name = value.Identifier.ValueText,
                        Value = value.EqualsValue?.Value.ToString(),
                    };
                    entry.Summary.AddRange(valueDocs.Summary);
                    type.EnumValues.Add(entry);
                }

                return type;
            }

            if (declaration is not TypeDeclarationSyntax body) return type;

            var insideInterface = body is InterfaceDeclarationSyntax;
            var members = new List<(string Name, string Kind, bool IsStatic, ApiOverload Overload)>();

            foreach (var member in body.Members)
            {
                if (member is BaseTypeDeclarationSyntax nested)
                {
                    if (IsPublic(nested, insideInterface)) type.NestedTypes.Add(ReadType(nested, sourcePath));
                    continue;
                }

                if (!IsPublic(member, insideInterface)) continue;
                members.AddRange(ReadMember(member, name));
            }

            foreach (var group in members.GroupBy(m => (m.Name, m.Kind)))
            {
                var first = group.First();
                var entry = new ApiMember
                {
                    Name = first.Name,
                    Kind = first.Kind,
                    Id = Slug.ForMember(first.Name),
                    IsStatic = group.All(m => m.IsStatic),
                };
                entry.Overloads.AddRange(group.Select(m => m.Overload));
                entry.Summary.AddRange(
                    entry.Overloads.FirstOrDefault(o => o.Summary.Count > 0)?.Summary ?? new List<DocNode>());
                type.Members.Add(entry);
            }

            return type;
        }

        private static IEnumerable<(string, string, bool, ApiOverload)> ReadMember(
            MemberDeclarationSyntax member,
            string typeName)
        {
            var docs = DocComments.Read(member);
            var isStatic = member.Modifiers.Any(SyntaxKind.StaticKeyword);

            switch (member)
            {
                case MethodDeclarationSyntax method:
                {
                    var overload = new ApiOverload
                    {
                        Signature = MethodSignature(method),
                        ReturnType = method.ReturnType.ToString(),
                        Parameters = ParametersOf(method.ParameterList, docs),
                        TypeParams = TypeParamsOf(method.TypeParameterList, method.ConstraintClauses, docs),
                    };
                    Fill(overload, docs);
                    yield return (method.Identifier.ValueText, "method", isStatic, overload);
                    break;
                }

                case ConstructorDeclarationSyntax constructor:
                {
                    var overload = new ApiOverload
                    {
                        Signature = $"{Modifiers(constructor)} {constructor.Identifier.ValueText}({Parameters(constructor.ParameterList)})".Trim(),
                        Parameters = ParametersOf(constructor.ParameterList, docs),
                    };
                    Fill(overload, docs);
                    yield return (constructor.Identifier.ValueText, "constructor", isStatic, overload);
                    break;
                }

                case PropertyDeclarationSyntax property:
                {
                    var overload = new ApiOverload
                    {
                        Signature = $"{Modifiers(property)} {property.Type} {property.Identifier.ValueText} {Accessors(property.AccessorList, property.ExpressionBody)}".Trim(),
                        ReturnType = property.Type.ToString(),
                    };
                    Fill(overload, docs);
                    yield return (property.Identifier.ValueText, "property", isStatic, overload);
                    break;
                }

                case IndexerDeclarationSyntax indexer:
                {
                    var overload = new ApiOverload
                    {
                        Signature = $"{Modifiers(indexer)} {indexer.Type} this[{Parameters(indexer.ParameterList)}] {Accessors(indexer.AccessorList, indexer.ExpressionBody)}".Trim(),
                        ReturnType = indexer.Type.ToString(),
                        Parameters = ParametersOf(indexer.ParameterList, docs),
                    };
                    Fill(overload, docs);
                    yield return ("this[]", "indexer", isStatic, overload);
                    break;
                }

                case FieldDeclarationSyntax field:
                {
                    foreach (var variable in field.Declaration.Variables)
                    {
                        var overload = new ApiOverload
                        {
                            Signature = $"{Modifiers(field)} {field.Declaration.Type} {variable.Identifier.ValueText}".Trim(),
                            ReturnType = field.Declaration.Type.ToString(),
                        };
                        Fill(overload, docs);
                        yield return (variable.Identifier.ValueText, "field", isStatic, overload);
                    }

                    break;
                }

                case EventFieldDeclarationSyntax evt:
                {
                    foreach (var variable in evt.Declaration.Variables)
                    {
                        var overload = new ApiOverload
                        {
                            Signature = $"{Modifiers(evt)} event {evt.Declaration.Type} {variable.Identifier.ValueText}".Trim(),
                            ReturnType = evt.Declaration.Type.ToString(),
                        };
                        Fill(overload, docs);
                        yield return (variable.Identifier.ValueText, "event", isStatic, overload);
                    }

                    break;
                }

                case OperatorDeclarationSyntax op:
                {
                    var overload = new ApiOverload
                    {
                        Signature = $"{Modifiers(op)} {op.ReturnType} operator {op.OperatorToken.ValueText}({Parameters(op.ParameterList)})".Trim(),
                        ReturnType = op.ReturnType.ToString(),
                        Parameters = ParametersOf(op.ParameterList, docs),
                    };
                    Fill(overload, docs);
                    yield return ($"operator {op.OperatorToken.ValueText}", "operator", true, overload);
                    break;
                }
            }
        }

        private static void Fill(ApiOverload overload, DocSections docs)
        {
            overload.Summary.AddRange(docs.Summary);
            overload.Remarks.AddRange(docs.Remarks);
            overload.Returns.AddRange(docs.Returns);
        }

        /// <summary>
        /// True for a member that is public for mechanism rather than for callers: one the plugin's
        /// own assemblies invoke across an asmdef boundary, marked
        /// <c>[EditorBrowsable(EditorBrowsableState.Never)]</c>. Hidden from IntelliSense, and hidden
        /// from the published reference for the same reason.
        /// </summary>
        private static bool IsHidden(MemberDeclarationSyntax member) =>
            member.AttributeLists
                .SelectMany(list => list.Attributes)
                .Any(attribute =>
                {
                    var name = attribute.Name.ToString();
                    if (!name.EndsWith("EditorBrowsable", StringComparison.Ordinal)) return false;
                    return attribute.ArgumentList?.Arguments
                        .Any(a => a.Expression.ToString().EndsWith("Never", StringComparison.Ordinal)) == true;
                });

        private static bool IsPublic(MemberDeclarationSyntax member, bool insideInterface)
        {
            if (IsHidden(member)) return false;
            if (member.Modifiers.Any(SyntaxKind.PublicKeyword)) return true;
            if (!insideInterface) return false;

            return !member.Modifiers.Any(SyntaxKind.PrivateKeyword)
                && !member.Modifiers.Any(SyntaxKind.ProtectedKeyword)
                && !member.Modifiers.Any(SyntaxKind.InternalKeyword);
        }

        /// <summary>
        /// <c>"component"</c> when the declaration's base list names <c>MonoBehaviour</c>; null
        /// otherwise. Syntax match on the base-list text, which is correct here since DocGen never
        /// binds symbols. A component is the only category we tag; everything else stays uncategorised.
        /// </summary>
        private static string? CategoryOf(BaseTypeDeclarationSyntax declaration)
        {
            var derivesFromMonoBehaviour = declaration.BaseList?.Types
                .Select(t => t.Type.ToString())
                .Any(text => text == "MonoBehaviour" || text.EndsWith(".MonoBehaviour", StringComparison.Ordinal))
                ?? false;

            return derivesFromMonoBehaviour ? "component" : null;
        }

        private static string KindOf(BaseTypeDeclarationSyntax declaration) => declaration switch
        {
            ClassDeclarationSyntax => "class",
            StructDeclarationSyntax => "struct",
            InterfaceDeclarationSyntax => "interface",
            RecordDeclarationSyntax => "record",
            EnumDeclarationSyntax => "enum",
            _ => "type",
        };

        private static string TypeSignature(BaseTypeDeclarationSyntax declaration)
        {
            var sb = new StringBuilder();
            sb.Append(Modifiers(declaration)).Append(' ').Append(KindOf(declaration)).Append(' ');
            sb.Append(declaration.Identifier.ValueText);

            if (declaration is TypeDeclarationSyntax typed)
            {
                if (typed.TypeParameterList is not null) sb.Append(typed.TypeParameterList.ToString());
                if (declaration.BaseList is not null) sb.Append(" : ").Append(string.Join(", ", declaration.BaseList.Types.Select(t => t.Type.ToString())));
                foreach (var clause in typed.ConstraintClauses) sb.Append(' ').Append(clause.ToString());
            }
            else if (declaration.BaseList is not null)
            {
                sb.Append(" : ").Append(string.Join(", ", declaration.BaseList.Types.Select(t => t.Type.ToString())));
            }

            return Squash(sb.ToString());
        }

        private static string MethodSignature(MethodDeclarationSyntax method)
        {
            var sb = new StringBuilder();
            var modifiers = Modifiers(method);
            if (modifiers.Length > 0) sb.Append(modifiers).Append(' ');
            sb.Append(method.ReturnType).Append(' ').Append(method.Identifier.ValueText);
            if (method.TypeParameterList is not null) sb.Append(method.TypeParameterList.ToString());
            sb.Append('(').Append(Parameters(method.ParameterList)).Append(')');
            foreach (var clause in method.ConstraintClauses) sb.Append(' ').Append(clause.ToString());
            return Squash(sb.ToString());
        }

        private static string Modifiers(MemberDeclarationSyntax member) =>
            string.Join(" ", member.Modifiers.Select(m => m.Text));

        private static string Accessors(AccessorListSyntax? accessors, ArrowExpressionClauseSyntax? arrow)
        {
            if (accessors is null) return arrow is null ? "" : "{ get; }";

            var parts = accessors.Accessors
                .Where(a => !a.Modifiers.Any(SyntaxKind.PrivateKeyword))
                .Select(a => $"{a.Keyword.ValueText};");
            return $"{{ {string.Join(" ", parts)} }}";
        }

        private static string Parameters(BaseParameterListSyntax? list)
        {
            if (list is null) return "";
            return string.Join(", ", list.Parameters.Select(p => Squash(p.ToString())));
        }

        private static List<ApiParameter> ParametersOf(BaseParameterListSyntax? list, DocSections docs)
        {
            var result = new List<ApiParameter>();
            if (list is null) return result;

            foreach (var parameter in list.Parameters)
            {
                var name = parameter.Identifier.ValueText;
                var entry = new ApiParameter
                {
                    Name = name,
                    Type = parameter.Type?.ToString() ?? "",
                    Default = parameter.Default?.Value.ToString(),
                    Modifier = parameter.Modifiers.Count == 0
                        ? null
                        : string.Join(" ", parameter.Modifiers.Select(m => m.Text)),
                };
                if (docs.Params.TryGetValue(name, out var summary)) entry.Summary.AddRange(summary);
                result.Add(entry);
            }

            return result;
        }

        private static List<ApiTypeParam> TypeParamsOf(BaseTypeDeclarationSyntax declaration, DocSections docs) =>
            declaration is TypeDeclarationSyntax typed
                ? TypeParamsOf(typed.TypeParameterList, typed.ConstraintClauses, docs)
                : new List<ApiTypeParam>();

        private static List<ApiTypeParam> TypeParamsOf(
            TypeParameterListSyntax? list,
            SyntaxList<TypeParameterConstraintClauseSyntax> constraints,
            DocSections docs)
        {
            var result = new List<ApiTypeParam>();
            if (list is null) return result;

            foreach (var parameter in list.Parameters)
            {
                var name = parameter.Identifier.ValueText;
                var clause = constraints.FirstOrDefault(c => c.Name.Identifier.ValueText == name);
                var entry = new ApiTypeParam
                {
                    Name = name,
                    Constraint = clause is null
                        ? null
                        : string.Join(", ", clause.Constraints.Select(c => c.ToString())),
                };
                if (docs.TypeParams.TryGetValue(name, out var summary)) entry.Summary.AddRange(summary);
                result.Add(entry);
            }

            return result;
        }

        /// <summary>Folds a multi-line declaration onto one line so signatures render as a single run.</summary>
        private static string Squash(string value)
        {
            var sb = new StringBuilder(value.Length);
            var pendingSpace = false;
            foreach (var ch in value)
            {
                if (ch is '\n' or '\r' or ' ' or '\t')
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }

                if (pendingSpace) sb.Append(' ');
                pendingSpace = false;
                sb.Append(ch);
            }

            return sb.ToString();
        }
    }

    public static class Slug
    {
        public static string ForType(string name, int arity)
        {
            var slug = Kebab(name);
            return arity == 0 ? slug : $"{slug}-{arity}";
        }

        public static string ForMember(string name) =>
            name == "this[]" ? "item" : Kebab(name.Replace("operator ", "op-"));

        /// <summary>
        /// Kebab-cases a PascalCase identifier, keeping acronym runs together so <c>UIHandle</c>
        /// becomes <c>ui-handle</c> rather than <c>u-i-handle</c>.
        /// </summary>
        private static string Kebab(string name)
        {
            var sb = new StringBuilder(name.Length + 4);
            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                if (!char.IsLetterOrDigit(ch))
                {
                    if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
                    continue;
                }

                if (char.IsUpper(ch) && i > 0 && sb.Length > 0 && sb[^1] != '-')
                {
                    var previous = name[i - 1];
                    var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                    if (!char.IsUpper(previous) || nextIsLower) sb.Append('-');
                }

                sb.Append(char.ToLowerInvariant(ch));
            }

            return sb.ToString().Trim('-');
        }
    }
}
