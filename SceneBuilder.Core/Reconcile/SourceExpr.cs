using System;
using System.Globalization;
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
        /// A C# numeric literal, rounded to 4 dp. Non-integers get the `f` suffix (1.53f) so the
        /// containing tuple is (float,float,float) — a bare `1.53` is a double and won't convert to the
        /// authoring API's float parameter.
        /// </summary>
        public static string Float(float value)
        {
            var rounded = Math.Round((double)value, 4, MidpointRounding.AwayFromZero);
            var text = rounded.ToString("0.####", CultureInfo.InvariantCulture);
            return text.Contains(".") ? text + "f" : text;
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
    }
}
