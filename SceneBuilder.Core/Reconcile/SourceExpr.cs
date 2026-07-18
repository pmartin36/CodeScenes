using System;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    /// <summary>
    /// Formats Core values as C# source expressions for generated / patched builder code. Shared by the
    /// Reconciler (transform-argument edits) and the SourcePatchApplier (appended statements) so float
    /// formatting is identical everywhere — round-trip-safe and f-suffixed, so the emitted code compiles.
    /// </summary>
    public static class SourceExpr
    {
        /// <summary>A tuple literal `(x, y, z)` with each component formatted via <see cref="Float"/>.</summary>
        public static string Vec3Literal(Vec3 v) =>
            "(" + Float(v.X) + ", " + Float(v.Y) + ", " + Float(v.Z) + ")";

        /// <summary>
        /// A C# float literal, rounded to 4 dp and ALWAYS `f`-suffixed (0f, 2f, 1.53f). The suffix is
        /// mandatory on non-integers (a bare `1.53` is a double and won't convert to the authoring API's
        /// float parameter) and applied on integral values too so the literal unambiguously reads as a
        /// float — never mistaken for an int or a double.
        /// </summary>
        public static string Float(float value)
        {
            var rounded = Math.Round((double)value, 4, MidpointRounding.AwayFromZero);
            var text = rounded.ToString("0.####", CultureInfo.InvariantCulture);
            return text + "f";
        }

        /// <summary>
        /// A quoted, escaped C# string literal (including surrounding double quotes) for the given raw
        /// value. Caller supplies the value WITHOUT quotes; this owns quoting/escaping.
        /// </summary>
        public static string StringLiteral(string value) =>
            SyntaxFactory.Literal(value).ToString();

        /// <summary>
        /// A bare C# integer literal (invariant culture, no suffix/quotes).
        /// </summary>
        public static string IntLiteral(int value) =>
            value.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// A round-trip-safe C# float literal, ALWAYS `f`-suffixed and unrounded (shortest
        /// round-trippable representation via .NET's invariant-culture ToString). Distinct from
        /// <see cref="Float"/> (which rounds to 4dp and omits the suffix on integral values) —
        /// used wherever a <see cref="ValueNode"/> float must parse back as exactly Float, never
        /// Int/Double.
        /// </summary>
        private static string FloatLiteral(float value) =>
            value.ToString(CultureInfo.InvariantCulture) + "f";

        /// <summary>
        /// A round-trip-safe C# double literal, ALWAYS `d`-suffixed and unrounded.
        /// </summary>
        private static string DoubleLiteral(double value) =>
            value.ToString(CultureInfo.InvariantCulture) + "d";

        /// <summary>
        /// Renders a <see cref="ValueNode"/> as a compilable, round-trip-safe C# expression
        /// (the `.Set(key, VALUE)` / `.Component&lt;T&gt;(...)` argument). Total: every in-scope
        /// kind has a rendering that parses back via <c>ValueNodeParser.Parse</c> to an equal
        /// <see cref="ValueNode"/>.
        /// </summary>
        public static string ValueNodeLiteral(ValueNode node) => node switch
        {
            ValueNode.Primitive(PrimitiveKind.Bool, bool b) => b ? "true" : "false",
            ValueNode.Primitive(PrimitiveKind.Int, int i) => IntLiteral(i),
            ValueNode.Primitive(PrimitiveKind.Long, long l) => l.ToString(CultureInfo.InvariantCulture) + "L",
            ValueNode.Primitive(PrimitiveKind.Float, float f) => FloatLiteral(f),
            ValueNode.Primitive(PrimitiveKind.Double, double d) => DoubleLiteral(d),
            ValueNode.Primitive(PrimitiveKind.String, string s) => StringLiteral(s),
            ValueNode.Primitive primitive => throw new NotSupportedException(
                $"SourceExpr.ValueNodeLiteral: unsupported PrimitiveKind {primitive.Kind}"),

            ValueNode.Enum(var typeFullName, var members, true) when members.Count > 1 =>
                string.Join(" | ", members.Select(m => typeFullName + "." + m)),
            ValueNode.Enum(var typeFullName, var members, _) => typeFullName + "." + members[0],

            ValueNode.Vec2(var v) =>
                $"new UnityEngine.Vector2({FloatLiteral(v.X)}, {FloatLiteral(v.Y)})",
            ValueNode.Vec3(var v) =>
                $"new UnityEngine.Vector3({FloatLiteral(v.X)}, {FloatLiteral(v.Y)}, {FloatLiteral(v.Z)})",
            ValueNode.Vec4(var v) =>
                $"new UnityEngine.Vector4({FloatLiteral(v.X)}, {FloatLiteral(v.Y)}, {FloatLiteral(v.Z)}, {FloatLiteral(v.W)})",
            ValueNode.Quat(var v) =>
                $"new UnityEngine.Quaternion({FloatLiteral(v.X)}, {FloatLiteral(v.Y)}, {FloatLiteral(v.Z)}, {FloatLiteral(v.W)})",
            ValueNode.Color(var v) =>
                $"new UnityEngine.Color({FloatLiteral(v.R)}, {FloatLiteral(v.G)}, {FloatLiteral(v.B)}, {FloatLiteral(v.A)})",

            ValueNode.List { Items.Count: 0 } => "new object[] { }",
            ValueNode.List list => "new[] { " + string.Join(", ", list.Items.Select(ValueNodeLiteral)) + " }",

            ValueNode.Nested nested => "new " + nested.TypeName + " { " +
                string.Join(", ", nested.Fields.Select(kv => kv.Key + " = " + ValueNodeLiteral(kv.Value))) +
                " }",

            ValueNode.Unsupported unsupported => unsupported.RawToken,

            ValueNode.AssetRef(null) => "Asset(null)",
            ValueNode.AssetRef(AssetRef { IsBuiltin: true, TypeHint: "" } assetRef) =>
                "Builtin(" + StringLiteral(assetRef.DisplayPath) + ")",
            ValueNode.AssetRef(AssetRef { IsBuiltin: true } assetRef) =>
                "Builtin(" + StringLiteral(assetRef.DisplayPath) + ", " + StringLiteral(assetRef.TypeHint) + ")",
            ValueNode.AssetRef(AssetRef { SubAsset: "" } assetRef) =>
                "Asset(" + StringLiteral(assetRef.DisplayPath) + ")",
            ValueNode.AssetRef(var assetRef) =>
                "Asset(" + StringLiteral(assetRef.DisplayPath) + ", " + StringLiteral(assetRef.SubAsset) + ")",

            _ => throw new NotSupportedException($"SourceExpr.ValueNodeLiteral: unsupported ValueNode kind {node.GetType().Name}"),
        };
    }
}
