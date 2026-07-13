using System;

namespace SceneBuilder.Core.Parsing
{
    public sealed class ParseException : Exception
    {
        public int Line { get; }
        public int Column { get; }

        public ParseException(string message, int line, int column) : base(message)
        {
            Line = line;
            Column = column;
        }
    }
}
