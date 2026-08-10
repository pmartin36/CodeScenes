using System;
using System.Linq;
using System.Reflection;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // PersistentCallPaths is the one vocabulary of serialized UnityEvent persistent-call field
    // spellings, measured against UnityEngine.CoreModule.dll (Unity 6000.5.3f1): PersistentCall,
    // ArgumentCache, PersistentCallGroup.m_Calls, UnityEventBase.m_PersistentCalls.
    public class UnityEventPathVocabularyTests
    {
        [Fact]
        public void Vocabulary_EachConstantCarriesTheEngineSpelling()
        {
            Assert.Equal("m_Target", PersistentCallPaths.Target);
            Assert.Equal("m_TargetAssemblyTypeName", PersistentCallPaths.TargetAssemblyTypeName);
            Assert.Equal("m_MethodName", PersistentCallPaths.MethodName);
            Assert.Equal("m_Mode", PersistentCallPaths.Mode);
            Assert.Equal("m_Arguments", PersistentCallPaths.Arguments);
            Assert.Equal("m_CallState", PersistentCallPaths.CallState);

            Assert.Equal("m_ObjectArgument", PersistentCallPaths.ObjectArgument);
            Assert.Equal("m_ObjectArgumentAssemblyTypeName", PersistentCallPaths.ObjectArgumentAssemblyTypeName);
            Assert.Equal("m_IntArgument", PersistentCallPaths.IntArgument);
            Assert.Equal("m_FloatArgument", PersistentCallPaths.FloatArgument);
            Assert.Equal("m_StringArgument", PersistentCallPaths.StringArgument);
            Assert.Equal("m_BoolArgument", PersistentCallPaths.BoolArgument);

            Assert.Equal("m_PersistentCalls", PersistentCallPaths.PersistentCalls);
            Assert.Equal("m_Calls", PersistentCallPaths.Calls);
            Assert.Equal("m_PersistentCalls.m_Calls", PersistentCallPaths.CallsRelativePath);
        }

        [Fact]
        public void Vocabulary_All_IsExactlyTheFourteenSerializedNames()
        {
            var expected = PersistentCallPaths.CallFields
                .Concat(PersistentCallPaths.ArgumentFields)
                .Concat(new[] { PersistentCallPaths.PersistentCalls, PersistentCallPaths.Calls })
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(14, PersistentCallPaths.All.Count);
            Assert.Equal(PersistentCallPaths.All.Count, PersistentCallPaths.All.Distinct().Count());
            Assert.Equal(expected, PersistentCallPaths.All.OrderBy(s => s, StringComparer.Ordinal).ToList());
        }

        [Fact]
        public void Vocabulary_ConstantIdentifiersNeverUseTheSerializedSpelling()
        {
            // Scan A matches IDENTIFIER tokens as well as string literals, so a constant named
            // "m_Mode" would make every consumer's own reference to it a guard violation.
            var offenders = typeof(PersistentCallPaths)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.Name.StartsWith("m_", StringComparison.Ordinal))
                .Select(f => f.Name)
                .ToList();

            Assert.True(offenders.Count == 0,
                $"member identifiers must not start with 'm_': {string.Join(", ", offenders)}");
        }

        [Fact]
        public void CallsArrayPath_ComposesTheRootUnderTheEventProperty()
        {
            Assert.Equal("m_OnClick.m_PersistentCalls.m_Calls", PersistentCallPaths.CallsArrayPath("m_OnClick"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void CallsArrayPath_NullOrEmptyEventPath_ThrowsRatherThanComposingAComponentRootPath(string? eventPropertyPath)
        {
            Assert.Throws<ArgumentException>(() => PersistentCallPaths.CallsArrayPath(eventPropertyPath!));
        }

        [Fact]
        public void ArgumentRelativePath_ComposesUnderMArguments()
        {
            Assert.Equal(
                "m_Arguments.m_IntArgument",
                PersistentCallPaths.ArgumentRelativePath(PersistentCallPaths.IntArgument));
        }

        [Fact]
        public void ArgumentRelativePath_NameOutsideTheArgumentFields_ThrowsNamingTheValue()
        {
            var ex = Assert.Throws<ArgumentException>(
                () => PersistentCallPaths.ArgumentRelativePath(PersistentCallPaths.Mode));
            Assert.Contains(PersistentCallPaths.Mode, ex.Message);
        }
    }
}
