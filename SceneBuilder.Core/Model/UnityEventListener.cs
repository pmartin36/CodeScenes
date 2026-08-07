using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    // The seven PersistentListenerMode / argument-mode spellings a UnityEvent's serialized
    // persistent call carries. Dynamic is the parameterless-or-runtime-typed call; the rest name a
    // static argument type baked at authoring time.
    public enum ListenerArgMode { Void, Int, Float, String, Bool, Object, Dynamic }

    // The three UnityEventCallState spellings governing when a persistent call fires relative to
    // play mode.
    public enum ListenerCallState { Off, RuntimeOnly, EditorAndRuntime }

    internal sealed class ListenerArgModeJsonConverter : JsonStringEnumConverter
    {
        public ListenerArgModeJsonConverter() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
        {
        }
    }

    internal sealed class ListenerCallStateJsonConverter : JsonStringEnumConverter
    {
        public ListenerCallStateJsonConverter() : base(namingPolicy: null, allowIntegerValues: false)
        {
        }
    }

    // One persistent call entry on a UnityEvent field: the target object/asset the call is made
    // on, the method it invokes, when it fires, and the static argument it was authored with (if
    // any). Property declaration order is the JSON key order. Get-only properties plus the one
    // validating constructor keep an illegal Target/ArgValue shape unconstructible: `with` cannot
    // set either property, so every instance is validated.
    public sealed record UnityEventListener
    {
        [JsonConstructor]
        public UnityEventListener(
            ValueNode? target,
            string methodName,
            ListenerArgMode argMode,
            ValueNode? argValue = null,
            ListenerCallState callState = ListenerCallState.RuntimeOnly)
        {
            Target = ValidateTarget(target);
            MethodName = methodName ?? "";
            ArgMode = argMode;
            ArgValue = ValidateArgValue(argMode, argValue);
            CallState = callState;
        }

        public ValueNode? Target { get; }

        public string MethodName { get; }

        [JsonConverter(typeof(ListenerCallStateJsonConverter))]
        public ListenerCallState CallState { get; }

        [JsonConverter(typeof(ListenerArgModeJsonConverter))]
        public ListenerArgMode ArgMode { get; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ValueNode? ArgValue { get; }

        // The dangling call (Unity's "Missing" target) is `null` and legal, never rejected or
        // pruned. Otherwise a target is one already-whole reference — never a container — so the
        // legal set is exactly the shipped reference/unresolved-reference carriers: a resolved
        // ObjectRef/AssetRef, or Unsupported, the marker ObjectRefLowering and
        // ComponentReconciler.RenderFieldValue both put at a reference slot that failed to
        // resolve.
        private static ValueNode? ValidateTarget(ValueNode? target)
        {
            if (target is null or ValueNode.ObjectRef or ValueNode.AssetRef or ValueNode.Unsupported)
            {
                return target;
            }

            throw new ArgumentException(
                "UnityEventListener target must be null, ValueNode.ObjectRef, ValueNode.AssetRef or " +
                $"ValueNode.Unsupported; got {target.GetType().Name}.",
                nameof(target));
        }

        // Void/Dynamic carry no static argument; the other four modes each require exactly the
        // ValueNode shape their PersistentListenerMode implies -- a Primitive of the matching
        // PrimitiveKind for Int/Float/String/Bool, or one already-whole reference for Object.
        private static ValueNode? ValidateArgValue(ListenerArgMode mode, ValueNode? argValue)
        {
            switch (mode)
            {
                case ListenerArgMode.Void:
                case ListenerArgMode.Dynamic:
                    if (argValue is not null)
                    {
                        throw new ArgumentException(
                            $"UnityEventListener argValue must be null when argMode is {mode}; got " +
                            $"{argValue.GetType().Name}.",
                            nameof(argValue));
                    }

                    return null;

                case ListenerArgMode.Int:
                    return RequirePrimitive(mode, argValue, PrimitiveKind.Int);
                case ListenerArgMode.Float:
                    return RequirePrimitive(mode, argValue, PrimitiveKind.Float);
                case ListenerArgMode.String:
                    return RequirePrimitive(mode, argValue, PrimitiveKind.String);
                case ListenerArgMode.Bool:
                    return RequirePrimitive(mode, argValue, PrimitiveKind.Bool);

                case ListenerArgMode.Object:
                    if (argValue is ValueNode.ObjectRef or ValueNode.AssetRef or ValueNode.Unsupported)
                    {
                        return argValue;
                    }

                    throw new ArgumentException(
                        "UnityEventListener argValue for ArgMode.Object must be ValueNode.ObjectRef, " +
                        "ValueNode.AssetRef or ValueNode.Unsupported; got " +
                        (argValue is null ? "null" : argValue.GetType().Name) + ".",
                        nameof(argValue));

                default:
                    throw new ArgumentException($"Unrecognized ListenerArgMode {mode}.", nameof(mode));
            }
        }

        private static ValueNode RequirePrimitive(ListenerArgMode mode, ValueNode? argValue, PrimitiveKind kind)
        {
            if (argValue is ValueNode.Primitive primitive && primitive.Kind == kind)
            {
                return argValue;
            }

            throw new ArgumentException(
                $"UnityEventListener argValue for ArgMode.{mode} must be a Primitive({kind}); got " +
                (argValue is null ? "null" : argValue.GetType().Name) + ".",
                nameof(argValue));
        }
    }
}
