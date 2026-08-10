using System;
using System.Collections.Generic;
using System.Linq;

namespace SceneBuilder.Core.Model
{
    /// <summary>
    /// The ONE vocabulary of the SERIALIZED field spellings a UnityEvent persistent call uses. Both adapter
    /// directions take every spelling from here and hold none of their own; ModeArgBypassScanTests' Scan A
    /// enforces that, and derives its own token list from All.
    /// Lives in this file deliberately: UnityEventProjection.cs is the only path Scan A allowlists, so a
    /// separate file would make these literals a violation of the very guard they exist to serve.
    /// MEASURED against UnityEngine.CoreModule.dll (Unity 6000.5.3f1) -- PersistentCall, ArgumentCache,
    /// PersistentCallGroup.m_Calls, UnityEventBase.m_PersistentCalls.
    /// </summary>
    public static class PersistentCallPaths
    {
        // PersistentCall
        public const string Target = "m_Target";
        public const string TargetAssemblyTypeName = "m_TargetAssemblyTypeName";
        public const string MethodName = "m_MethodName";
        public const string Mode = "m_Mode";
        public const string Arguments = "m_Arguments";
        public const string CallState = "m_CallState";

        // ArgumentCache -- the m_Arguments child
        public const string ObjectArgument = "m_ObjectArgument";
        public const string ObjectArgumentAssemblyTypeName = "m_ObjectArgumentAssemblyTypeName";
        public const string IntArgument = "m_IntArgument";
        public const string FloatArgument = "m_FloatArgument";
        public const string StringArgument = "m_StringArgument";
        public const string BoolArgument = "m_BoolArgument";

        // The call-array root, relative to the UnityEvent field's own propertyPath.
        public const string PersistentCalls = "m_PersistentCalls";
        public const string Calls = "m_Calls";
        public const string CallsRelativePath = PersistentCalls + "." + Calls;

        public static readonly IReadOnlyList<string> CallFields = new[]
        {
            Target, TargetAssemblyTypeName, MethodName, Mode, Arguments, CallState,
        };

        public static readonly IReadOnlyList<string> ArgumentFields = new[]
        {
            ObjectArgument, ObjectArgumentAssemblyTypeName, IntArgument, FloatArgument, StringArgument, BoolArgument,
        };

        public static readonly IReadOnlyList<string> All =
            CallFields.Concat(ArgumentFields).Concat(new[] { PersistentCalls, Calls }).ToList();

        public static string CallsArrayPath(string eventPropertyPath)
        {
            if (string.IsNullOrWhiteSpace(eventPropertyPath))
            {
                throw new ArgumentException("eventPropertyPath must be non-empty.", nameof(eventPropertyPath));
            }

            return $"{eventPropertyPath}.{CallsRelativePath}";
        }

        public static string ArgumentRelativePath(string argumentField)
        {
            if (!ArgumentFields.Contains(argumentField))
            {
                throw new ArgumentException(
                    $"'{argumentField}' is not one of the ArgumentCache fields.", nameof(argumentField));
            }

            return $"{Arguments}.{argumentField}";
        }
    }

    // The raw serialized fields of one persistent call, as UnityEngine.Events.PersistentCall +
    // ArgumentCache store them (m_Target, m_MethodName, m_Mode, m_Arguments.*, m_CallState).
    // The record omits m_TargetAssemblyTypeName / m_ObjectArgumentAssemblyTypeName: the adapter
    // derives them from the live resolved target at write time (their spellings live on
    // PersistentCallPaths above).
    public sealed record PersistentCallFields
    {
        public ValueNode? Target { get; init; }
        public string MethodName { get; init; } = "";
        public int Mode { get; init; }
        public int CallState { get; init; }
        public ValueNode? ObjectArgument { get; init; }
        public int IntArgument { get; init; }
        public float FloatArgument { get; init; }
        public string StringArgument { get; init; } = "";
        public bool BoolArgument { get; init; }
    }

    // Total, discriminated. A call whose shape is outside the table is a distinct inhabitant
    // carrying its raw token, never a null listener and never coerced into a supported mode.
    public abstract record CallProjection
    {
        public sealed record Projected(UnityEventListener Listener) : CallProjection;

        public sealed record OutOfTable(string RawToken) : CallProjection;
    }

    // The one place that knows a PersistentListenerMode / UnityEventCallState number. Both adapter
    // directions copy PersistentCallFields without branching; only this class maps a raw number to
    // (and from) the model's ListenerArgMode / ListenerCallState.
    public static class UnityEventProjection
    {
        private static readonly IReadOnlyDictionary<ListenerArgMode, int> ModeNumbers = new Dictionary<ListenerArgMode, int>
        {
            [ListenerArgMode.Dynamic] = 0,
            [ListenerArgMode.Void] = 1,
            [ListenerArgMode.Object] = 2,
            [ListenerArgMode.Int] = 3,
            [ListenerArgMode.Float] = 4,
            [ListenerArgMode.String] = 5,
            [ListenerArgMode.Bool] = 6,
        };

