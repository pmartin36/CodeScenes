using System.Text.Json.Serialization;

namespace SceneBuilder.DocGen
{
    /// <summary>The whole emitted document: the authoring surface plus the analyzer diagnostic table.</summary>
    public sealed class ApiDocument
    {
        public string Assembly { get; set; } = "";
        public string Namespace { get; set; } = "";
        public List<ApiType> Types { get; set; } = new();
        public List<ApiDiagnostic> Diagnostics { get; set; } = new();
    }

    /// <summary>A public type. <see cref="Id"/> is the URL slug; generic types carry an arity suffix.</summary>
    public sealed class ApiType
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Signature { get; set; } = "";
        public string? BaseType { get; set; }
        public string SourcePath { get; set; } = "";
        public List<DocNode> Summary { get; set; } = new();
        public List<DocNode> Remarks { get; set; } = new();
        public List<ApiTypeParam> TypeParams { get; set; } = new();
        public List<ApiMember> Members { get; set; } = new();
        public List<ApiType> NestedTypes { get; set; } = new();
        public List<ApiEnumValue> EnumValues { get; set; } = new();
    }

    public sealed class ApiEnumValue
    {
        public string Name { get; set; } = "";
        public string? Value { get; set; }
        public List<DocNode> Summary { get; set; } = new();
    }

    public sealed class ApiTypeParam
    {
        public string Name { get; set; } = "";
        public string? Constraint { get; set; }
        public List<DocNode> Summary { get; set; } = new();
    }

    /// <summary>
    /// One member NAME. Overloads are grouped under it so a member's anchor stays stable when an
    /// overload is added or reordered.
    /// </summary>
    public sealed class ApiMember
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public bool IsStatic { get; set; }
        public List<DocNode> Summary { get; set; } = new();
        public List<ApiOverload> Overloads { get; set; } = new();
    }

    public sealed class ApiOverload
    {
        public string Signature { get; set; } = "";
        public string? ReturnType { get; set; }
        public List<ApiParameter> Parameters { get; set; } = new();
        public List<ApiTypeParam> TypeParams { get; set; } = new();
        public List<DocNode> Summary { get; set; } = new();
        public List<DocNode> Remarks { get; set; } = new();
        public List<DocNode> Returns { get; set; } = new();
    }

    public sealed class ApiParameter
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string? Default { get; set; }
        public string? Modifier { get; set; }
        public List<DocNode> Summary { get; set; } = new();
    }

    /// <summary>An SB#### analyzer diagnostic, read from the DiagnosticDescriptors registry.</summary>
    public sealed class ApiDiagnostic
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string MessageFormat { get; set; } = "";
        public string Category { get; set; } = "";
        public string Severity { get; set; } = "";
        public List<DocNode> Summary { get; set; } = new();
    }

    /// <summary>
    /// One inline run of documentation prose. Structured rather than pre-rendered HTML so the site
    /// controls how a cref becomes a link and how &lt;c&gt; becomes code.
    /// </summary>
    public sealed class DocNode
    {
        public string Kind { get; set; } = "text";
        public string Text { get; set; } = "";

        /// <summary>Slug of the target type when a cref resolves inside the documented surface.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Type { get; set; }

        /// <summary>Anchor of the target member when a cref names one.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Member { get; set; }

        /// <summary>Absolute URL when a cref resolves outside the documented surface.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Href { get; set; }

        public static DocNode Text_(string text) => new() { Kind = "text", Text = text };
        public static DocNode Code(string text) => new() { Kind = "code", Text = text };
        public static DocNode Para() => new() { Kind = "para" };
    }
}
