using System.Collections.Generic;
using System.Linq;

namespace SceneBuilder.Core.Validation
{
    public sealed class DiagnosticBag
    {
        private readonly List<Diagnostic> _items = new();

        public void Add(Diagnostic diagnostic)
        {
            _items.Add(diagnostic);
        }

        public IReadOnlyList<Diagnostic> Diagnostics => _items;

        public bool HasErrors => _items.Any(d => d.Severity == DiagnosticSeverity.Error);

        public ValidationResult ToResult()
        {
            return new ValidationResult { Diagnostics = _items.ToArray() };
        }
    }
}
