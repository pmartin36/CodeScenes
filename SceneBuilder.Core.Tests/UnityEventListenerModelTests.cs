using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // ValueNode.UnityEventListeners carries a UnityEvent field's persistent-call list: it
    // round-trips through canonical JSON to an equal value, compares deep and order-significant,
    // rejects an out-of-table Target/ArgValue shape, and is reachable by every ValueWalk-built
    // pass (ObjectRefValues, AssetRefLowering, ComponentReconciler's asset-sidecar harvest) the
    // same way every other container kind already is.
    public class UnityEventListenerModelTests
    {
        private static UnityEventListener Listener(
            ValueNode? target,
            string methodName,
            ListenerArgMode argMode,
            ValueNode? argValue = null,
            ListenerCallState callState = ListenerCallState.RuntimeOnly) =>
            new(target, methodName, argMode, argValue, callState);

        private static ValueNode RoundTrip(ValueNode node) =>
            CanonicalJson.Deserialize<ValueNode>(CanonicalJson.Serialize(node));

        // ---- Canonical JSON + round trip (spec 09's "serialize -> parse -> equal") -------------

        public static IEnumerable<object?[]> ArgModeSamples()
        {
            yield return new object?[] { ListenerArgMode.Void, null };
            yield return new object?[] { ListenerArgMode.Dynamic, null };
            yield return new object?[] { ListenerArgMode.Int, ValueNode.Primitive.Int(7) };
            yield return new object?[] { ListenerArgMode.Float, ValueNode.Primitive.Float(1.5f) };
            yield return new object?[] { ListenerArgMode.String, ValueNode.Primitive.String("hi") };
            yield return new object?[] { ListenerArgMode.Bool, ValueNode.Primitive.Bool(true) };
            yield return new object?[] { ListenerArgMode.Object, new ValueNode.ObjectRef("arg-target") };
        }

        [Theory]
        [MemberData(nameof(ArgModeSamples))]
        public void RoundTrip_EachArgMode_ProducesAnEqualValue(ListenerArgMode argMode, ValueNode? argValue)
        {
            var node = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.ObjectRef("go-1"), "Handler", argMode, argValue),
            });

            Assert.Equal(node, RoundTrip(node));
        }

        [Theory]
        [InlineData(ListenerCallState.Off)]
        [InlineData(ListenerCallState.RuntimeOnly)]
        [InlineData(ListenerCallState.EditorAndRuntime)]
        public void RoundTrip_EachCallState_ProducesAnEqualValue(ListenerCallState callState)
        {
            var node = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.ObjectRef("go-1"), "Handler", ListenerArgMode.Void, callState: callState),
            });

            Assert.Equal(node, RoundTrip(node));
        }

        [Fact]
        public void RoundTrip_NullTarget_ProducesAnEqualValue()
        {
            // spec 09's dangling call (Unity's "Missing" target) — legal, never rejected or pruned.
            var node = new ValueNode.UnityEventListeners(new[]
            {
                Listener(null, "Handler", ListenerArgMode.Void),
            });

            Assert.Equal(node, RoundTrip(node));
        }

        [Fact]
        public void RoundTrip_AssetRefTarget_ProducesAnEqualValue()
        {
            var node = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.AssetRef(new AssetRef { Guid = "abc", FileId = 1 }), "Handler", ListenerArgMode.Void),
            });

            Assert.Equal(node, RoundTrip(node));
        }

        [Fact]
        public void RoundTrip_UnsupportedTarget_ProducesAnEqualValue()
        {
            // The shipped unresolved-reference carrier: ObjectRefLowering and
            // ComponentReconciler.RenderFieldValue both put ValueNode.Unsupported at a reached
            // reference slot, including a listener's Target.
            var node = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.Unsupported("Handle(12345)"), "Handler", ListenerArgMode.Void),
            });

            Assert.Equal(node, RoundTrip(node));
        }

        [Fact]
        public void Serialize_TwoListeners_RepeatedSerializationIsByteIdentical()
        {
            var node = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.ObjectRef("go-1"), "Show", ListenerArgMode.Void),
                Listener(new ValueNode.ObjectRef("go-2"), "SetActive", ListenerArgMode.Bool, ValueNode.Primitive.Bool(true)),
            });

            var json1 = CanonicalJson.Serialize<ValueNode>(node);
            var json2 = CanonicalJson.Serialize<ValueNode>(node);

            Assert.Equal(json1, json2);
        }

        [Fact]
        public void Serialize_Discriminator_IsExactlyUnityEventListeners()
        {
            var node = new ValueNode.UnityEventListeners(Array.Empty<UnityEventListener>());

            var json = CanonicalJson.Serialize<ValueNode>(node);

            Assert.Contains("\"kind\": \"UnityEventListeners\"", json);
        }

        [Fact]
        public void Serialize_ArgValueKey_OmittedForVoidAndDynamic_PresentForOthers()
        {
            var node = new ValueNode.UnityEventListeners(new[]
            {
                Listener(null, "VoidCall", ListenerArgMode.Void),
                Listener(null, "DynamicCall", ListenerArgMode.Dynamic),
                Listener(null, "IntCall", ListenerArgMode.Int, ValueNode.Primitive.Int(3)),
            });

            var json = CanonicalJson.Serialize<ValueNode>(node);

            var voidCallIndex = json.IndexOf("\"VoidCall\"", StringComparison.Ordinal);
            var dynamicCallIndex = json.IndexOf("\"DynamicCall\"", StringComparison.Ordinal);
            var intCallIndex = json.IndexOf("\"IntCall\"", StringComparison.Ordinal);
            Assert.True(voidCallIndex >= 0 && dynamicCallIndex >= 0 && intCallIndex >= 0);

            var voidListenerText = json.Substring(voidCallIndex, dynamicCallIndex - voidCallIndex);
            var dynamicListenerText = json.Substring(dynamicCallIndex, intCallIndex - dynamicCallIndex);
            var intListenerText = json.Substring(intCallIndex);

            Assert.DoesNotContain("\"argValue\"", voidListenerText);
            Assert.DoesNotContain("\"argValue\"", dynamicListenerText);
            Assert.Contains("\"argValue\"", intListenerText);
        }

        // ---- Equality (b): deep, order-significant ---------------------------------------------

        private static ValueNode.UnityEventListeners TwoListenerSample() => new(new[]
        {
            Listener(new ValueNode.ObjectRef("go-1"), "Show", ListenerArgMode.Void),
            Listener(new ValueNode.ObjectRef("go-2"), "SetActive", ListenerArgMode.Bool, ValueNode.Primitive.Bool(true)),
        });

        [Fact]
        public void Equality_StructurallyEqualValues_AreEqual_AndHashEqual()
        {
            var a = TwoListenerSample();
            var b = TwoListenerSample();

            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        // ---- Target slot legality (b3, b2): null | ObjectRef | AssetRef | Unsupported ----------

        public static IEnumerable<object[]> IllegalTargetKinds()
        {
            yield return new object[] { ValueNode.Primitive.Int(1) };
            yield return new object[] { new ValueNode.Nested("Foo", FieldMap.Empty) };
            yield return new object[] { new ValueNode.List(Array.Empty<ValueNode>()) };
            yield return new object[] { new ValueNode.UnityEventListeners(Array.Empty<UnityEventListener>()) };
        }

        [Theory]
        [MemberData(nameof(IllegalTargetKinds))]
        public void Construct_TargetOfAnIllegalKind_Throws(ValueNode illegalTarget)
        {
            Assert.Throws<ArgumentException>(() => new UnityEventListener(illegalTarget, "Handler", ListenerArgMode.Void));
        }

        // ---- ArgValue rules (spec 09:84-86) -----------------------------------------------------

        [Fact]
        public void Construct_VoidModeWithNonNullArgValue_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                new UnityEventListener(null, "Handler", ListenerArgMode.Void, ValueNode.Primitive.Int(1)));
        }

        [Fact]
        public void Construct_DynamicModeWithNonNullArgValue_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                new UnityEventListener(null, "Handler", ListenerArgMode.Dynamic, ValueNode.Primitive.Int(1)));
        }

        [Theory]
        [InlineData(ListenerArgMode.Int)]
        [InlineData(ListenerArgMode.Float)]
        [InlineData(ListenerArgMode.String)]
        [InlineData(ListenerArgMode.Bool)]
        [InlineData(ListenerArgMode.Object)]
        public void Construct_StaticModeWithNullArgValue_Throws(ListenerArgMode argMode)
        {
            Assert.Throws<ArgumentException>(() => new UnityEventListener(null, "Handler", argMode, null));
        }

        [Fact]
        public void Construct_IntModeWithMismatchedPrimitiveKind_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                new UnityEventListener(null, "Handler", ListenerArgMode.Int, ValueNode.Primitive.Float(1f)));
        }

        [Fact]
        public void Construct_ObjectModeWithPrimitiveArgValue_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                new UnityEventListener(null, "Handler", ListenerArgMode.Object, ValueNode.Primitive.Int(1)));
        }

        [Fact]
        public void Construct_NullMethodName_NormalizesToEmptyString()
        {
            var listener = new UnityEventListener(null, null!, ListenerArgMode.Void);

            Assert.Equal("", listener.MethodName);
        }

        // ---- Descent (c): ObjectRefValues reaches a listener's Target and object-mode ArgValue --

        private static ValueNode.UnityEventListeners TwoRefListenerSample() => new(new[]
        {
            Listener(new ValueNode.ObjectRef("target-a"), "Show", ListenerArgMode.Void),
            Listener(null, "Assign", ListenerArgMode.Object, new ValueNode.ObjectRef("target-b")),
        });

        [Fact]
        public void ObjectRefValues_Enumerate_ReachesListenerTargetAndObjectModeArgValue_AtComposedPaths()
        {
            var refs = ObjectRefValues.Enumerate(TwoRefListenerSample()).ToList();

            Assert.Contains(refs, r => r.Path == "[0].Target" && r.Ref.TargetLogicalId == "target-a");
            Assert.Contains(refs, r => r.Path == "[1].ArgValue" && r.Ref.TargetLogicalId == "target-b");
        }

        [Fact]
        public void ObjectRefValues_Enumerate_ReachesAListenerNestedInsideANestedMember()
        {
            var wrapped = new ValueNode.Nested("Foo", new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("evt", TwoRefListenerSample()),
            }));

            var refs = ObjectRefValues.Enumerate(wrapped).ToList();

            Assert.Contains(refs, r => r.Path == ".evt[0].Target" && r.Ref.TargetLogicalId == "target-a");
        }

        [Fact]
        public void ObjectRefValues_Enumerate_ReachesAListenerNestedInsideAList()
        {
            var wrapped = new ValueNode.List(new ValueNode[] { TwoRefListenerSample() });

            var refs = ObjectRefValues.Enumerate(wrapped).ToList();

            Assert.Contains(refs, r => r.Path == "[0][0].Target" && r.Ref.TargetLogicalId == "target-a");
        }

        [Fact]
        public void ObjectRefValues_Targets_IncludesBothListenerSlots()
        {
            var targets = ObjectRefValues.Targets(TwoRefListenerSample()).ToList();

            Assert.Contains("target-a", targets);
            Assert.Contains("target-b", targets);
        }

        [Fact]
        public void ObjectRefValues_Substitute_ReplacesBothSlots_PreservesMethodNameAndOrder()
        {
            var value = TwoRefListenerSample();

            var substituted = (ValueNode.UnityEventListeners)ObjectRefValues.Substitute(
                value, r => new ValueNode.ObjectRef(r.TargetLogicalId + "-mapped"));

            Assert.Equal("Show", substituted.Listeners[0].MethodName);
            Assert.Equal("target-a-mapped", ((ValueNode.ObjectRef)substituted.Listeners[0].Target!).TargetLogicalId);
            Assert.Equal("Assign", substituted.Listeners[1].MethodName);
            Assert.Equal("target-b-mapped", ((ValueNode.ObjectRef)substituted.Listeners[1].ArgValue!).TargetLogicalId);
        }

        // ---- Descent (c2): the shipped asset passes inherit the same descent ------------------

        [Fact]
        public void AssetRefLowering_Lower_ResolvesAnUnresolvedAssetRefCarriedAsAListenerTarget()
        {
            var unresolved = new AssetRef { DisplayPath = "Materials/Foo.mat" };
            var node = new ValueNode.UnityEventListeners(new[]
            {
                Listener(new ValueNode.AssetRef(unresolved), "Handler", ListenerArgMode.Void),
            });
            var component = new ComponentData
            {
                LogicalId = "comp-1",
                Type = new TypeRef("UnityEngine.UI.Button"),
                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_OnClick", node) }),
            };
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode { LogicalId = "go-1", Name = "Root", Components = new[] { component } },
                },
            };

            var lowered = AssetRefLowering.Lower(model, (path, typeHint) => ("resolved-guid", 2L, "Material"));

            var loweredListeners = (ValueNode.UnityEventListeners)lowered.Roots[0].Components[0].Fields["m_OnClick"];
            var loweredTarget = (ValueNode.AssetRef)loweredListeners.Listeners[0].Target!;
            Assert.Equal("resolved-guid", loweredTarget.Ref!.Guid);
        }

        [Fact]
        public void ComponentReconciler_CollectAssetEntries_ReturnsTheEntryForAListenerBorneAsset()
        {
            var resolved = new AssetRef { Guid = "asset-guid-1", DisplayPath = "Materials/Foo.mat", TypeHint = "Material" };
            var node = new ValueNode.UnityEventListeners(new[]
            {
                Listener(null, "SetMaterial", ListenerArgMode.Object, new ValueNode.AssetRef(resolved)),
            });

            var sink = new List<SceneBuilder.Core.Identity.AssetEntry>();
            ComponentReconciler.CollectAssetEntries(node, sink);

            Assert.Contains(sink, e => e.Guid == "asset-guid-1" && e.LastKnownPath == "Materials/Foo.mat");
        }

        // ---- ValueWalk.Fold / Descend route through the listener slots, not the leaf arm -------

        [Fact]
        public void Fold_UnityEventListenersNode_InvokesTheListenersDelegate_NotTheLeafDelegate()
        {
            // No listener has a non-null slot, so leaf has no legitimate child to visit: any
            // call to it means the container itself was misrouted to the leaf arm.
            var node = new ValueNode.UnityEventListeners(new[]
            {
                Listener(null, "Show", ListenerArgMode.Void),
            });
            var listenersDelegateCalled = false;

            ValueWalk.Fold<string>(
                node,
                (listNode, items) => "",
                (nestedNode, members) => "",
                leaf => throw new Xunit.Sdk.XunitException("leaf delegate ran for a UnityEventListeners node"),
                (listenersNode, foldedSlots) => { listenersDelegateCalled = true; return ""; },
                (managedReferenceNode, members) => throw new Xunit.Sdk.XunitException("managedReference delegate ran for a UnityEventListeners node"));

            Assert.True(listenersDelegateCalled, "ValueWalk.Fold did not invoke the listeners delegate for a UnityEventListeners node");
        }

        [Fact]
        public void Fold_UnityEventListenersNode_FoldsSlotsEagerly_BeforeInvokingTheListenersDelegate()
        {
            // The listeners delegate never reads foldedSlots, so this only passes if each
            // non-null slot's leaf ran during the Fold call itself, not on demand.
            var node = TwoRefListenerSample();
            var leafCallCount = 0;

            ValueWalk.Fold<string>(
                node,
                (listNode, items) => "",
                (nestedNode, members) => "",
                leaf => { leafCallCount++; return ""; },
                (listenersNode, foldedSlots) => "",
                (managedReferenceNode, members) => throw new Xunit.Sdk.XunitException("managedReference delegate ran unexpectedly"));

            Assert.Equal(2, leafCallCount);
        }

        [Fact]
        public void Fold_UnityEventListenersNode_FoldsOnlyNonNullSlots_KeyedByCompositePath()
        {
            var node = TwoRefListenerSample(); // [0].Target present, [0].ArgValue absent, [1].Target absent, [1].ArgValue present
            IReadOnlyList<KeyValuePair<string, string>>? foldedSlots = null;

            ValueWalk.Fold<string>(
                node,
                (listNode, items) => "",
                (nestedNode, members) => "",
                leaf => leaf is ValueNode.ObjectRef r ? r.TargetLogicalId! : "",
                (listenersNode, slots) => { foldedSlots = slots; return ""; },
                (managedReferenceNode, members) => throw new Xunit.Sdk.XunitException("managedReference delegate ran unexpectedly"));

            Assert.NotNull(foldedSlots);
            Assert.Equal(
                new[] { "[0].Target", "[1].ArgValue" },
                foldedSlots!.Select(s => s.Key));
        }

        [Fact]
        public void Descend_UnityEventListenersNode_VisitsEachNonNullSlot_WithListenerIndexAndSlotName()
        {
            var node = TwoRefListenerSample();
            var visited = new List<(int Index, string SlotName)>();

            ValueWalk.Descend<int>(
                node,
                0,
                enter: (n, ctx) => ctx,
                item: (listNode, ctx, index) => ctx,
                member: (nestedNode, ctx, key) => ctx,
                listenerSlot: (listenersNode, ctx, index, slotName) => { visited.Add((index, slotName)); return ctx; },
                managedReferenceMember: (managedReferenceNode, ctx, key) => throw new Xunit.Sdk.XunitException("managedReferenceMember delegate ran unexpectedly"));

            Assert.Contains((0, "Target"), visited);
            Assert.Contains((1, "ArgValue"), visited);
        }
    }
}
