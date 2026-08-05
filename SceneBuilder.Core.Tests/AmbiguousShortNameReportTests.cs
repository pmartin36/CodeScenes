using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Conflict.AmbiguousTypeName is the one construction of a report for a component field whose
    // declared reference type name matched two or more loaded types, so it resolved to none.
    public class AmbiguousShortNameReportTests
    {
        private const string LogicalId = "joint-1";
        private const string ComponentType = "UnityEngine.HingeJoint";
        private const string MemberKey = "m_ConnectedBody";
        private const string AmbiguousName = "Rigidbody";

        private static readonly string[] TwoCandidates =
            { "UnityEngine.Rigidbody", "MyGame.Physics.Rigidbody" };

        private static Conflict Make(IReadOnlyList<string> candidates) =>
            Conflict.AmbiguousTypeName(LogicalId, ComponentType, MemberKey, AmbiguousName, candidates);

        [Fact]
        public void AmbiguousTypeName_TwoCollidingCandidates_ComposesOneMessageNamingBoth()
        {
            var conflict = Make(TwoCandidates);

            Assert.Equal(ConflictKind.AmbiguousTypeName, conflict.Kind);
            Assert.Equal(LogicalId, conflict.LogicalId);
            Assert.Null(conflict.Location);
            Assert.NotNull(conflict.Located);
            Assert.Equal(LogicalId, conflict.Located!.LogicalId);
            Assert.Equal(ComponentType, conflict.Located.ComponentTypeFullName);
            Assert.Equal(MemberKey, conflict.Located.MemberKey);
            Assert.Equal(conflict.Located.Compose(), conflict.Reason);

            Assert.Contains(AmbiguousName, conflict.Reason);
            foreach (var candidate in TwoCandidates)
            {
                Assert.Contains(candidate, conflict.Reason);
            }
        }

        [Fact]
        public void AmbiguousTypeName_CandidateOrderPermuted_ProducesTheSameMessage()
        {
            var forward = Make(TwoCandidates);
            var reversed = Make(TwoCandidates.Reverse().ToArray());

            Assert.Equal(forward.Reason, reversed.Reason);
        }

        [Fact]
        public void AmbiguousTypeName_KeyIsNameScoped_NotInstanceScoped()
        {
            var first = Conflict.AmbiguousTypeName(
                "joint-1", "UnityEngine.HingeJoint", "m_ConnectedBody", AmbiguousName, TwoCandidates);
            var second = Conflict.AmbiguousTypeName(
                "other-2", "UnityEngine.SpringJoint", "m_Target", AmbiguousName, TwoCandidates);

            Assert.Equal($"ambiguous-type-name:{AmbiguousName}", first.RecurrenceKey);
            Assert.Equal(first.RecurrenceKey, second.RecurrenceKey);
        }

        [Fact]
        public void Conflict_SettingTheAmbiguousKindDirectly_Throws()
        {
            Assert.Throws<ArgumentException>(() => new Conflict { Kind = ConflictKind.AmbiguousTypeName });

            // An unguarded kind still constructs by plain object initializer.
            var unguarded = new Conflict { Kind = ConflictKind.DanglingReference };
            Assert.Equal(ConflictKind.DanglingReference, unguarded.Kind);
        }

        [Fact]
        public void AmbiguousTypeName_WithScenePath_RecomposesTheReasonAndKeepsKindAndKey()
        {
            var conflict = Make(TwoCandidates);
            var moved = conflict with { Located = conflict.Located! with { ScenePath = "Rig/Joint" } };

            Assert.Contains("Rig/Joint", moved.Reason);
            Assert.Equal(ConflictKind.AmbiguousTypeName, moved.Kind);
            Assert.Equal(conflict.RecurrenceKey, moved.RecurrenceKey);
        }

        // Reflection sweep over every public static Conflict factory, so a future factory
        // producing a located kind cannot slip past this check by having an unusual signature.
        [Fact]
        public void Conflict_EveryPublicFactoryProducingALocatedKind_CarriesTheLocatedData()
        {
            var factories = typeof(Conflict)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.ReturnType == typeof(Conflict))
                .ToArray();

            Assert.NotEmpty(factories);

            foreach (var factory in factories)
            {
                var args = factory.GetParameters().Select(p => SynthesizeArg(p.ParameterType)).ToArray();
                var conflict = (Conflict)factory.Invoke(null, args)!;

                if (conflict.Kind != ConflictKind.UnrepresentableValue
                    && conflict.Kind != ConflictKind.AmbiguousTypeName)
                {
                    continue;
                }

                Assert.NotNull(conflict.Located);
                Assert.False(string.IsNullOrEmpty(conflict.Located!.LogicalId));
                Assert.False(string.IsNullOrEmpty(conflict.Located.ComponentTypeFullName));
                Assert.False(string.IsNullOrEmpty(conflict.Located.MemberKey));
            }
        }

        private static object? SynthesizeArg(Type t)
        {
            if (t == typeof(string)) return "x";
            if (t == typeof(IReadOnlyList<string>)) return new[] { "A.X", "B.X" };
            if (t == typeof(SourceSpan?)) return null;
            if (t == typeof(ConflictKind)) return ConflictKind.AmbiguousAnchor;
            if (t == typeof(LocatedReport)) return new LocatedReport("x", "x", "x", "detail");
            throw new NotSupportedException($"extend the argument synthesizer for {t}");
        }
    }
}
