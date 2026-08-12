#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Reads a live UnityEvent field's persistent-call array into
    /// <see cref="ValueNode.UnityEventListeners"/> (or <see cref="ValueNode.Unsupported"/> for an
    /// out-of-table shape) via <see cref="UnityEventProjection.ProjectField"/>. Every serialized-path
    /// spelling is taken from <see cref="PersistentCallPaths"/> — this class holds no mode/argument/
    /// path literal of its own (ModeArgBypassScanTests Scan A allowlists ONLY UnityEventProjection.cs).
    /// </summary>
    internal static class UnityEventReader
    {
        /// <summary>
        /// True for a UnityEventBase-shaped property: it carries the <c>m_PersistentCalls.m_Calls</c>
        /// array every UnityEvent&lt;T...&gt; subclass serializes. Structural, not a type-name check,
        /// so it is version-robust and matches every generic arity, mirroring how
        /// <see cref="UnityEventWriter"/> locates the same array on write.
        /// </summary>
        public static bool IsUnityEventField(SerializedProperty prop)
        {
            var callsArray = prop.Copy().FindPropertyRelative(PersistentCallPaths.CallsRelativePath);
            return callsArray != null && callsArray.isArray;
        }

        /// <summary>
        /// Reads every persistent call's raw serialized fields — <c>m_Target</c> and the object-mode
        /// <c>m_Arguments.m_ObjectArgument</c> stamped through <paramref name="resolveListenerRef"/>,
        /// every other field copied verbatim — and hands the raw calls to
        /// <see cref="UnityEventProjection.ProjectField"/>, the sole owner of the mode/callstate
        /// number tables and the active-argument-slot pick.
        /// </summary>
        public static ValueNode ReadField(
            SerializedProperty eventProp, Func<UnityEngine.Object?, ValueNode?>? resolveListenerRef)
        {
            var callsArray = eventProp.FindPropertyRelative(PersistentCallPaths.CallsRelativePath);
            var calls = new List<PersistentCallFields>(callsArray.arraySize);

            for (var i = 0; i < callsArray.arraySize; i++)
            {
                var call = callsArray.GetArrayElementAtIndex(i);
                var args = call.FindPropertyRelative(PersistentCallPaths.Arguments);

                calls.Add(new PersistentCallFields
                {
                    Target = resolveListenerRef?.Invoke(
                        call.FindPropertyRelative(PersistentCallPaths.Target).objectReferenceValue),
                    MethodName = call.FindPropertyRelative(PersistentCallPaths.MethodName).stringValue,
                    Mode = call.FindPropertyRelative(PersistentCallPaths.Mode).intValue,
                    CallState = call.FindPropertyRelative(PersistentCallPaths.CallState).intValue,
                    ObjectArgument = resolveListenerRef?.Invoke(
                        args.FindPropertyRelative(PersistentCallPaths.ObjectArgument).objectReferenceValue),
                    IntArgument = args.FindPropertyRelative(PersistentCallPaths.IntArgument).intValue,
                    FloatArgument = args.FindPropertyRelative(PersistentCallPaths.FloatArgument).floatValue,
                    StringArgument = args.FindPropertyRelative(PersistentCallPaths.StringArgument).stringValue ?? "",
                    BoolArgument = args.FindPropertyRelative(PersistentCallPaths.BoolArgument).boolValue,
                });
            }

            return UnityEventProjection.ProjectField(calls);
        }
    }
}
