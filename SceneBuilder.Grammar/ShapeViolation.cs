namespace SceneBuilder.Grammar
{
    public readonly struct ShapeViolation
    {
        public SourceSpan Span { get; }
        public string Code { get; }
        public string Message { get; }

        public ShapeViolation(SourceSpan span, string code, string message)
        {
            Span = span;
            Code = code;
            Message = message;
        }
    }
}
