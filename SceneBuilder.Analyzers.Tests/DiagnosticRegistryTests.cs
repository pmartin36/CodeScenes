using System.Collections.Generic;
using CodeScenes.Analyzers;
using Microsoft.CodeAnalysis;

namespace SceneBuilder.Analyzers.Tests
{
    public class DiagnosticRegistryTests
    {
        private static readonly Dictionary<string, DiagnosticSeverity> ExpectedSeverities = new()
        {
            ["SB1001"] = DiagnosticSeverity.Error,
            ["SB1002"] = DiagnosticSeverity.Error,
            ["SB1003"] = DiagnosticSeverity.Error,
            ["SB1101"] = DiagnosticSeverity.Info,
            ["SB1102"] = DiagnosticSeverity.Info,
            ["SB1103"] = DiagnosticSeverity.Info,
            ["SB1104"] = DiagnosticSeverity.Warning,
            ["SB1105"] = DiagnosticSeverity.Warning,
            ["SB1106"] = DiagnosticSeverity.Info,
            ["SB1201"] = DiagnosticSeverity.Error,
            ["SB1202"] = DiagnosticSeverity.Warning,
            ["SB1203"] = DiagnosticSeverity.Warning,
        };

        [Fact]
        public void Registry_DeclaresAllTwelveSB1xxx_WithSpecSeverities()
        {
            Assert.Equal(ExpectedSeverities.Count, DiagnosticDescriptors.All.Length);

            foreach (var (id, severity) in ExpectedSeverities)
            {
                Assert.True(DiagnosticDescriptors.ById.ContainsKey(id), $"ById is missing {id}");
                var descriptor = DiagnosticDescriptors.ById[id];
                Assert.Equal(id, descriptor.Id);
                Assert.Equal(severity, descriptor.DefaultSeverity);
                Assert.True(descriptor.IsEnabledByDefault);
            }
        }
    }
}
