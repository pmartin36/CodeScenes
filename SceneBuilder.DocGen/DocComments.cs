using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.DocGen
{
    /// <summary>
    /// Turns the XML doc trivia above a declaration into structured inline runs. Element text is
    /// preserved verbatim; only the leading "///" and the indentation that follows it are stripped.
    /// </summary>
    public static class DocComments
    {
        public static DocSections Read(SyntaxNode node)
        {
            var sections = new DocSections();

            var trivia = node.GetLeadingTrivia()
                .Select(t => t.GetStructure())
                .OfType<DocumentationCommentTriviaSyntax>()
                .ToList();

            foreach (var doc in trivia)
            {
                foreach (var element in doc.Content.OfType<XmlElementSyntax>())
                {
                    var name = element.StartTag.Name.LocalName.ValueText;
                    var body = Inline(element.Content);
                    switch (name)
                    {
                        case "summary":
                            Append(sections.Summary, body);
                            break;
                        case "remarks":
                            Append(sections.Remarks, body);
                            break;
                        case "returns":
                            Append(sections.Returns, body);
                            break;
                        case "value":
                            Append(sections.Returns, body);
                            break;
                        case "param":
                        {
                            var key = AttributeValue(element.StartTag, "name");
                            if (key is not null) sections.Params[key] = body;
                            break;
                        }
                        case "typeparam":
                        {
                            var key = AttributeValue(element.StartTag, "name");
                            if (key is not null) sections.TypeParams[key] = body;
                            break;
                        }
                    }
                }
            }

            return sections;
        }

        private static void Append(List<DocNode> target, List<DocNode> body)
        {
            if (body.Count == 0) return;
            if (target.Count > 0) target.Add(DocNode.Para());
            target.AddRange(body);
        }

        private static string? AttributeValue(XmlElementStartTagSyntax tag, string name) =>
            tag.Attributes
                .OfType<XmlNameAttributeSyntax>()
                .FirstOrDefault(a => a.Name.LocalName.ValueText == name)
                ?.Identifier.Identifier.ValueText;

        private static List<DocNode> Inline(SyntaxList<XmlNodeSyntax> content)
        {
            var nodes = new List<DocNode>();
            foreach (var node in content) Visit(node, nodes);
            Normalize(nodes);
            return nodes;
        }

        private static void Visit(XmlNodeSyntax node, List<DocNode> output)
        {
            switch (node)
            {
                case XmlTextSyntax text:
                    AddText(output, TextOf(text));
                    break;

                case XmlEmptyElementSyntax empty:
                    Element(empty.Name.LocalName.ValueText, empty.Attributes, null, output);
                    break;

                case XmlElementSyntax element:
                    Element(
                        element.StartTag.Name.LocalName.ValueText,
                        element.StartTag.Attributes,
                        element.Content,
                        output);
                    break;
            }
        }

        private static void Element(
            string name,
            SyntaxList<XmlAttributeSyntax> attributes,
            SyntaxList<XmlNodeSyntax>? content,
            List<DocNode> output)
        {
            switch (name)
            {
                case "see":
                case "seealso":
                {
                    var cref = attributes.OfType<XmlCrefAttributeSyntax>().FirstOrDefault();
                    if (cref is not null)
                    {
                        output.Add(new DocNode { Kind = "cref", Text = CrefText(cref.Cref) });
                        return;
                    }

                    var href = attributes
                        .OfType<XmlTextAttributeSyntax>()
                        .FirstOrDefault(a => a.Name.LocalName.ValueText == "href");
                    if (href is not null)
                    {
                        var url = string.Concat(href.TextTokens.Select(t => t.ValueText));
                        var label = content is null ? url : Plain(content.Value);
                        output.Add(new DocNode { Kind = "link", Text = label, Href = url });
                        return;
                    }

                    var langword = attributes
                        .OfType<XmlTextAttributeSyntax>()
                        .FirstOrDefault(a => a.Name.LocalName.ValueText == "langword");
                    if (langword is not null)
                        output.Add(DocNode.Code(string.Concat(langword.TextTokens.Select(t => t.ValueText))));
                    return;
                }

                case "paramref":
                case "typeparamref":
                {
                    var value = attributes
                        .OfType<XmlNameAttributeSyntax>()
                        .FirstOrDefault(a => a.Name.LocalName.ValueText == "name")
                        ?.Identifier.Identifier.ValueText;
                    if (value is not null) output.Add(new DocNode { Kind = name, Text = value });
                    return;
                }

                case "c":
                case "code":
                    if (content is not null) output.Add(DocNode.Code(Plain(content.Value)));
                    return;

                case "b":
                case "strong":
                    if (content is not null) output.Add(new DocNode { Kind = "strong", Text = Plain(content.Value) });
                    return;

                case "i":
                case "em":
                    if (content is not null) output.Add(new DocNode { Kind = "em", Text = Plain(content.Value) });
                    return;

                case "para":
                    output.Add(DocNode.Para());
                    if (content is not null) foreach (var child in content.Value) Visit(child, output);
                    output.Add(DocNode.Para());
                    return;

                default:
                    if (content is not null) foreach (var child in content.Value) Visit(child, output);
                    return;
            }
        }

        /// <summary>Renders a cref the way it was written, with generic braces restored to angle brackets.</summary>
        private static string CrefText(CrefSyntax cref) =>
            cref.ToString().Replace('{', '<').Replace('}', '>');

        private static string Plain(SyntaxList<XmlNodeSyntax> content)
        {
            var sb = new StringBuilder();
            foreach (var node in content)
            {
                if (node is XmlTextSyntax text) sb.Append(TextOf(text));
                else sb.Append(node.ToString());
            }

            return Collapse(sb.ToString()).Trim();
        }

        private static string TextOf(XmlTextSyntax text)
        {
            var sb = new StringBuilder();
            foreach (var token in text.TextTokens)
            {
                sb.Append(token.IsKind(SyntaxKind.XmlTextLiteralNewLineToken) ? "\n" : token.ValueText);
            }

            return sb.ToString();
        }

        private static void AddText(List<DocNode> output, string raw)
        {
            var value = Collapse(raw);

            // The space that separates a run from the element before it lives at the START of this
            // text node, so it survives everywhere except at the beginning of a paragraph.
            if (output.Count == 0 || output[^1].Kind == "para") value = value.TrimStart();

            if (value.Length == 0) return;
            output.Add(DocNode.Text_(value));
        }

        /// <summary>
        /// Folds the newlines and per-line indentation of a wrapped /// block into single spaces,
        /// keeping the leading and trailing one so adjacent inline elements stay separated.
        /// </summary>
        private static string Collapse(string value)
        {
            var sb = new StringBuilder(value.Length);
            var pendingSpace = false;
            foreach (var ch in value)
            {
                if (ch is '\n' or '\r' or ' ' or '\t')
                {
                    pendingSpace = true;
                    continue;
                }

                if (pendingSpace) sb.Append(' ');
                pendingSpace = false;
                sb.Append(ch);
            }

            if (pendingSpace) sb.Append(' ');
            return sb.ToString();
        }

        /// <summary>Drops empty/duplicate paragraph breaks and trims the run sequence at both ends.</summary>
        private static void Normalize(List<DocNode> nodes)
        {
            for (var i = nodes.Count - 1; i > 0; i--)
            {
                if (nodes[i].Kind == "para" && nodes[i - 1].Kind == "para") nodes.RemoveAt(i);
            }

            while (nodes.Count > 0 && nodes[0].Kind == "para") nodes.RemoveAt(0);
            while (nodes.Count > 0 && nodes[^1].Kind == "para") nodes.RemoveAt(nodes.Count - 1);

            if (nodes.Count > 0 && nodes[0].Kind == "text")
                nodes[0].Text = nodes[0].Text.TrimStart();
            if (nodes.Count > 0 && nodes[^1].Kind == "text")
                nodes[^1].Text = nodes[^1].Text.TrimEnd();

            nodes.RemoveAll(n => n.Kind == "text" && n.Text.Length == 0);
        }
    }

    public sealed class DocSections
    {
        public List<DocNode> Summary { get; } = new();
        public List<DocNode> Remarks { get; } = new();
        public List<DocNode> Returns { get; } = new();
        public Dictionary<string, List<DocNode>> Params { get; } = new();
        public Dictionary<string, List<DocNode>> TypeParams { get; } = new();
    }
}
