using System.Collections.Generic;
using UnityEngine;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Executes a Core <see cref="Plan"/> (code-&gt;scene) into the active scene via Unity APIs.
    /// The adapter is deliberately logic-light: it maps ops to Editor calls and nothing more.
    /// Reconcile-into-existing (for rebuilds) comes with sync-back; this first pass builds fresh.
    /// </summary>
    public static class PlanExecutor
    {
        /// <summary>Runs the plan; returns logicalId -&gt; created GameObject for id capture after save.</summary>
        public static Dictionary<string, GameObject> Execute(Plan plan)
        {
            var byLogicalId = new Dictionary<string, GameObject>();

            foreach (var op in plan.Ops)
            {
                switch (op)
                {
                    case CreateObject create:
                    {
                        byLogicalId[create.LogicalId] = new GameObject(create.Name);
                        break;
                    }
                    case SetName setName:
                    {
                        if (byLogicalId.TryGetValue(setName.LogicalId, out var go)) go.name = setName.Name;
                        break;
                    }
                    case SetParent setParent:
                    {
                        if (byLogicalId.TryGetValue(setParent.LogicalId, out var child))
                        {
                            Transform parent = null;
                            if (setParent.ParentLogicalId != null &&
                                byLogicalId.TryGetValue(setParent.ParentLogicalId, out var parentGo))
                            {
                                parent = parentGo.transform;
                            }
                            child.transform.SetParent(parent, worldPositionStays: false);
                        }
                        break;
                    }
                    case ReorderChild reorder:
                    {
                        if (byLogicalId.TryGetValue(reorder.LogicalId, out var go))
                            go.transform.SetSiblingIndex(reorder.SiblingIndex);
                        break;
                    }
                    case SetTag setTag:
                    {
                        if (byLogicalId.TryGetValue(setTag.LogicalId, out var go)) TrySetTag(go, setTag.Tag);
                        break;
                    }
                    case SetLayer setLayer:
                    {
                        if (byLogicalId.TryGetValue(setLayer.LogicalId, out var go)) go.layer = setLayer.Layer;
                        break;
                    }
                    case SetActive setActive:
                    {
                        if (byLogicalId.TryGetValue(setActive.LogicalId, out var go)) go.SetActive(setActive.Active);
                        break;
                    }
                    case SetStatic setStatic:
                    {
                        if (byLogicalId.TryGetValue(setStatic.LogicalId, out var go)) go.isStatic = setStatic.IsStatic;
                        break;
                    }
                    case SetField setField:
                    {
                        if (byLogicalId.TryGetValue(setField.LogicalId, out var go)) ApplyTransformField(go.transform, setField);
                        break;
                    }
                    case DestroyObject destroy:
                    {
                        if (byLogicalId.TryGetValue(destroy.LogicalId, out var go))
                        {
                            UnityEngine.Object.DestroyImmediate(go);
                            byLogicalId.Remove(destroy.LogicalId);
                        }
                        break;
                    }
                }
            }

            return byLogicalId;
        }

        private static void ApplyTransformField(Transform t, SetField op)
        {
            switch (op.Value)
            {
                case ValueNode.Vec3 v when op.Path == "m_LocalPosition":
                    t.localPosition = new Vector3(v.Value.X, v.Value.Y, v.Value.Z);
                    break;
                case ValueNode.Vec3 v when op.Path == "m_LocalScale":
                    t.localScale = new Vector3(v.Value.X, v.Value.Y, v.Value.Z);
                    break;
                case ValueNode.Quat q when op.Path == "m_LocalRotation":
                    t.localRotation = new Quaternion(q.Value.X, q.Value.Y, q.Value.Z, q.Value.W);
                    break;
                default:
                    Debug.LogWarning($"[SceneBuilder] Unhandled SetField '{op.Path}' on '{op.LogicalId}'.");
                    break;
            }
        }

        private static void TrySetTag(GameObject go, string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag == "Untagged") return;
            try { go.tag = tag; }
            catch { Debug.LogWarning($"[SceneBuilder] Tag '{tag}' is not in the Tag Manager; skipped for '{go.name}'."); }
        }
    }
}