        private static readonly IReadOnlyDictionary<ListenerCallState, int> CallStateNumbers = new Dictionary<ListenerCallState, int>
        {
            [ListenerCallState.Off] = 0,
            [ListenerCallState.EditorAndRuntime] = 1,
            [ListenerCallState.RuntimeOnly] = 2,
        };

        public static int ModeNumber(ListenerArgMode mode) => ModeNumbers[mode];

        public static bool TryModeFromNumber(int number, out ListenerArgMode mode)
        {
            foreach (var pair in ModeNumbers)
            {
                if (pair.Value == number)
                {
                    mode = pair.Key;
                    return true;
                }
            }

            mode = default;
            return false;
        }

        public static int CallStateNumber(ListenerCallState state) => CallStateNumbers[state];

        public static bool TryCallStateFromNumber(int number, out ListenerCallState state)
        {
            foreach (var pair in CallStateNumbers)
            {
                if (pair.Value == number)
                {
                    state = pair.Key;
                    return true;
                }
            }

            state = default;
            return false;
        }

        // Total: writes the active argument slot from ArgValue and leaves every other slot at its
        // type default, so a straight copy of every field also clears a stale slot left by a
        // previous mode.
        public static PersistentCallFields ToPersistentCall(UnityEventListener listener)
        {
            var fields = new PersistentCallFields
            {
                Target = listener.Target,
                MethodName = listener.MethodName,
                Mode = ModeNumber(listener.ArgMode),
                CallState = CallStateNumber(listener.CallState),
            };

            return listener.ArgMode switch
            {
                ListenerArgMode.Object => fields with { ObjectArgument = listener.ArgValue },
                ListenerArgMode.Int => fields with { IntArgument = (int)((ValueNode.Primitive)listener.ArgValue!).Value! },
                ListenerArgMode.Float => fields with { FloatArgument = (float)((ValueNode.Primitive)listener.ArgValue!).Value! },
                ListenerArgMode.String => fields with { StringArgument = (string)((ValueNode.Primitive)listener.ArgValue!).Value! },
                ListenerArgMode.Bool => fields with { BoolArgument = (bool)((ValueNode.Primitive)listener.ArgValue!).Value! },
                _ => fields,
            };
        }

        public static IReadOnlyList<PersistentCallFields> ToPersistentCalls(ValueNode.UnityEventListeners listeners) =>
            listeners.Listeners.Select(ToPersistentCall).ToList();

        // Total: never throws, never returns null. A number outside the table, an Object-mode call
        // with a C# null ObjectArgument, or a Target/ObjectArgument carrying a ValueNode kind the
        // listener model forbids all become OutOfTable rather than a coerced listener. The legal
        // shape is enforced by UnityEventListener's own validating constructor -- this method does
        // not restate that rule, it just catches the constructor's rejection.
        public static CallProjection ProjectCall(PersistentCallFields fields)
        {
            if (!TryModeFromNumber(fields.Mode, out var mode) || !TryCallStateFromNumber(fields.CallState, out var callState))
            {
                return new CallProjection.OutOfTable(RawToken(fields));
            }

            ValueNode? argValue = mode switch
            {
                ListenerArgMode.Object => fields.ObjectArgument,
                ListenerArgMode.Int => ValueNode.Primitive.Int(fields.IntArgument),
                ListenerArgMode.Float => ValueNode.Primitive.Float(fields.FloatArgument),
                ListenerArgMode.String => ValueNode.Primitive.String(fields.StringArgument),
                ListenerArgMode.Bool => ValueNode.Primitive.Bool(fields.BoolArgument),
                _ => null,
            };

            try
            {
                return new CallProjection.Projected(new UnityEventListener(fields.Target, fields.MethodName, mode, argValue, callState));
            }
            catch (ArgumentException)
            {
                return new CallProjection.OutOfTable(RawToken(fields));
            }
        }

        // ValueNode.UnityEventListeners when every call projects (order preserved, empty list legal
        // -- the cleared event); ValueNode.Unsupported, whose RawToken newline-joins EVERY call's
        // token (not just the offending one), when any call does not project.
        public static ValueNode ProjectField(IReadOnlyList<PersistentCallFields> calls)
        {
            var listeners = new List<UnityEventListener>(calls.Count);
            var anyOutOfTable = false;

            foreach (var call in calls)
            {
                if (ProjectCall(call) is CallProjection.Projected projected)
                {
                    listeners.Add(projected.Listener);
                }
                else
                {
                    anyOutOfTable = true;
                }
            }

            return anyOutOfTable
                ? new ValueNode.Unsupported(string.Join("\n", calls.Select(RawToken)))
                : new ValueNode.UnityEventListeners(listeners);
        }

        public static string RawToken(PersistentCallFields fields) =>
            $"persistentCall(mode={fields.Mode}, callState={fields.CallState}, method={fields.MethodName})";
    }
}
