#nullable enable
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Executes a <c>SetUnityEvent</c> op: projects <see cref="ValueNode.UnityEventListeners"/>
    /// through <see cref="UnityEventProjection.ToPersistentCalls"/> and straight-copies every
    /// <see cref="PersistentCallFields"/> field into the live component's persistent-call array via
    /// <see cref="PersistentCallPaths"/> constants only. Holds no mode/argument/callState number
    /// table and no serialized-path string literal of its own — <see cref="UnityEventProjection"/> is
    /// the sole owner of both (enforced by ModeArgBypassScanTests Scan A/B).
    /// </summary>
    internal static class UnityEventWriter
    {
        public static void Write(
            SerializedObject so, string eventPath, ValueNode.UnityEventListeners listeners,
            PlanExecutor.ExecutionResult result, IdentityMap map, Scene scene)
        {
            var calls = UnityEventProjection.ToPersistentCalls(listeners);

            var callsArray = so.FindProperty(PersistentCallPaths.CallsArrayPath(eventPath));
            if (callsArray == null)
            {
                Debug.LogWarning($"[SceneBuilder] UnityEvent property '{eventPath}' not found on '{so.targetObject}'.");
                return;
            }

            callsArray.arraySize = calls.Count;

            for (var i = 0; i < calls.Count; i++)
            {
                var f = calls[i];
                var e = callsArray.GetArrayElementAtIndex(i);

                var target = Resolve(f.Target, result, map, scene);
                e.FindPropertyRelative(PersistentCallPaths.Target).objectReferenceValue = target;
                e.FindPropertyRelative(PersistentCallPaths.TargetAssemblyTypeName).stringValue =
                    target != null ? target.GetType().AssemblyQualifiedName : "";
                e.FindPropertyRelative(PersistentCallPaths.MethodName).stringValue = f.MethodName;
                e.FindPropertyRelative(PersistentCallPaths.Mode).intValue = f.Mode;
                e.FindPropertyRelative(PersistentCallPaths.CallState).intValue = f.CallState;

                var args = e.FindPropertyRelative(PersistentCallPaths.Arguments);
                args.FindPropertyRelative(PersistentCallPaths.IntArgument).intValue = f.IntArgument;
                args.FindPropertyRelative(PersistentCallPaths.FloatArgument).floatValue = f.FloatArgument;
                args.FindPropertyRelative(PersistentCallPaths.StringArgument).stringValue = f.StringArgument;
                args.FindPropertyRelative(PersistentCallPaths.BoolArgument).boolValue = f.BoolArgument;

                var objectArgument = Resolve(f.ObjectArgument, result, map, scene);
                args.FindPropertyRelative(PersistentCallPaths.ObjectArgument).objectReferenceValue = objectArgument;
                args.FindPropertyRelative(PersistentCallPaths.ObjectArgumentAssemblyTypeName).stringValue =
                    objectArgument != null ? objectArgument.GetType().AssemblyQualifiedName : "";
            }
        }

        // The one reference-resolution point for a listener slot (m_Target or the object-mode
        // m_Arguments.m_ObjectArgument): null/Unsupported clears the slot (Unity "Missing"); a
        // scene reference resolves through the shared M5 ladder (the reference kind's own descent,
        // ObjectRefValues.Enumerate, so this site never pattern-matches the ref kind itself); an
        // asset reference resolves through M4.
        private static UnityEngine.Object? Resolve(
            ValueNode? node, PlanExecutor.ExecutionResult result, IdentityMap map, Scene scene)
        {
            if (node == null)
            {
                return null;
            }

            if (node is ValueNode.AssetRef { Ref: { } assetRef })
            {
                return AssetReferenceResolver.ResolveAssetObject(assetRef.Guid, assetRef.FileId);
            }

            var targetLogicalId = ObjectRefValues.Enumerate(node).Select(e => e.Ref.TargetLogicalId).FirstOrDefault();
            return targetLogicalId == null
                ? null
                : ObjectReferenceResolver.ResolveListenerTarget(
                    targetLogicalId, result.GameObjectsByLogicalId, result.ComponentsByLogicalId, map, scene);
        }
    }
}
