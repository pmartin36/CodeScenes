using System.Linq;
using System.Text.Json;
using SceneBuilder.Core.Validation;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t4: DiagnosticRenderer turns a HeadlessValidationResult into the text/JSON envelope the
    // CLI prints and the exit code it returns. See research.md "Test surface" + blueprint
    // INTERFACES for the exact shapes asserted here.
    public class DiagnosticRendererTests
    {
        private static Diagnostic ErrorDiagnostic() => new Diagnostic
        {
            File = "Assets/Foo.cs",
            Line = 12,
            Col = 5,
            Code = DiagnosticCodes.UnresolvedType,
            Severity = DiagnosticSeverity.Error,
            Message = "Unresolved type 'Rigidbdy'.",
            Suggestion = "Rigidbody"
        };

        private static HeadlessValidationResult CleanResult() => new HeadlessValidationResult
        {
            File = "Assets/Foo.cs",
            Result = new ValidationResult { Diagnostics = new Diagnostic[0] },
            Skipped = new string[0],
        };

        private static HeadlessValidationResult ErrorResult() => new HeadlessValidationResult
        {
            File = "Assets/Foo.cs",
            Result = new ValidationResult { Diagnostics = new[] { ErrorDiagnostic() } },
            Skipped = new string[0],
        };

        private static HeadlessValidationResult SkippedResult() => new HeadlessValidationResult
        {
            File = "Assets/Foo.cs",
            Result = new ValidationResult { Diagnostics = new Diagnostic[0] },
            Skipped = new[] { "type", "asset" },
        };

        [Fact]
        public void RenderJson_Envelope_HasFlatShapeFileOkDiagnosticsPhase()
        {
            var json = DiagnosticRenderer.RenderJson(ErrorResult());
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var propertyNames = root.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "diagnostics", "file", "ok", "phase" }, propertyNames);

            Assert.Equal("Assets/Foo.cs", root.GetProperty("file").GetString());
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.Equal("planning", root.GetProperty("phase").GetString());

            var diagnostics = root.GetProperty("diagnostics");
            Assert.Equal(1, diagnostics.GetArrayLength());
            var diagnostic = diagnostics[0];
            Assert.Equal(DiagnosticCodes.UnresolvedType, diagnostic.GetProperty("code").GetString());
            Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
            Assert.Equal(12, diagnostic.GetProperty("line").GetInt32());
            Assert.Equal(5, diagnostic.GetProperty("col").GetInt32());
            Assert.Equal("Rigidbody", diagnostic.GetProperty("suggestion").GetString());
        }

        [Fact]
        public void RenderJson_OmitsSkipped_WhenNoneSkipped()
        {
            var json = DiagnosticRenderer.RenderJson(CleanResult());
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal(0, root.GetProperty("diagnostics").GetArrayLength());
            Assert.False(root.TryGetProperty("skipped", out _));
        }

        [Fact]
        public void RenderJson_IncludesSkipped_WhenManagedMissing()
        {
            var json = DiagnosticRenderer.RenderJson(SkippedResult());
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("skipped", out var skipped));
            var values = skipped.EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(new[] { "type", "asset" }, values);
        }

        [Fact]
        public void RenderText_ErrorDiagnostic_RendersLocatedBlockWithCodeAndSuggestion()
        {
            var text = DiagnosticRenderer.RenderText(ErrorResult());

            Assert.Contains("Assets/Foo.cs:12:5  error  SB2001  Unresolved type 'Rigidbdy'.", text);
            Assert.Contains("Rigidbody", text);
        }

        [Fact]
        public void RenderText_Summary_CountsErrorsAndAppendsPlanningBoundaryNote()
        {
            var errorText = DiagnosticRenderer.RenderText(ErrorResult());
            Assert.Contains("1 error(s).  " + DiagnosticRenderer.PlanningBoundaryNote, errorText);

            var cleanText = DiagnosticRenderer.RenderText(CleanResult());
            Assert.Contains("0 error(s).  " + DiagnosticRenderer.PlanningBoundaryNote, cleanText);
        }

        [Fact]
        public void ExitCode_ZeroWhenOk_NonZeroWhenErrors()
        {
            Assert.Equal(0, DiagnosticRenderer.ExitCode(CleanResult()));
            Assert.Equal(1, DiagnosticRenderer.ExitCode(ErrorResult()));
        }
    }
}
