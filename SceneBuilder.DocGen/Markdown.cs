using System.Text;

namespace SceneBuilder.DocGen
{
    // Renders an ApiDocument to a single self-contained markdown file: the local authoring reference
    // the AI skill reads. Same source model the site renders, but crefs become inline code (this is
    // one file, so the reader finds a type by name) and external crefs stay as links.
    public static class Markdown
    {
        public static string Render(ApiDocument doc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# CodeScenes authoring API");
            sb.AppendLine();
            sb.AppendLine(
                $"Every type and member you can use inside `ISceneDefinition.Build` (and `IPrefabDefinition.Build`). "
                + $"Namespace `{doc.Namespace}`, assembly `{doc.Assembly}`. Generated from the C# source by SceneBuilder.DocGen.");
            sb.AppendLine();

            foreach (var type in doc.Types)
            {
                RenderType(sb, type, 2);
            }

            if (doc.Diagnostics.Count > 0)
            {
                sb.AppendLine("## Analyzer diagnostics");
                sb.AppendLine();
                sb.AppendLine("| ID | Severity | Title |");
                sb.AppendLine("|----|----------|-------|");
                foreach (var d in doc.Diagnostics)
                {
                    sb.AppendLine($"| {d.Id} | {d.Severity} | {Inline(d.Title)} |");
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd() + "\n";
        }

        private static void RenderType(StringBuilder sb, ApiType type, int level)
        {
            sb.AppendLine($"{Hashes(level)} {type.DisplayName}");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(type.Signature);
            sb.AppendLine("```");
            sb.AppendLine();

            AppendProse(sb, type.Summary);
            AppendProse(sb, type.Remarks);

            if (type.EnumValues.Count > 0)
            {
                foreach (var v in type.EnumValues)
                {
                    var s = Inline(v.Summary);
                    sb.AppendLine(string.IsNullOrWhiteSpace(s) ? $"- `{v.Name}`" : $"- `{v.Name}` — {s}");
                }
                sb.AppendLine();
            }

            foreach (var m in type.Members)
            {
                RenderMember(sb, type, m, level + 1);
            }

            foreach (var nested in type.NestedTypes)
            {
                RenderType(sb, nested, level);
            }
        }

        private static void RenderMember(StringBuilder sb, ApiType type, ApiMember m, int level)
        {
            sb.AppendLine($"{Hashes(level)} {type.Name}.{m.Name}");
            sb.AppendLine();

            var memberSummary = Inline(m.Summary);
            if (!string.IsNullOrWhiteSpace(memberSummary))
            {
                sb.AppendLine(memberSummary);
                sb.AppendLine();
            }

            foreach (var ov in m.Overloads)
            {
                sb.AppendLine("```csharp");
                sb.AppendLine(ov.Signature);
                sb.AppendLine("```");
                sb.AppendLine();

                // Only when the overload adds something the member-level summary did not already say
                // (single-overload members carry the same text in both).
                var ovSummary = Inline(ov.Summary);
                if (!string.IsNullOrWhiteSpace(ovSummary) && ovSummary != memberSummary)
                {
                    sb.AppendLine(ovSummary);
                    sb.AppendLine();
                }

                foreach (var p in ov.Parameters)
                {
                    var ps = Inline(p.Summary);
                    var head = $"- `{p.Name}` (`{p.Type}`)";
                    sb.AppendLine(string.IsNullOrWhiteSpace(ps) ? head : $"{head} — {ps}");
                }
                if (ov.Parameters.Count > 0)
                {
                    sb.AppendLine();
                }

                var returns = Inline(ov.Returns);
                if (!string.IsNullOrWhiteSpace(returns))
                {
                    sb.AppendLine($"Returns: {returns}");
                    sb.AppendLine();
                }

                AppendProse(sb, ov.Remarks);
            }
        }

        private static void AppendProse(StringBuilder sb, List<DocNode> nodes)
        {
            var s = Inline(nodes);
            if (!string.IsNullOrWhiteSpace(s))
            {
                sb.AppendLine(s);
                sb.AppendLine();
            }
        }

        // Renders a run of DocNodes to inline markdown. A `para` node becomes a paragraph break.
        private static string Inline(List<DocNode> nodes)
        {
            var sb = new StringBuilder();
            foreach (var n in nodes)
            {
                switch (n.Kind)
                {
                    case "code":
                        sb.Append('`').Append(n.Text).Append('`');
                        break;
                    case "para":
                        sb.Append("\n\n");
                        break;
                    default:
                        // A resolved external cref keeps its link; an internal cref (Type/Member) becomes
                        // inline code, since the whole surface is in this one file.
                        if (!string.IsNullOrEmpty(n.Href))
                        {
                            sb.Append('[').Append(n.Text).Append("](").Append(n.Href).Append(')');
                        }
                        else if (!string.IsNullOrEmpty(n.Type) || !string.IsNullOrEmpty(n.Member))
                        {
                            sb.Append('`').Append(n.Text).Append('`');
                        }
                        else
                        {
                            sb.Append(n.Text);
                        }
                        break;
                }
            }
            return sb.ToString().Replace(" \n", "\n").Trim();
        }

        private static string Inline(string text) => text;

        private static string Hashes(int level) => new string('#', Math.Clamp(level, 1, 6));
    }
}
