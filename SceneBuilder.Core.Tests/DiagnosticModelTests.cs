using System.Linq;
using System.Text.Json;
using SceneBuilder.Core.Validation;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class DiagnosticModelTests
    {
        [Fact]
        public void Diagnostic_SerializesToStableJsonShape()
        {
            var diagnostic = new Diagnostic
            {
                File = "Assets/Foo.cs",
                Line = 12,
                Col = 5,
                Code = DiagnosticCodes.UnresolvedType,
                Severity = DiagnosticSeverity.Error,
                Message = "Unresolved type 'Rigidbdy'.",
                Suggestion = "Rigidbody"
            };

            var json = JsonSerializer.Serialize(diagnostic);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var propertyNames = root.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(
                new[] { "code", "col", "line", "message", "severity", "suggestion" },
                propertyNames);

            Assert.Equal(12, root.GetProperty("line").GetInt32());
            Assert.Equal(5, root.GetProperty("col").GetInt32());
            Assert.Equal(DiagnosticCodes.UnresolvedType, root.GetProperty("code").GetString());
            Assert.Equal("error", root.GetProperty("severity").GetString());
            Assert.Equal("Unresolved type 'Rigidbdy'.", root.GetProperty("message").GetString());
            Assert.Equal("Rigidbody", root.GetProperty("suggestion").GetString());
        }

        [Fact]
        public void ValidationResult_Ok_TrueWhenNoErrorSeverity()
        {
            var result = new ValidationResult
            {
                Diagnostics = new[]
                {
                    new Diagnostic { Severity = DiagnosticSeverity.Info, Code = "SB0000", Message = "deferred" }
                }
            };

            Assert.True(result.Ok);
        }

        [Fact]
        public void ValidationResult_Ok_FalseWhenAnyErrorSeverity()
        {
            var result = new ValidationResult
            {
                Diagnostics = new[]
                {
                    new Diagnostic { Severity = DiagnosticSeverity.Info, Code = "SB0000", Message = "deferred" },
                    new Diagnostic { Severity = DiagnosticSeverity.Error, Code = DiagnosticCodes.UnresolvedType, Message = "bad" }
                }
            };

            Assert.False(result.Ok);
        }

        [Fact]
        public void DiagnosticBag_CollectsAll_AndProducesResult()
        {
            var bag = new DiagnosticBag();

            bag.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Code = DiagnosticCodes.UnresolvedType, Message = "first" });
            bag.Add(new Diagnostic { Severity = DiagnosticSeverity.Info, Code = "SB0000", Message = "second" });

            Assert.Equal(2, bag.Diagnostics.Count);
            Assert.True(bag.HasErrors);

            var result = bag.ToResult();

            Assert.Equal(2, result.Diagnostics.Count);
            Assert.False(result.Ok);
        }
    }
}
