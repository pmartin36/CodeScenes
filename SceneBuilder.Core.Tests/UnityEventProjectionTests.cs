using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // UnityEventProjection is the one place that knows a PersistentListenerMode / UnityEventCallState
    // number: a total, reversible mapping between UnityEventListener and the raw persistent-call
    // fields both adapter directions copy without branching.
    public class UnityEventProjectionTests
    {
        private static UnityEventListener Listener(
            ValueNode? target,
            string methodName,
            ListenerArgMode argMode,
            ValueNode? argValue = null,
            ListenerCallState callState = ListenerCallState.RuntimeOnly) =>
            new(target, methodName, argMode, argValue, callState);

        // ---- THE TABLE: total and reversible for every enum member, not a sample ----------------

        private static readonly IReadOnlyDictionary<ListenerArgMode, int> ExpectedModeNumbers =
            new Dictionary<ListenerArgMode, int>
            {
                [ListenerArgMode.Dynamic] = 0,
                [ListenerArgMode.Void] = 1,
                [ListenerArgMode.Object] = 2,
                [ListenerArgMode.Int] = 3,
                [ListenerArgMode.Float] = 4,
                [ListenerArgMode.String] = 5,
                [ListenerArgMode.Bool] = 6,
            };

        public static IEnumerable<object[]> AllArgModes() =>
            Enum.GetValues(typeof(ListenerArgMode)).Cast<ListenerArgMode>().Select(m => new object[] { m });

        [Theory]
        [MemberData(nameof(AllArgModes))]
        public void ModeNumber_MatchesTheTable_ForEveryListenerArgMode(ListenerArgMode mode)
        {
            Assert.Equal(ExpectedModeNumbers[mode], UnityEventProjection.ModeNumber(mode));
        }

        [Theory]
        [MemberData(nameof(AllArgModes))]
        public void TryModeFromNumber_OfModeNumber_RoundTripsToTheSameMode(ListenerArgMode mode)
        {
            var number = UnityEventProjection.ModeNumber(mode);

            Assert.True(UnityEventProjection.TryModeFromNumber(number, out var roundTripped));
            Assert.Equal(mode, roundTripped);
        }

        [Theory]
        [InlineData(ListenerCallState.Off, 0)]
        [InlineData(ListenerCallState.EditorAndRuntime, 1)]
        [InlineData(ListenerCallState.RuntimeOnly, 2)]
        public void CallStateNumber_MatchesTheMeasuredTable_NotSpecText(ListenerCallState state, int expected)
        {
            Assert.Equal(expected, UnityEventProjection.CallStateNumber(state));
        }

        [Theory]
        [InlineData(ListenerCallState.Off)]
        [InlineData(ListenerCallState.EditorAndRuntime)]
        [InlineData(ListenerCallState.RuntimeOnly)]
        public void TryCallStateFromNumber_OfCallStateNumber_RoundTripsToTheSameState(ListenerCallState state)
        {
            var number = UnityEventProjection.CallStateNumber(state);

            Assert.True(UnityEventProjection.TryCallStateFromNumber(number, out var roundTripped));
            Assert.Equal(state, roundTripped);
        }

        [Fact]
        public void TryModeFromNumber_OutsideZeroToSix_ReturnsFalse()
        {
            Assert.False(UnityEventProjection.TryModeFromNumber(7, out _));
            Assert.False(UnityEventProjection.TryModeFromNumber(-1, out _));
        }

        [Fact]
        public void TryCallStateFromNumber_OutsideZeroToTwo_ReturnsFalse()
        {
            Assert.False(UnityEventProjection.TryCallStateFromNumber(3, out _));
            Assert.False(UnityEventProjection.TryCallStateFromNumber(-1, out _));
        }

        // ---- REVERSIBILITY: ProjectCall(ToPersistentCall(listener)) == Projected(listener) -------

        public static IEnumerable<object[]> ReversibleListeners()
        {
            yield return new object[] { "Dynamic", Listener(new ValueNode.ObjectRef("go-1"), "Handler", ListenerArgMode.Dynamic) };
            yield return new object[] { "Void", Listener(new ValueNode.ObjectRef("go-1"), "Handler", ListenerArgMode.Void) };
            yield return new object[] { "Object_ObjectRef", Listener(null, "Assign", ListenerArgMode.Object, new ValueNode.ObjectRef("target-b")) };
            yield return new object[]
            {
                "Object_AssetRef",
                Listener(null, "SetMaterial", ListenerArgMode.Object, new ValueNode.AssetRef(new AssetRef { Guid = "asset-guid", FileId = 1 })),
            };
            yield return new object[] { "Object_NoneObjectRef", Listener(null, "Assign", ListenerArgMode.Object, new ValueNode.ObjectRef(null)) };
            yield return new object[] { "Int", Listener(null, "SetLevel", ListenerArgMode.Int, ValueNode.Primitive.Int(7)) };
            yield return new object[] { "Float", Listener(null, "SetVol", ListenerArgMode.Float, ValueNode.Primitive.Float(1.5f)) };
            yield return new object[] { "String", Listener(null, "SetText", ListenerArgMode.String, ValueNode.Primitive.String("hi")) };
            yield return new object[] { "Bool", Listener(null, "SetVisible", ListenerArgMode.Bool, ValueNode.Primitive.Bool(true)) };
            yield return new object[] { "NullTarget", Listener(null, "Handler", ListenerArgMode.Void) };
            yield return new object[] { "UnsupportedTarget", Listener(new ValueNode.Unsupported("Handle(12345)"), "Handler", ListenerArgMode.Void) };
            yield return new object[]
            {
                "AssetRefTarget",
                Listener(new ValueNode.AssetRef(new AssetRef { Guid = "asset-guid", FileId = 2 }), "Handler", ListenerArgMode.Void),
            };
            yield return new object[] { "CallState_Off", Listener(new ValueNode.ObjectRef("go-1"), "Handler", ListenerArgMode.Void, callState: ListenerCallState.Off) };
            yield return new object[] { "CallState_RuntimeOnly", Listener(new ValueNode.ObjectRef("go-1"), "Handler", ListenerArgMode.Void, callState: ListenerCallState.RuntimeOnly) };
            yield return new object[] { "CallState_EditorAndRuntime", Listener(new ValueNode.ObjectRef("go-1"), "Handler", ListenerArgMode.Void, callState: ListenerCallState.EditorAndRuntime) };
        }

        [Theory]
        [MemberData(nameof(ReversibleListeners))]
        public void ProjectCall_OfToPersistentCall_IsProjectedWithTheOriginalListener(string _, UnityEventListener listener)
        {
            var fields = UnityEventProjection.ToPersistentCall(listener);
            var projection = UnityEventProjection.ProjectCall(fields);

            var projected = Assert.IsType<CallProjection.Projected>(projection);
            Assert.Equal(listener, projected.Listener);
        }

        // ---- SLOT EXCLUSIVITY: each static mode populates exactly its own slot -------------------

        [Theory]
        [InlineData(ListenerArgMode.Int)]
        [InlineData(ListenerArgMode.Float)]
        [InlineData(ListenerArgMode.String)]
        [InlineData(ListenerArgMode.Bool)]
        public void ToPersistentCall_EachStaticMode_PopulatesOnlyItsOwnArgumentSlot(ListenerArgMode mode)
        {
            ValueNode argValue = mode switch
            {
                ListenerArgMode.Int => ValueNode.Primitive.Int(7),
                ListenerArgMode.Float => ValueNode.Primitive.Float(1.5f),
                ListenerArgMode.String => ValueNode.Primitive.String("hi"),
                ListenerArgMode.Bool => ValueNode.Primitive.Bool(true),
                _ => throw new InvalidOperationException(),
            };

            var fields = UnityEventProjection.ToPersistentCall(Listener(null, "Handler", mode, argValue));

            Assert.Equal(mode == ListenerArgMode.Int ? 7 : 0, fields.IntArgument);
            Assert.Equal(mode == ListenerArgMode.Float ? 1.5f : 0f, fields.FloatArgument);
            Assert.Equal(mode == ListenerArgMode.String ? "hi" : "", fields.StringArgument);
            Assert.Equal(mode == ListenerArgMode.Bool, fields.BoolArgument);
            Assert.Null(fields.ObjectArgument);
        }

        [Fact]
        public void ToPersistentCall_ObjectMode_PopulatesOnlyObjectArgumentSlot()
        {
            var fields = UnityEventProjection.ToPersistentCall(
                Listener(null, "Assign", ListenerArgMode.Object, new ValueNode.ObjectRef("target-b")));

            Assert.Equal("target-b", ((ValueNode.ObjectRef)fields.ObjectArgument!).TargetLogicalId);
            Assert.Equal(0, fields.IntArgument);
            Assert.Equal(0f, fields.FloatArgument);
            Assert.Equal("", fields.StringArgument);
            Assert.False(fields.BoolArgument);
        }

        [Theory]
        [InlineData(ListenerArgMode.Void)]
        [InlineData(ListenerArgMode.Dynamic)]
        public void ToPersistentCall_VoidOrDynamicMode_AllArgumentSlotsAtDefault(ListenerArgMode mode)
        {
            var fields = UnityEventProjection.ToPersistentCall(Listener(new ValueNode.ObjectRef("go-1"), "Handler", mode));

            Assert.Equal(0, fields.IntArgument);
            Assert.Equal(0f, fields.FloatArgument);
            Assert.Equal("", fields.StringArgument);
            Assert.False(fields.BoolArgument);
            Assert.Null(fields.ObjectArgument);
        }

        // ---- STALE-SLOT CANONICALIZATION: the MODEL is identical either way ----------------------

        [Fact]
        public void ProjectCall_VoidModeWithAStaleIntArgument_ProjectsAVoidListener()
        {
            var fields = new PersistentCallFields
            {
                Target = new ValueNode.ObjectRef("go-1"),
                MethodName = "Handler",
                Mode = UnityEventProjection.ModeNumber(ListenerArgMode.Void),
                CallState = UnityEventProjection.CallStateNumber(ListenerCallState.RuntimeOnly),
                IntArgument = 7,
            };

            var listener = Assert.IsType<CallProjection.Projected>(UnityEventProjection.ProjectCall(fields)).Listener;

            Assert.Equal(ListenerArgMode.Void, listener.ArgMode);
            Assert.Null(listener.ArgValue);
        }

        [Fact]
        public void ToPersistentCall_OfAProjectedVoidListener_DropsTheStaleIntArgument()
        {
            var fields = new PersistentCallFields
            {
                Target = new ValueNode.ObjectRef("go-1"),
                MethodName = "Handler",
                Mode = UnityEventProjection.ModeNumber(ListenerArgMode.Void),
                CallState = UnityEventProjection.CallStateNumber(ListenerCallState.RuntimeOnly),
                IntArgument = 7,
            };
            var listener = Assert.IsType<CallProjection.Projected>(UnityEventProjection.ProjectCall(fields)).Listener;

            var roundTripped = UnityEventProjection.ToPersistentCall(listener);

            Assert.Equal(0, roundTripped.IntArgument);
        }

        // ---- OUT-OF-TABLE: never coerced into a supported mode, never throws ---------------------

        [Fact]
        public void ProjectCall_ModeAboveTable_IsOutOfTable_TokenCarriesModeAndMethod()
        {
            var fields = new PersistentCallFields { Target = new ValueNode.ObjectRef("go-1"), MethodName = "Handler", Mode = 7, CallState = 2 };

            var outOfTable = Assert.IsType<CallProjection.OutOfTable>(UnityEventProjection.ProjectCall(fields));

            Assert.Contains("7", outOfTable.RawToken);
            Assert.Contains("Handler", outOfTable.RawToken);
        }

        [Fact]
        public void ProjectCall_NegativeMode_IsOutOfTable()
        {
            var fields = new PersistentCallFields { Mode = -1, CallState = 2, MethodName = "Handler" };

            Assert.IsType<CallProjection.OutOfTable>(UnityEventProjection.ProjectCall(fields));
        }

        [Fact]
        public void ProjectCall_CallStateAboveTable_IsOutOfTable()
        {
            var fields = new PersistentCallFields { Mode = 1, CallState = 3, MethodName = "Handler" };

            Assert.IsType<CallProjection.OutOfTable>(UnityEventProjection.ProjectCall(fields));
        }

        [Fact]
        public void ProjectCall_ObjectModeWithNullObjectArgument_IsOutOfTable_NotCoercedToVoid()
        {
            var fields = new PersistentCallFields
            {
                Mode = UnityEventProjection.ModeNumber(ListenerArgMode.Object),
                CallState = 2,
                MethodName = "Assign",
                ObjectArgument = null,
            };

            Assert.IsType<CallProjection.OutOfTable>(UnityEventProjection.ProjectCall(fields));
        }

        [Fact]
        public void ProjectCall_TargetCarryingAPrimitiveValueNode_IsOutOfTable()
        {
            var fields = new PersistentCallFields { Target = ValueNode.Primitive.Int(1), Mode = 1, CallState = 2, MethodName = "Handler" };

            Assert.IsType<CallProjection.OutOfTable>(UnityEventProjection.ProjectCall(fields));
        }

        // ---- FIELD LEVEL: ProjectField -------------------------------------------------------------

        [Fact]
        public void ProjectField_EmptyList_IsAnEmptyUnityEventListeners()
        {
            var result = UnityEventProjection.ProjectField(Array.Empty<PersistentCallFields>());

            var listeners = Assert.IsType<ValueNode.UnityEventListeners>(result);
            Assert.Empty(listeners.Listeners);
        }

        [Fact]
        public void ProjectField_TwoInTableCalls_IsUnityEventListenersInArrayOrder()
        {
            var first = UnityEventProjection.ToPersistentCall(Listener(new ValueNode.ObjectRef("go-1"), "Show", ListenerArgMode.Void));
            var second = UnityEventProjection.ToPersistentCall(
                Listener(new ValueNode.ObjectRef("go-2"), "SetActive", ListenerArgMode.Bool, ValueNode.Primitive.Bool(true)));

            var listeners = Assert.IsType<ValueNode.UnityEventListeners>(UnityEventProjection.ProjectField(new[] { first, second }));

            Assert.Equal(2, listeners.Listeners.Count);
            Assert.Equal("Show", listeners.Listeners[0].MethodName);
            Assert.Equal("SetActive", listeners.Listeners[1].MethodName);
        }

        [Fact]
        public void ProjectField_OneOutOfTableCall_IsUnsupported_WithATokenLineForEveryCall()
        {
            var inTable = UnityEventProjection.ToPersistentCall(Listener(new ValueNode.ObjectRef("go-1"), "Show", ListenerArgMode.Void));
            var outOfTable = new PersistentCallFields { Target = new ValueNode.ObjectRef("go-2"), MethodName = "Bad", Mode = 9, CallState = 2 };

            var unsupported = Assert.IsType<ValueNode.Unsupported>(UnityEventProjection.ProjectField(new[] { inTable, outOfTable }));

            var lines = unsupported.RawToken.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.Contains("Show", lines[0]);
            Assert.Contains("Bad", lines[1]);
        }

        [Fact]
        public void ProjectField_UnsupportedResult_RoundTripsByteIdenticallyThroughCanonicalJson()
        {
            var outOfTable = new PersistentCallFields { Target = new ValueNode.ObjectRef("go-2"), MethodName = "Bad", Mode = 9, CallState = 2 };
            var result = UnityEventProjection.ProjectField(new[] { outOfTable });

            var json1 = CanonicalJson.Serialize<ValueNode>(result);
            var roundTripped = CanonicalJson.Deserialize<ValueNode>(json1);
            var json2 = CanonicalJson.Serialize<ValueNode>(roundTripped);

            Assert.Equal(json1, json2);
            Assert.Equal(result, roundTripped);
        }

        // ---- EMPTY METHOD NAME: projected, never rejected -----------------------------------------

        [Fact]
        public void ProjectCall_EmptyMethodName_ProjectsAListener_NotRejected()
        {
            var fields = new PersistentCallFields { Target = new ValueNode.ObjectRef("go-1"), MethodName = "", Mode = 1, CallState = 2 };

            var listener = Assert.IsType<CallProjection.Projected>(UnityEventProjection.ProjectCall(fields)).Listener;

            Assert.Equal("", listener.MethodName);
        }

        // ---- ToPersistentCalls: preserves count, order, and per-listener equivalence -------------

        [Fact]
        public void ToPersistentCalls_PreservesCountAndOrder_AndEqualsPerListenerToPersistentCall()
        {
            var listeners = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.ObjectRef("go-1"), "Show", ListenerArgMode.Void),
                Listener(new ValueNode.ObjectRef("go-2"), "SetActive", ListenerArgMode.Bool, ValueNode.Primitive.Bool(true)),
            });

            var calls = UnityEventProjection.ToPersistentCalls(listeners);

            Assert.Equal(2, calls.Count);
            Assert.Equal(UnityEventProjection.ToPersistentCall(listeners.Listeners[0]), calls[0]);
            Assert.Equal(UnityEventProjection.ToPersistentCall(listeners.Listeners[1]), calls[1]);
        }

        // ---- PersistentCallFields: init-only record, value equality, stated defaults --------------

        [Fact]
        public void PersistentCallFields_Defaults_MethodNameAndStringArgumentEmpty_EverythingElseDefault()
        {
            var fields = new PersistentCallFields();

            Assert.Equal("", fields.MethodName);
            Assert.Equal("", fields.StringArgument);
            Assert.Equal(0, fields.Mode);
            Assert.Equal(0, fields.CallState);
            Assert.Equal(0, fields.IntArgument);
            Assert.Equal(0f, fields.FloatArgument);
            Assert.False(fields.BoolArgument);
            Assert.Null(fields.Target);
            Assert.Null(fields.ObjectArgument);
        }

        [Fact]
        public void PersistentCallFields_ValueEquality_StructurallyEqualInstancesAreEqual()
        {
            var a = new PersistentCallFields { Target = new ValueNode.ObjectRef("go-1"), MethodName = "Handler", Mode = 1, CallState = 2 };
            var b = new PersistentCallFields { Target = new ValueNode.ObjectRef("go-1"), MethodName = "Handler", Mode = 1, CallState = 2 };

            Assert.Equal(a, b);
        }
    }
}
