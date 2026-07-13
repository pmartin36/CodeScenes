using System;

namespace SceneBuilder.Core.Reconcile
{
    // Fail-loud/located exception for SourcePatchApplier (§7); mirrors Parsing/ParseException.cs
    // but lives in Reconcile so the two namespaces don't share an exception type.
    public sealed class PatchException : Exception
    {
        public int Line { get; }
        public int Column { get; }

        public PatchException(string message, int line, int column) : base(message)
        {
            Line = line;
            Column = column;
        }
    }
}
