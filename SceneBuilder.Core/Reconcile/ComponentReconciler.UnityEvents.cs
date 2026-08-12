using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // The scene->code UnityEvent listener source-patch matrix (spec 09:438-461): index-matched
    // delta emission (EmitListenerFieldDelta) and the one per-listener authoring-call renderer
    // (RenderListenerCall) every emit site (in-place patch, ADD, and an appended component's own
    // listener chain) goes through. Split from ComponentReconciler.cs for file-size discipline,
    // mirroring the existing `.Instances`/`.AnchorChain`/`.RectTransform`/`.ScopedOn` partial split.
    internal static partial class ComponentReconciler
    {
        private const string OnClickFieldKey = "m_OnClick";

        // Button.onClick's own real public C# spelling — the ONE case a listener field's public
        // name is known without a MemberSpelling lookup (the `.OnClick(...)` shortcut renders
        // without one; only a DYNAMIC m_OnClick listener, which has no OnClick(...) overload, ever
        // needs it as an OnEvent(...) selector).
        private const string OnClickPublicName = "onClick";

        // Emits the source-patch delta for a differing UnityEvent field once source information
        // (componentHandles + listenerCallSpans) is available: an index-matched delta over
        // `sourceComp.Fields[fieldKey]` (source) vs `snapListeners` (snapshot) —
        // - index present in both: byte-equal listener -> no-op; else classify the snapshot
        //   listener's target (the shipped ClassifyListenerTarget, reused verbatim) and, for a
        //   non-`m_OnClick` field, require a MemberSpelling entry -> emit ONE PatchListenerCall at
        //   that index's own span (never a whole-field overwrite of a sibling index);
        // - index only in the snapshot (past the source's own listener count) -> ONE
        //   AppendListenerCall per such index, in order;
        // - index only in the source (past the snapshot's own listener count) -> ONE
        //   RemoveListenerCall at that index's span.
        // An unresolvable/empty-method/unspellable listener never gets an edit — the existing
        // Conflict.UnsyncableListener report fires instead, and a resolvable SIBLING at a different
        // index in the SAME field still gets its own edit.
        internal static void EmitListenerFieldDelta(
            ComponentData sourceComp,
            ValueNode.UnityEventListeners snapListeners,
            string fieldKey,
            MemberSpellingIndex? snapshotMemberSpellings,
            IReadOnlyDictionary<string, string> componentHandles,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SourceSpan>>> listenerCallSpans,
            ISet<string> resolvableTargets,
            ISet<string> pendingTargets,
            AssetCatalog? assetCatalog,
            List<SourceEdit> edits,
            List<Conflict> conflicts,
            List<AssetEntry> addedAssets)
        {
            var src = sourceComp.Fields.TryGetValue(fieldKey, out var srcFieldVal)
                && srcFieldVal is ValueNode.UnityEventListeners srcListeners
                ? srcListeners.Listeners
                : (IReadOnlyList<UnityEventListener>)Array.Empty<UnityEventListener>();
            var snap = snapListeners.Listeners;

            var spans = listenerCallSpans.TryGetValue(sourceComp.LogicalId, out var byField)
                && byField.TryGetValue(fieldKey, out var fieldSpans)
                ? fieldSpans
                : (IReadOnlyList<SourceSpan>)Array.Empty<SourceSpan>();

            var count = Math.Max(src.Count, snap.Count);
            for (var i = 0; i < count; i++)
            {
                if (i >= snap.Count)
                {
                    // REMOVE: a source listener with no snapshot counterpart at this index.
                    if (i < spans.Count)
                    {
                        edits.Add(new RemoveListenerCall { Anchor = sourceComp.LogicalId, CallSpan = spans[i], FieldKey = fieldKey });
                    }

                    continue;
                }

                var listener = snap[i];
                if (i < src.Count && Equals(src[i], listener))
                {
                    continue;
                }

                var resolution = ClassifyListenerTarget(listener.Target, listener.MethodName, resolvableTargets, pendingTargets);
                if (resolution.Kind == ListenerTargetKind.Unsyncable)
                {
                    conflicts.Add(Conflict.UnsyncableListener(
                        sourceComp.LogicalId, sourceComp.Type.FullName, fieldKey, i, resolution.Reason!.Value));
                    continue;
                }

                if (resolution.Kind == ListenerTargetKind.Pending)
                {
                    // A same-batch new target has no LogicalId and no handle yet — defer with
                    // no edit and no report; it converges on the guaranteed second Sync once
                    // DetectAppends maps it.
                    continue;
                }

                string? memberSpelling = null;
                if (fieldKey != OnClickFieldKey)
                {
                    if (snapshotMemberSpellings == null
                        || !snapshotMemberSpellings.TryGet(sourceComp.Type.FullName, fieldKey, out memberSpelling))
                    {
                        conflicts.Add(Conflict.UnsyncableListener(
                            sourceComp.LogicalId, sourceComp.Type.FullName, fieldKey, i, ListenerReportReason.UnresolvedEventMemberName));
                        continue;
                    }
                }

                var callText = RenderListenerCall(listener, fieldKey, memberSpelling, componentHandles, assetCatalog);

                // The generic per-field harvest (ComponentReconciler.cs's CollectAssetEntries call
                // sites) never sees a listener field — it is intercepted into this method before
                // reaching them (see the m8 comment at ComponentReconciler.cs's field-value-diff
                // loop) — so a project-asset target/arg that is ABOUT to reach source (a Patch or
                // Append below, never a deferred/unsyncable/no-op listener) is harvested here, the
                // one place that sees every listener actually emitted.
                if (listener.Target is not null)
                {
                    CollectAssetEntries(listener.Target, addedAssets);
                }

                if (listener.ArgValue is not null)
                {
                    CollectAssetEntries(listener.ArgValue, addedAssets);
                }

                if (i < src.Count)
                {
                    // In-place CHANGE (target/method/arg/callState) — patched at its OWN span, never
                    // the whole field, so a sibling listener's statement is untouched.
                    if (i < spans.Count)
                    {
                        edits.Add(new PatchListenerCall
                        {
                            Anchor = sourceComp.LogicalId,
                            CallSpan = spans[i],
                            FieldKey = fieldKey,
                            NewExpr = "." + callText,
                        });
                    }
                }
                else
                {
                    // ADD: a snapshot listener past the source's own listener count.
                    edits.Add(new AppendListenerCall { Anchor = sourceComp.LogicalId, FieldKey = fieldKey, CallExpr = callText });
                }
            }
        }

        // Renders one listener as its authoring call text, WITHOUT the leading operator dot
        // (PatchListenerCall prepends it; AppendListenerCall's receiver-less body never carries
        // one). `memberSpelling` is required for every field except `m_OnClick` (the `.OnClick(...)`
        // shortcut needs none; its own dynamic form, which has no OnClick(...) overload, uses the
        // known real public spelling <see cref="OnClickPublicName"/> instead of a lookup).
        internal static string RenderListenerCall(
            UnityEventListener listener,
            string fieldKey,
            string? memberSpelling,
            IReadOnlyDictionary<string, string> componentHandles,
            AssetCatalog? assetCatalog)
        {
            var target = RenderListenerTarget(listener.Target!, componentHandles, assetCatalog);
            var isDynamic = listener.ArgMode == ListenerArgMode.Dynamic;

            if (fieldKey == OnClickFieldKey && !isDynamic)
            {
                return $"OnClick({target}, {RenderMethodLambda(listener)}{RenderCallStateSuffix(listener.CallState)})";
            }

            var spelling = fieldKey == OnClickFieldKey ? OnClickPublicName : memberSpelling;
            var selector = $"x => x.{spelling}";

            return isDynamic
                ? $"OnEvent({selector}, {target}, h => h.{listener.MethodName}, dynamic: true)"
                : $"OnEvent({selector}, {target}, {RenderMethodLambda(listener)}{RenderCallStateSuffix(listener.CallState)})";
        }

        // A listener target is always one already-whole reference (UnityEventListener.ValidateTarget
        // allows only ObjectRef/AssetRef/Unsupported/null; a caller here only ever reaches this with
        // a resolution already classified ResolvableScene/ResolvableAsset by ClassifyListenerTarget,
        // so ObjectRef/AssetRef are the only two shapes reached). Reads an ObjectRef target through
        // ObjectRefValues (never the raw `ValueNode.ObjectRef` token) — mirrors
        // ClassifyListenerTarget's own delegation to the shared reconcile-side ref machinery.
        private static string RenderListenerTarget(
            ValueNode target, IReadOnlyDictionary<string, string> componentHandles, AssetCatalog? assetCatalog)
        {
            if (ObjectRefValues.Contains(target))
            {
                var targetLogicalId = ObjectRefValues.Enumerate(target).First().Ref.TargetLogicalId;
                return targetLogicalId != null && componentHandles.TryGetValue(targetLogicalId, out var handle)
                    ? handle
                    : targetLogicalId ?? "NodeHandle.None";
            }

            if (target is ValueNode.AssetRef(var assetRef))
            {
                return SourceExpr.RenderAssetRef(assetRef, assetCatalog);
            }

            throw new InvalidOperationException(
                $"RenderListenerCall: target must be a resolvable ObjectRef or AssetRef; got {target.GetType().Name}.");
        }

        // `t => t.Method()` / `t => t.Method(<literal>)` for every static-argument mode. Dynamic
        // (a method GROUP, never invoked here) and the target/selector text are the caller's own
        // concern.
        private static string RenderMethodLambda(UnityEventListener listener)
        {
            var call = listener.ArgMode switch
            {
                ListenerArgMode.Void => $"{listener.MethodName}()",
                ListenerArgMode.Int => $"{listener.MethodName}({SourceExpr.IntLiteral((int)((ValueNode.Primitive)listener.ArgValue!).Value!)})",
                ListenerArgMode.Float => $"{listener.MethodName}({SourceExpr.Float((float)((ValueNode.Primitive)listener.ArgValue!).Value!)})",
                ListenerArgMode.String => $"{listener.MethodName}({SourceExpr.StringLiteral((string)((ValueNode.Primitive)listener.ArgValue!).Value!)})",
                ListenerArgMode.Bool => $"{listener.MethodName}({((bool)((ValueNode.Primitive)listener.ArgValue!).Value! ? "true" : "false")})",
                ListenerArgMode.Object => $"{listener.MethodName}({RenderTypedObjectArg(listener.ArgValue!)})",
                _ => throw new InvalidOperationException($"RenderListenerCall: unsupported static ListenerArgMode {listener.ArgMode}."),
            };

            return $"t => t.{call}";
        }

        // An Object-mode static argument renders through the same reference machinery as a
        // listener TARGET, but a bare reference does not bind to the wired method's typed
        // parameter (`AssetReference.As<T>()` / `ComponentRef<T>.As()` are the authoring-only
        // typing scaffolding the parser's own UnwrapTypedRef strips back off on read) — so this
        // appends it back on render. An AssetRef needs its type spelled out (`.As<Material>()`,
        // `AssetReference.As<T>` is generic); an ObjectRef's handle is already a `ComponentRef<T>`,
        // so its `.As()` is parameterless.
        private static string RenderTypedObjectArg(ValueNode argValue)
        {
            var rendered = RenderListenerTarget(argValue, EmptyComponentHandles, null);
            return argValue is ValueNode.AssetRef(var assetRef)
                ? rendered + (string.IsNullOrEmpty(assetRef?.TypeHint) ? ".As<object>()" : $".As<{assetRef!.TypeHint}>()")
                : rendered + ".As()";
        }

        // An Object-mode listener argument that targets a component (rather than an asset) has no
        // handle table of its own to resolve against here; an empty one keeps
        // RenderListenerTarget's signature the one this file uses everywhere, rather than a second
        // parameter threaded through for that one rare shape.
        private static readonly IReadOnlyDictionary<string, string> EmptyComponentHandles = new Dictionary<string, string>();

        private static string RenderCallStateSuffix(ListenerCallState state) =>
            state == ListenerCallState.RuntimeOnly ? "" : $", callState: UnityEngine.Events.UnityEventCallState.{state}";
    }
}
