#nullable enable
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using UnityRemovedGameObject = UnityEditor.SceneManagement.RemovedGameObject;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// m-nested-props b6-t2 (specs/24-nested-prefab-overrides.md write mechanics): the four
    /// added/removed-child-GameObject Plan ops, applied IN PLACE against the live instance —
    /// mirrors <see cref="InstanceOverrideExecutor"/>'s component ops but for whole GameObjects.
    /// Parenting an added child under a resolved in-instance sub-object makes Unity auto-record the
    /// AddedGameObjects override (no dedicated "add override" API); destroying a live source-derived
    /// child auto-records a RemovedGameObjects override the same way. Never calls
    /// <see cref="PrefabUtility.InstantiatePrefab"/> anywhere in this path.
    /// </summary>
    internal static partial class InstanceOverrideExecutor
    {
        public static void Apply(AddInstanceChild op, PlanExecutor.ExecutionResult result, IdentityMap map, Scene scene)
        {
            var root = InstanceRoot(op.LogicalId, result);
            var parent = ResolveTargetObject(root, op.Target);
            if (parent == null)
            {
                return;
            }

            BuildAddedChild(parent, op.Node, result, map, scene);
        }

        public static void Apply(RemoveInstanceChild op, PlanExecutor.ExecutionResult result)
        {
            var root = InstanceRoot(op.LogicalId, result);
            if (root == null)
            {
                return;
            }

            var child = PrefabInstanceProbe.ResolveSubObjectByChildPath(root, op.Target.ChildPath);
            if (child != null)
            {
                UnityEngine.Object.DestroyImmediate(child);
            }
        }

        public static void Apply(RevertAddedChild op, PlanExecutor.ExecutionResult result)
        {
            var root = InstanceRoot(op.LogicalId, result);
            var parent = ResolveTargetObject(root, op.Target);
            if (parent == null)
            {
                return;
            }

            GameObject? child = null;
            if (result.GameObjectsByLogicalId.TryGetValue(op.ChildLogicalId, out var mapped) && mapped != null)
            {
                child = mapped;
            }
            else
            {
                var name = ChildName(op.ChildLogicalId);
                child = parent.transform.Find(name)?.gameObject;
            }

            if (child != null)
            {
                PrefabUtility.RevertAddedGameObject(child, InteractionMode.AutomatedAction);
                result.GameObjectsByLogicalId.Remove(op.ChildLogicalId);
            }
        }

        public static void Apply(RevertRemovedChild op, PlanExecutor.ExecutionResult result)
        {
            var root = InstanceRoot(op.LogicalId, result);
            if (root == null)
            {
                return;
            }

            var removed = PrefabUtility.GetRemovedGameObjects(root);
            if (removed == null)
            {
                return;
            }

            var sourceRoot = PrefabUtility.GetCorrespondingObjectFromSource(root) as GameObject;
            var matchBySubKey = !op.Target.SubKey.Equals(DefaultSubKey);

            foreach (UnityRemovedGameObject entry in removed)
            {
                if (entry.assetGameObject == null)
                {
                    continue;
                }

                var matches = matchBySubKey
                    ? PrefabInstanceProbe.SubKeyOf(entry.assetGameObject).Equals(op.Target.SubKey)
                    : PrefabInstanceProbe.RelativePath(sourceRoot, entry.assetGameObject) == op.Target.ChildPath;

                if (matches)
                {
                    PrefabUtility.RevertRemovedGameObject(entry.assetGameObject, root, InteractionMode.AutomatedAction);
                    return;
                }
            }
        }

        // Builds a live GameObject (with its full component/field payload) UNDER an already-resolved
        // in-instance parent — parenting alone is what makes Unity record the AddedGameObjects
        // override, so this NEVER re-instantiates. Recurses into node.Children so a multi-level added
        // subtree is built in one pass (Unity records only the top-level added GameObject; nested
        // children of an added object are part of it, not separate overrides).
        private static GameObject BuildAddedChild(
            GameObject parent, GameObjectNode node, PlanExecutor.ExecutionResult result, IdentityMap map, Scene scene)
        {
            // m-ui-recttransform b3-t1 (iteration 2): create AS a RectTransform when authored
            // (never a plain GameObject promoted later) — same entry point PlanExecutor.CreateObject
            // uses, so there is ONE spelling of "create a GameObject for this Kind".
            var go = LiveTransformWrite.Create(node.Name, node.Transform.Kind);
            go.transform.SetParent(parent.transform, worldPositionStays: false);

            TrySetChildTag(go, node.Tag);
            go.layer = node.Layer;
            go.SetActive(node.Active);
            go.isStatic = node.IsStatic;

            var t = go.transform;
            var position = node.Transform.Position;
            var rotation = node.Transform.Rotation;
            var scale = node.Transform.Scale;
            t.localPosition = new Vector3(position.X, position.Y, position.Z);
            t.localRotation = new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
            t.localScale = new Vector3(scale.X, scale.Y, scale.Z);

            // D8: the rect write must follow the localPosition/localRotation/localScale writes above
            // — anchoredPosition re-derives m_LocalPosition and must win.
            if (go.transform is RectTransform rt)
            {
                LiveTransformWrite.ApplyRectFields(rt, node.Transform);
            }

            foreach (var c in node.Components)
            {
                var type = ComponentTypeResolver.Resolve(c.Type);
                if (type == null || type == typeof(Transform))
                {
                    continue;
                }

                var comp = ComponentDefaultTemplate.Create(go, type);
                if (comp == null)
                {
                    continue;
                }

                if (c.Fields.Count > 0)
                {
                    var so = new SerializedObject(comp);
                    foreach (var (key, value) in c.Fields)
                    {
                        var path = ResolveProperty(so, key);
                        WriteFieldValue(so, path, value, comp, c.LogicalId, result, map, scene);
                    }

                    so.ApplyModifiedProperties();
                }

                result.ComponentsByLogicalId[c.LogicalId] = comp;
            }

            result.GameObjectsByLogicalId[node.LogicalId] = go;

            foreach (var childNode in node.Children)
            {
                BuildAddedChild(go, childNode, result, map, scene);
            }

            return go;
        }

        // Mirrors PlanExecutor.TrySetTag's Untagged/try-catch guard (private there — not reachable
        // from this type, so replicated at the same, small guard).
        private static void TrySetChildTag(GameObject go, string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag == "Untagged")
            {
                return;
            }

            try { go.tag = tag; }
            catch { Debug.LogWarning($"[SceneBuilder] Tag '{tag}' is not in the Tag Manager; skipped for '{go.name}'."); }
        }

        // ChildLogicalId is "{ownerLogicalId}/.../{childName}" — the leaf segment is the live
        // GameObject's own Name, used as the by-name fallback when the child isn't (yet) captured in
        // result.GameObjectsByLogicalId (e.g. a fresh Execute() call after the id was captured by a
        // PRIOR plan's now-discarded ExecutionResult).
        private static string ChildName(string childLogicalId)
        {
            var slash = childLogicalId.LastIndexOf('/');
            return slash < 0 ? childLogicalId : childLogicalId.Substring(slash + 1);
        }
    }
}
