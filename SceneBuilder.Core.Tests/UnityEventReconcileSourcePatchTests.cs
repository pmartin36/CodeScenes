using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // The scene->code UnityEvent source-patch matrix (spec 09:438-461) — ADD, an
    // in-place CHANGE (target/method/arg/callState), REMOVE, multiple adds, combined edits on one
    // field, reorder, the `.OnEvent` form, a dynamic method-group listener, and the fail-loud path,
    // all keyed by ARRAY INDEX so a same-position change patches in place and never
    // deletes-and-re-appends (which would reorder the field). The Reconcile_* cases pin the
    // classification+render decision through the top-level entry point; the Apply_* cases pin the
    // applier's per-listener splice — an edit to one listener must never touch a sibling's own
    // statement.
    public class UnityEventReconcileSourcePatchTests
    {
        private const string DoorLogicalId = "root-1/UnityEventReconcileSourcePatchTests.DoorOpener#0";
        private const string AltLogicalId = "root-1/UnityEventReconcileSourcePatchTests.AltOpener#0";
        private const string ButtonLogicalId = "root-1/UnityEngine.UI.Button#0";
        private const string ButtonTypeFullName = "UnityEngine.UI.Button";
        private const string OnValueChangedFieldKey = "m_OnValueChanged";

        private static ComponentData TargetComponent(string logicalId, string typeFullName) => new ComponentData
        {
            LogicalId = logicalId,
            Type = new TypeRef(typeFullName),
            Fields = FieldMap.Empty,
        };

        private static ComponentData ButtonComponent(string fieldKey, IReadOnlyList<UnityEventListener> listeners) => new ComponentData
        {
            LogicalId = ButtonLogicalId,
            Type = new TypeRef(ButtonTypeFullName),
            Fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(fieldKey, new ValueNode.UnityEventListeners(listeners)),
            }),
        };

        private static SceneModel ButtonModel(string fieldKey, params UnityEventListener[] listeners) => new SceneModel
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new GameObjectNode
                {
                    LogicalId = "root-1",
                    Name = "Root",
                    Components = new[]
                    {
                        TargetComponent(DoorLogicalId, "UnityEventReconcileSourcePatchTests.DoorOpener"),
                        TargetComponent(AltLogicalId, "UnityEventReconcileSourcePatchTests.AltOpener"),
                        ButtonComponent(fieldKey, listeners),
                    },
                },
            },
        };

        private static SceneSnapshot ButtonSnapshot(string fieldKey, MemberSpelling[]? memberSpellings, params UnityEventListener[] listeners) => new SceneSnapshot
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new SnapshotNode
                {
                    GlobalObjectId = "goid-root",
                    Name = "Root",
                    Components = new[]
                    {
                        TargetComponent(DoorLogicalId, "UnityEventReconcileSourcePatchTests.DoorOpener"),
                        TargetComponent(AltLogicalId, "UnityEventReconcileSourcePatchTests.AltOpener"),
                        ButtonComponent(fieldKey, listeners),
                    },
                },
            },
            MemberSpellings = memberSpellings ?? Array.Empty<MemberSpelling>(),
        };

        private static IdentityMap Map() => new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                new IdentityMapEntry
                {
                    LogicalId = DoorLogicalId, GlobalObjectId = "", Kind = "Component",
                    ComponentType = "UnityEventReconcileSourcePatchTests.DoorOpener", ParentLogicalId = "root-1",
                },
                new IdentityMapEntry
                {
                    LogicalId = AltLogicalId, GlobalObjectId = "", Kind = "Component",
                    ComponentType = "UnityEventReconcileSourcePatchTests.AltOpener", ParentLogicalId = "root-1",
                },
                new IdentityMapEntry
                {
                    LogicalId = ButtonLogicalId, GlobalObjectId = "", Kind = "Component",
                    ComponentType = ButtonTypeFullName, ParentLogicalId = "root-1",
                },
            },
        };

        // The authored `.Ref<T>()` var name for each component target.
        private static readonly IReadOnlyDictionary<string, string> ComponentHandles = new Dictionary<string, string>
        {
            [DoorLogicalId] = "door",
            [AltLogicalId] = "alt",
        };

        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SourceSpan>>> ListenerSpans(
            string fieldKey, params SourceSpan[] spans) => new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SourceSpan>>>
        {
            [ButtonLogicalId] = new Dictionary<string, IReadOnlyList<SourceSpan>> { [fieldKey] = spans },
        };

        private static UnityEventListener Void(string targetId, string method) =>
            new UnityEventListener(new ValueNode.ObjectRef(targetId), method, ListenerArgMode.Void);

        private static UnityEventListener IntArg(string targetId, string method, int value) =>
            new UnityEventListener(new ValueNode.ObjectRef(targetId), method, ListenerArgMode.Int, ValueNode.Primitive.Int(value));

        private static UnityEventListener FloatArg(string targetId, string method, float value) =>
            new UnityEventListener(new ValueNode.ObjectRef(targetId), method, ListenerArgMode.Float, ValueNode.Primitive.Float(value));

        private static UnityEventListener StringArg(string targetId, string method, string value) =>
            new UnityEventListener(new ValueNode.ObjectRef(targetId), method, ListenerArgMode.String, ValueNode.Primitive.String(value));

        private static UnityEventListener Dynamic(string targetId, string methodGroup) =>
            new UnityEventListener(new ValueNode.ObjectRef(targetId), methodGroup, ListenerArgMode.Dynamic);

        private static UnityEventListener AssetArg(string targetId, string method, AssetRef arg) =>
            new UnityEventListener(new ValueNode.ObjectRef(targetId), method, ListenerArgMode.Object, new ValueNode.AssetRef(arg));

        // ---- Reconcile-level: classification + render, driven through the top-level entry point --

        [Fact]
        public void Reconcile_Add_EmitsAppendListenerCall_PreservesExistingListener()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", Void(DoorLogicalId, "Open")),
                ButtonSnapshot("m_OnClick", null, Void(DoorLogicalId, "Open"), Void(DoorLogicalId, "Close")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10)));

            Assert.Empty(result.Notes);
            Assert.Empty(result.Conflicts);
            var append = Assert.Single(result.Patch.Edits.OfType<AppendListenerCall>());
            Assert.Equal("m_OnClick", append.FieldKey);
            Assert.Matches(@"OnClick\(door,\s*\w+\s*=>\s*\w+\.Close\(\)\)", append.CallExpr);
            Assert.Empty(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Empty(result.Patch.Edits.OfType<RemoveListenerCall>());
        }

        [Fact]
        public void Reconcile_TargetChange_PatchesOnlyThatIndex_NeverRemoveAdd()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", Void(DoorLogicalId, "Open")),
                ButtonSnapshot("m_OnClick", null, Void(AltLogicalId, "Open")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(5, 12)));

            Assert.Empty(result.Notes);
            Assert.Empty(result.Conflicts);
            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Equal(new SourceSpan(5, 12), patch.CallSpan);
            Assert.Equal("m_OnClick", patch.FieldKey);
            Assert.Matches(@"^\.OnClick\(alt,\s*\w+\s*=>\s*\w+\.Open\(\)\)$", patch.NewExpr);
            Assert.Empty(result.Patch.Edits.OfType<AppendListenerCall>());
            Assert.Empty(result.Patch.Edits.OfType<RemoveListenerCall>());
        }

        [Fact]
        public void Reconcile_MethodChange_PatchesInPlace()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", Void(DoorLogicalId, "Open")),
                ButtonSnapshot("m_OnClick", null, Void(DoorLogicalId, "Close")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10)));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Matches(@"door,\s*\w+\s*=>\s*\w+\.Close\(\)", patch.NewExpr);
        }

        [Fact]
        public void Reconcile_ArgChange_PatchesInPlace()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", IntArg(DoorLogicalId, "SetLevel", 3)),
                ButtonSnapshot("m_OnClick", null, IntArg(DoorLogicalId, "SetLevel", 7)),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10)));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Matches(@"door,\s*\w+\s*=>\s*\w+\.SetLevel\(7\)", patch.NewExpr);
            Assert.DoesNotContain("SetLevel(3)", patch.NewExpr);
        }

        [Fact]
        public void Reconcile_CallStateChange_PatchesInPlace_AppendsExplicitCallState()
        {
            var source = new UnityEventListener(
                new ValueNode.ObjectRef(DoorLogicalId), "Open", ListenerArgMode.Void, null, ListenerCallState.RuntimeOnly);
            var snap = new UnityEventListener(
                new ValueNode.ObjectRef(DoorLogicalId), "Open", ListenerArgMode.Void, null, ListenerCallState.EditorAndRuntime);

            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", source),
                ButtonSnapshot("m_OnClick", null, snap),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10)));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Contains("callState: UnityEngine.Events.UnityEventCallState.EditorAndRuntime", patch.NewExpr);
        }

        [Fact]
        public void Reconcile_Remove_DropsMatchingStatement()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", Void(DoorLogicalId, "Open"), Void(DoorLogicalId, "Close")),
                ButtonSnapshot("m_OnClick", null, Void(DoorLogicalId, "Open")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10), new SourceSpan(20, 11)));

            var remove = Assert.Single(result.Patch.Edits.OfType<RemoveListenerCall>());
            Assert.Equal(new SourceSpan(20, 11), remove.CallSpan);
            Assert.Equal("m_OnClick", remove.FieldKey);
            Assert.Empty(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Empty(result.Patch.Edits.OfType<AppendListenerCall>());
        }

        [Fact]
        public void Reconcile_EmptyList_ClearsField_RemovesSoleListener()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", Void(DoorLogicalId, "Open")),
                ButtonSnapshot("m_OnClick", null),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10)));

            var remove = Assert.Single(result.Patch.Edits.OfType<RemoveListenerCall>());
            Assert.Equal(new SourceSpan(0, 10), remove.CallSpan);
        }

        [Fact]
        public void Reconcile_MultipleAdds_AllInsertedInOrder()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", Void(DoorLogicalId, "Open")),
                ButtonSnapshot(
                    "m_OnClick", null, Void(DoorLogicalId, "Open"), Void(DoorLogicalId, "Close"), Void(DoorLogicalId, "Toggle")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10)));

            var appends = result.Patch.Edits.OfType<AppendListenerCall>().ToArray();
            Assert.Equal(2, appends.Length);
            Assert.Matches(@"\.Close\(\)", appends[0].CallExpr);
            Assert.Matches(@"\.Toggle\(\)", appends[1].CallExpr);
            Assert.Empty(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Empty(result.Patch.Edits.OfType<RemoveListenerCall>());
        }

        // Achievable analog of spec case 6b: the ADD region (snap longer than src) and an in-place
        // CHANGE in the overlap both fire for one field in one pass; both edits apply, non-overlapping.
        [Fact]
        public void Reconcile_AddPlusChange_SameField_BothEditsApplyNonOverlapping()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", Void(DoorLogicalId, "Open"), Void(DoorLogicalId, "Close")),
                ButtonSnapshot(
                    "m_OnClick", null, Void(DoorLogicalId, "Open"), Void(AltLogicalId, "Close"), Void(DoorLogicalId, "Toggle")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10), new SourceSpan(20, 11)));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Equal(new SourceSpan(20, 11), patch.CallSpan);
            Assert.Matches(@"alt,\s*\w+\s*=>\s*\w+\.Close\(\)", patch.NewExpr);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendListenerCall>());
            Assert.Matches(@"door,\s*\w+\s*=>\s*\w+\.Toggle\(\)", append.CallExpr);

            Assert.Empty(result.Patch.Edits.OfType<RemoveListenerCall>());
        }

        // Achievable analog of spec case 6a: the REMOVE region (src longer than snap) and an
        // in-place CHANGE in the overlap both fire for one field in one pass; both edits apply,
        // non-overlapping. (A literal ADD+REMOVE on ONE field in ONE pass cannot occur under
        // index-matched delta: the two regions are only ever populated by the SAME length
        // difference, never both — this is the achievable combination the algorithm actually
        // produces.)
        [Fact]
        public void Reconcile_RemovePlusChange_SameField_BothEditsApplyNonOverlapping()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", Void(DoorLogicalId, "Open"), Void(DoorLogicalId, "Close"), Void(DoorLogicalId, "Toggle")),
                ButtonSnapshot("m_OnClick", null, Void(DoorLogicalId, "Open"), Void(AltLogicalId, "Close")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans(
                    "m_OnClick", new SourceSpan(0, 10), new SourceSpan(20, 11), new SourceSpan(40, 12)));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Equal(new SourceSpan(20, 11), patch.CallSpan);
            Assert.Matches(@"alt,\s*\w+\s*=>\s*\w+\.Close\(\)", patch.NewExpr);

            var remove = Assert.Single(result.Patch.Edits.OfType<RemoveListenerCall>());
            Assert.Equal(new SourceSpan(40, 12), remove.CallSpan);

            Assert.Empty(result.Patch.Edits.OfType<AppendListenerCall>());
        }

        [Fact]
        public void Reconcile_Reorder_EmitsTwoInPlacePatches_NeverRemoveAdd()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", Void(DoorLogicalId, "Open"), Void(DoorLogicalId, "Close")),
                ButtonSnapshot("m_OnClick", null, Void(DoorLogicalId, "Close"), Void(DoorLogicalId, "Open")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10), new SourceSpan(20, 11)));

            var patches = result.Patch.Edits.OfType<PatchListenerCall>().ToArray();
            Assert.Equal(2, patches.Length);
            Assert.Equal(new SourceSpan(0, 10), patches[0].CallSpan);
            Assert.Matches(@"\.Close\(\)", patches[0].NewExpr);
            Assert.Equal(new SourceSpan(20, 11), patches[1].CallSpan);
            Assert.Matches(@"\.Open\(\)", patches[1].NewExpr);
            Assert.Empty(result.Patch.Edits.OfType<AppendListenerCall>());
            Assert.Empty(result.Patch.Edits.OfType<RemoveListenerCall>());
        }

        [Fact]
        public void Reconcile_OnEventForm_FullMatrixHolds_NotJustOnClick()
        {
            var spellings = new[]
            {
                new MemberSpelling { Type = new TypeRef(ButtonTypeFullName), SerializedPath = OnValueChangedFieldKey, PublicName = "onValueChanged" },
            };

            var result = Reconciler.Reconcile(
                ButtonModel(OnValueChangedFieldKey, Void(DoorLogicalId, "Open")),
                ButtonSnapshot(OnValueChangedFieldKey, spellings, Void(DoorLogicalId, "Close")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans(OnValueChangedFieldKey, new SourceSpan(0, 10)));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Matches(
                @"^\.OnEvent\(\w+\s*=>\s*\w+\.onValueChanged,\s*door,\s*\w+\s*=>\s*\w+\.Close\(\)\)$", patch.NewExpr);
        }

        [Fact]
        public void Reconcile_DynamicMethodGroup_RoundTrips()
        {
            var spellings = new[]
            {
                new MemberSpelling { Type = new TypeRef(ButtonTypeFullName), SerializedPath = OnValueChangedFieldKey, PublicName = "onValueChanged" },
            };

            var result = Reconciler.Reconcile(
                ButtonModel(OnValueChangedFieldKey, Dynamic(DoorLogicalId, "SetValue")),
                ButtonSnapshot(OnValueChangedFieldKey, spellings, Dynamic(DoorLogicalId, "SetValueAlt")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans(OnValueChangedFieldKey, new SourceSpan(0, 10)));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Matches(
                @"^\.OnEvent\(\w+\s*=>\s*\w+\.onValueChanged,\s*door,\s*\w+\s*=>\s*\w+\.SetValueAlt,\s*dynamic:\s*true\)$",
                patch.NewExpr);
            Assert.DoesNotContain("SetValueAlt(", patch.NewExpr);
        }

        // ---- Emitted-source bind proofs -------------------------------------------------------
        // Splices the ACTUAL rendered call text a Reconcile pass produces into a builder scene and
        // binds it against the real com.codescenes/Runtime authoring surface via
        // AuthoringBindHarness -- a regex/parse-only assertion on the string cannot see a type
        // mismatch between an emitted handle token and the parameter it is assigned to; only a
        // full compilation can.

        private static string EmbedPatchedOnClick(string newExprWithLeadingDot) => $@"using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
using UnityEngine;
public class EmittedListenerPatchScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        var door = scene.Add(""Door"").Component<DoorOpener>(_ => {{ }}).Ref<DoorOpener>();
        scene.Add(""QuitButton"").Component<UnityEngine.UI.Button>(b => b{newExprWithLeadingDot});
    }}
}}";

        [Fact]
        public void Reconcile_OnEventSelectorForm_EmittedSourceCompilesAgainstRealAuthoringSurface()
        {
            var spellings = new[]
            {
                new MemberSpelling { Type = new TypeRef(ButtonTypeFullName), SerializedPath = OnValueChangedFieldKey, PublicName = "onValueChanged" },
            };

            var result = Reconciler.Reconcile(
                ButtonModel(OnValueChangedFieldKey, Void(DoorLogicalId, "Open")),
                ButtonSnapshot(OnValueChangedFieldKey, spellings, Void(DoorLogicalId, "Close")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans(OnValueChangedFieldKey, new SourceSpan(0, 10)));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());

            var errors = AuthoringBindHarness.BindErrors(EmbedPatchedOnClick(patch.NewExpr), AuthoringBindHarness.UnityEventStubs);
            Assert.Empty(errors);
        }

        [Fact]
        public void Reconcile_DynamicMethodGroupForm_EmittedSourceCompilesAgainstRealAuthoringSurface()
        {
            var spellings = new[]
            {
                new MemberSpelling { Type = new TypeRef(ButtonTypeFullName), SerializedPath = OnValueChangedFieldKey, PublicName = "onValueChanged" },
            };

            var result = Reconciler.Reconcile(
                ButtonModel(OnValueChangedFieldKey, Dynamic(DoorLogicalId, "SetValue")),
                ButtonSnapshot(OnValueChangedFieldKey, spellings, Dynamic(DoorLogicalId, "SetValueAlt")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans(OnValueChangedFieldKey, new SourceSpan(0, 10)));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());

            var errors = AuthoringBindHarness.BindErrors(EmbedPatchedOnClick(patch.NewExpr), AuthoringBindHarness.UnityEventStubs);
            Assert.Empty(errors);
        }

        [Fact]
        public void Reconcile_IntArgForm_EmittedSourceCompilesAgainstRealAuthoringSurface()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", IntArg(DoorLogicalId, "SetLevel", 3)),
                ButtonSnapshot("m_OnClick", null, IntArg(DoorLogicalId, "SetLevel", 7)),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10)));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());

            var errors = AuthoringBindHarness.BindErrors(EmbedPatchedOnClick(patch.NewExpr), AuthoringBindHarness.UnityEventStubs);
            Assert.Empty(errors);
        }

        [Fact]
        public void Reconcile_FloatArgForm_EmittedSourceCompilesAgainstRealAuthoringSurface()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", FloatArg(DoorLogicalId, "SetSpeed", 1f)),
                ButtonSnapshot("m_OnClick", null, FloatArg(DoorLogicalId, "SetSpeed", 2.5f)),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10)));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());

            var errors = AuthoringBindHarness.BindErrors(EmbedPatchedOnClick(patch.NewExpr), AuthoringBindHarness.UnityEventStubs);
            Assert.Empty(errors);
        }

        [Fact]
        public void Reconcile_StringArgForm_EmittedSourceCompilesAgainstRealAuthoringSurface()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", StringArg(DoorLogicalId, "SetLabel", "go")),
                ButtonSnapshot("m_OnClick", null, StringArg(DoorLogicalId, "SetLabel", "stop")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10)));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());

            var errors = AuthoringBindHarness.BindErrors(EmbedPatchedOnClick(patch.NewExpr), AuthoringBindHarness.UnityEventStubs);
            Assert.Empty(errors);
        }

        [Fact]
        public void Reconcile_ObjectModeAssetArg_EmitsAsTypedRender_CompilesAgainstAuthoringSurface()
        {
            var asset = new AssetRef { Guid = "matguid", FileId = 0, DisplayPath = "Assets/Foo.mat", TypeHint = "Material" };

            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", Void(DoorLogicalId, "Open")),
                ButtonSnapshot("m_OnClick", null, AssetArg(DoorLogicalId, "SetMaterial", asset)),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10)));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Contains("SetMaterial(Asset(\"Assets/Foo.mat\").As<Material>())", patch.NewExpr);

            var errors = AuthoringBindHarness.BindErrors(EmbedPatchedOnClick(patch.NewExpr), AuthoringBindHarness.UnityEventStubs);
            Assert.Empty(errors);
        }

        [Fact]
        public void Reconcile_UnresolvableTargetSibling_ReportsUnsyncable_StillPatchesResolvableListener()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", Void(DoorLogicalId, "Open")),
                ButtonSnapshot("m_OnClick", null, Void(DoorLogicalId, "Close"), Void("root-1/Unknown#0", "Foo")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10)));

            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnsyncableListener, note.Kind);
            Assert.Contains("could not be resolved", note.Reason);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Equal(new SourceSpan(0, 10), patch.CallSpan);
            Assert.Matches(@"door,\s*\w+\s*=>\s*\w+\.Close\(\)", patch.NewExpr);
            Assert.Empty(result.Patch.Edits.OfType<AppendListenerCall>());
        }

        [Fact]
        public void Reconcile_EmptyMethodNameSibling_ReportsUnsyncable_StillPatchesResolvableListener()
        {
            var result = Reconciler.Reconcile(
                ButtonModel("m_OnClick", Void(DoorLogicalId, "Open")),
                ButtonSnapshot(
                    "m_OnClick", null,
                    Void(DoorLogicalId, "Close"),
                    new UnityEventListener(new ValueNode.ObjectRef(DoorLogicalId), "", ListenerArgMode.Void)),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans("m_OnClick", new SourceSpan(0, 10)));

            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnsyncableListener, note.Kind);
            Assert.Contains("no method name", note.Reason);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchListenerCall>());
            Assert.Equal(new SourceSpan(0, 10), patch.CallSpan);
        }

        [Fact]
        public void Reconcile_OnEventFieldMissingMemberSpelling_ReportsUnresolvedEventMemberName_NoPatch()
        {
            // A sibling spellable entry (m_OnValueChanged) is present in the index, proving the
            // lookup ran; the wired field (m_PrivateEvent) has no entry of its own, so it is the
            // ONLY thing that should surface UnsyncableListener.
            const string UnspellableFieldKey = "m_PrivateEvent";
            var spellings = new[]
            {
                new MemberSpelling { Type = new TypeRef(ButtonTypeFullName), SerializedPath = OnValueChangedFieldKey, PublicName = "onValueChanged" },
            };

            var result = Reconciler.Reconcile(
                ButtonModel(UnspellableFieldKey, Void(DoorLogicalId, "Open")),
                ButtonSnapshot(UnspellableFieldKey, spellings, Void(DoorLogicalId, "Close")),
                Map(),
                componentHandles: ComponentHandles,
                listenerCallSpans: ListenerSpans(UnspellableFieldKey, new SourceSpan(0, 10)));

            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnsyncableListener, note.Kind);
            Assert.Contains("no public C# spelling", note.Reason);
            Assert.Empty(result.Patch.Edits);
        }

        // ---- Applier-level: the exact per-listener splice -----------------------------------------
        // Pins the regression a prior attempt shipped: an ADD/PATCH/REMOVE on one listener must
        // never re-render or delete a SIBLING listener's own statement (never a whole-field
        // overwrite).

        private const string OneListenerSource = @"
public class UnityEventSourcePatchApplyAddScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Door"").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();
        scene.Add(""QuitButton"").Component<UnityEngine.UI.Button>(b => b.OnClick(opener, o => o.Open()));
    }
}
";

        private const string TwoListenerSource = @"
public class UnityEventSourcePatchApplyTwoScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Door"").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();
        scene.Add(""QuitButton"").Component<UnityEngine.UI.Button>(b =>
        {
            b.OnClick(opener, o => o.Open());
            b.OnClick(opener, o => o.Close());
        });
    }
}
";

        private static string ButtonComponentLogicalId(ParseResult parsed) =>
            parsed.Model.Roots.Single(r => r.Name == "QuitButton").Components.Single().LogicalId;

        [Fact]
        public void Apply_AppendListenerCall_InsertsSecondStatement_PreservesFirstByteIdentical()
        {
            var parsed = BuilderParser.Parse(OneListenerSource);
            var componentLogicalId = ButtonComponentLogicalId(parsed);

            var edit = new AppendListenerCall
            {
                Anchor = componentLogicalId,
                FieldKey = "m_OnClick",
                CallExpr = "OnClick(opener, o => o.Close())",
            };

            var patched = SourcePatchApplier.Apply(
                OneListenerSource, new SourcePatch { Edits = new SourceEdit[] { edit } }, SourcePatchTestHelpers.MergeAnchors(parsed));

            Assert.Contains(".OnClick(opener, o => o.Open())", patched);
            Assert.Contains("OnClick(opener, o => o.Close())", patched);
            Assert.Equal(2, Regex.Matches(patched, "OnClick\\(").Count);
            SourcePatchTestHelpers.AssertNoSyntaxErrors(patched);
        }

        [Fact]
        public void Apply_PatchListenerCall_PatchesOnlyItsOwnSpan_SiblingByteIdentical()
        {
            var parsed = BuilderParser.Parse(TwoListenerSource);
            var componentLogicalId = ButtonComponentLogicalId(parsed);
            var firstCallSpan = parsed.FieldArgumentSpans[componentLogicalId]["m_OnClick"];

            var edit = new PatchListenerCall
            {
                Anchor = componentLogicalId,
                FieldKey = "m_OnClick",
                CallSpan = firstCallSpan,
                NewExpr = ".OnClick(opener, o => o.Toggle())",
            };

            var patched = SourcePatchApplier.Apply(
                TwoListenerSource, new SourcePatch { Edits = new SourceEdit[] { edit } }, SourcePatchTestHelpers.MergeAnchors(parsed));

            Assert.Contains(".OnClick(opener, o => o.Toggle())", patched);
            Assert.Contains(".OnClick(opener, o => o.Close())", patched);
            Assert.DoesNotContain("o.Open()", patched);
            SourcePatchTestHelpers.AssertNoSyntaxErrors(patched);
        }

        [Fact]
        public void Apply_RemoveListenerCall_DropsOnlyThatStatement_KeepsSibling()
        {
            var parsed = BuilderParser.Parse(TwoListenerSource);
            var componentLogicalId = ButtonComponentLogicalId(parsed);
            var firstCallSpan = parsed.FieldArgumentSpans[componentLogicalId]["m_OnClick"];

            var edit = new RemoveListenerCall
            {
                Anchor = componentLogicalId,
                FieldKey = "m_OnClick",
                CallSpan = firstCallSpan,
            };

            var patched = SourcePatchApplier.Apply(
                TwoListenerSource, new SourcePatch { Edits = new SourceEdit[] { edit } }, SourcePatchTestHelpers.MergeAnchors(parsed));

            Assert.Contains(".OnClick(opener, o => o.Close())", patched);
            Assert.DoesNotContain("o.Open()", patched);
            Assert.Equal(1, Regex.Matches(patched, "OnClick\\(").Count);
            SourcePatchTestHelpers.AssertNoSyntaxErrors(patched);
        }
    }
}
