using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace CodeScenes.Analyzers
{
    /// <summary>
    /// Single source of truth for the SB1xxx diagnostic id/title/message/category/severity table
    /// (specs/26-codescenes-analyzers-toolkit.md, "Core deliverables" analyzer table).
    /// </summary>
    public static class DiagnosticDescriptors
    {
        private const string ShapeCategory = "CodeScenes.Shape";
        private const string NudgeCategory = "CodeScenes.Nudge";
        private const string UnityEventCategory = "CodeScenes.UnityEvent";
        private const string SpatialCategory = "CodeScenes.Spatial";

        // SB100x flat-shape (Error) — MessageFormat "{0}" so b2-t2 passes ShapeViolation.Message verbatim.
        public static readonly DiagnosticDescriptor SB1001 = new(
            "SB1001",
            "Build body must be a flat sequence of builder calls",
            "{0}",
            ShapeCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor SB1002 = new(
            "SB1002",
            "Unrecognized builder call",
            "{0}",
            ShapeCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor SB1003 = new(
            "SB1003",
            "Component closure must contain only .Set(...), .OnClick(...) or .OnEvent(...) calls",
            "{0}",
            ShapeCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        // SB11xx nudges — fixed MessageFormat from spec table (§Core deliverables analyzer table).
        public static readonly DiagnosticDescriptor SB1101 = new(
            "SB1101",
            "Use the typed .Set(...) form",
            "Use the typed form `.Set(x => x.member, …)` — the compiler checks the member and its type.",
            NudgeCategory,
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor SB1102 = new(
            "SB1102",
            "Use the typed prefab reference",
            "Use `Instance(Prefabs.X)` (spec 25) — a stale/typo'd prefab path then fails to compile.",
            NudgeCategory,
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor SB1103 = new(
            "SB1103",
            "Use the typed .On(...) selector",
            "Use the typed selector `.On(sel => sel.A.B, …)` (spec 24/25).",
            NudgeCategory,
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor SB1104 = new(
            "SB1104",
            "Use the typed Tags catalog",
            "Use `Tags.<str>` — a typo'd tag then fails to compile.",
            NudgeCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor SB1105 = new(
            "SB1105",
            "Use the typed Layers catalog",
            "Use `Layers.<name>` — a typo'd/invalid layer then fails to compile.",
            NudgeCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor SB1106 = new(
            "SB1106",
            "Use the typed Set<T> overload",
            "Use `Set<T>(x => x.member, value)` — the compiler catches value-type mismatches.",
            NudgeCategory,
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        // SB120x UnityEvent scaffold — MessageFormat with {0}/{1}/{2} placeholders (b2-t4 fills args).
        public static readonly DiagnosticDescriptor SB1201 = new(
            "SB1201",
            "UnityEvent listener signature mismatch",
            "Method `{0}` does not match `{1}`'s signature; expected `{2}`.",
            UnityEventCategory,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor SB1202 = new(
            "SB1202",
            "UnityEvent listener target not eligible",
            "Persistent-listener targets must be public instance/static methods matching the event.",
            UnityEventCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        // SB1203 same-axis position-driver conflict (Warning — runtime still arbitrates deterministically).
        public static readonly DiagnosticDescriptor SB1203 = new(
            "SB1203",
            "Two position drivers claim the same axis",
            "Two position drivers on this node claim the same axis; they will fight at runtime. Give them different axes, share one orientation reference, or remove one.",
            SpatialCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly ImmutableArray<DiagnosticDescriptor> All = ImmutableArray.Create(
            SB1001, SB1002, SB1003,
            SB1101, SB1102, SB1103, SB1104, SB1105, SB1106,
            SB1201, SB1202, SB1203);

        public static readonly ImmutableDictionary<string, DiagnosticDescriptor> ById =
            All.ToImmutableDictionary(d => d.Id);
    }
}
